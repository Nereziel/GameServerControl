# Nginx HTTPS gateway for Cloudflare-managed GameServerControl Agents

This is an **alternative** to the Caddy example. It forwards administration requests from a Cloudflare Worker to fixed PC Tailscale addresses, not game traffic. Use it if the VPS already runs Nginx; **do not install Caddy or replace existing virtual hosts**.

The template is [`nginx-gateway.conf.example`](../deploy/relay/admin-gateway/nginx-gateway.conf.example). It contains **no real domain names, Tailscale addresses, credentials or Cloudflare identifiers**.

## Prerequisites

- A VPS with an existing Nginx installation, and Tailscale connectivity to every PC Agent.
- A **dedicated DNS hostname**, resolving to the VPS public IP, separate from the Cloudflare Worker panel. DNS-only is simplest for this endpoint; the VPS must present a publicly trusted HTTPS certificate.
- A trusted TLS certificate for that hostname, issued with the VPS's existing ACME / Certbot setup **before enabling the HTTPS server block**. Do not overwrite or stop existing sites to obtain a certificate.
- On PC1 and PC2, configure `Agent:BindTailscale` to their actual Tailscale IPv4 address on port 5099; allow inbound TCP 5099 **only from the VPS Tailscale IPv4 address**. Keep the local UI on `127.0.0.1:5099`.
- Managed `Admin` and `ReadOnly` tokens from each Agent's local UI; these are registered later through Cloudflare and are never placed in the Nginx config.

## Install alongside existing Nginx sites

1. Confirm the active proxy: `systemctl is-active nginx` and `sudo ss -ltnp | grep -E ':80 |:443 '`. Back up the existing Nginx site configuration. No new database or WebUI runs on the VPS.

2. Create a **root-owned secret include file on the VPS**, not in this repository. A 64-character hexadecimal secret is safe to embed inside the generated Nginx directive:

   ```bash
   sudo install -d -m 0700 /etc/nginx/gsc-admin-gateway
   key="$(openssl rand -hex 32)"
   printf 'set $gsc_gateway_key "%s";\n' "$key" |
     sudo tee /etc/nginx/gsc-admin-gateway/secret.conf >/dev/null
   unset key
   sudo chown root:root /etc/nginx/gsc-admin-gateway/secret.conf
   sudo chmod 0600 /etc/nginx/gsc-admin-gateway/secret.conf
   ```

   **Important:** Save the generated 64-character value as the private GitHub Actions secret `GSC_CF_GATEWAY_SHARED_KEY` (deployed to the Worker as `GATEWAY_SHARED_KEY`). The Worker and Nginx must use the same value. Do not put it into Git, chat, an access log, or a public issue. Because the include file is required, Nginx fails `nginx -t` if it is missing. Do not execute `nginx -T` into shared logs: it prints the included secret.

3. Copy the example to its own Nginx virtual host file (Debian/Ubuntu example):

   ```bash
   sudo cp deploy/relay/admin-gateway/nginx-gateway.conf.example \
     /etc/nginx/sites-available/gsc-admin-gateway
   sudoedit /etc/nginx/sites-available/gsc-admin-gateway
   ```

   Replace exactly these placeholders **on the VPS only**:
   - `GSC_GATEWAY_HOSTNAME` → the dedicated public VPS hostname (used by `server_name` and certificate paths).
   - `PC1_TAILSCALE_IPV4` → PC1 Agent's actual Tailscale IPv4 address.
   - `PC2_TAILSCALE_IPV4` → PC2 Agent's actual Tailscale IPv4 address.

   Use the TLS certificate paths from your existing ACME setup if they differ. This template contains a `map` directive and must be included inside Nginx's `http` context (as Debian/Ubuntu `sites-enabled` normally is), **not inside another `server` block**. If your distro uses `conf.d`, place the file there instead.

4. Check that no other enabled site already declares the **same gateway hostname** on port 443. Do not modify any other virtual hosts, TCP/UDP game Relay listeners, or Tailscale port forwarding.

   ```bash
   sudo ln -s /etc/nginx/sites-available/gsc-admin-gateway \
     /etc/nginx/sites-enabled/gsc-admin-gateway
   sudo nginx -t
   sudo systemctl reload nginx
   ```

   If `nginx -t` fails, **do not reload**. Check that the certificate exists, the secret include has been created, and the placeholders have been replaced. The configuration intentionally does not work as a public gateway before these prerequisites are met.

## Security, routes, and tests

The Nginx configuration enforces the header `X-GSC-Proxy-Key` **before any routing**, including WebSocket upgrades. It strips the gateway key and `CF-Access-Jwt-Assertion` before forwarding to the Agent, while forwarding the Agent bearer `Authorization` token. It supports WebSocket / SignalR using HTTP/1.1 Upgrade and long timeouts. Request buffering is disabled to avoid unnecessary staging on a low-RAM VPS; the upload limit is 512 MiB and can be adjusted if needed.

Only the following routes exist:

| Incoming path | Destination |
|---|---|
| `/_gsc_gateway/health` | Replies `ok` with valid shared key |
| `/gsc-agent/pc1/...` | PC1 on Tailscale TCP 5099; strips `/gsc-agent/pc1/` |
| `/gsc-agent/pc2/...` | PC2 on Tailscale TCP 5099; strips `/gsc-agent/pc2/` |
| Everything else | HTTP 403 |

Use the same stable host IDs (`pc1`, `pc2`) when adding hosts in the Cloudflare WebUI. A new PC requires its own fixed upstream route; never accept client-provided upstream IPs.

Verify from a machine that can reach the VPS, replacing the **example** hostname with the actual one **locally**:

```bash
# Must be HTTP 403 when the shared key is missing:
curl -i https://gateway.example.com/_gsc_gateway/health

# From the VPS: supply the private header through stdin, not the curl command line.
sudo cut -d '"' -f2 /etc/nginx/gsc-admin-gateway/secret.conf |
  awk '{print "header = \"X-GSC-Proxy-Key: " $0 "\""}' |
  curl --config - -i https://gateway.example.com/_gsc_gateway/health
# Must be HTTP 200 and body: ok.
```

Keep TCP 5099 closed on the public Internet; the PC firewall must permit **only the VPS Tailscale IP**. Do not enable debug logs or include HTTP authorization or gateway headers in access logs; gateway access logging is disabled by default.

Once both health checks pass, proceed with [Cloudflare Worker deployment](cloudflare-remote-management.md) using its GitHub Actions secrets and workflow inputs. The deployment workflow checks both `200` with the key and `403` without it. The VPS remains a forwarding-only component; Cloudflare D1 owns the host registry and the Worker owns the WebUI/authentication.
