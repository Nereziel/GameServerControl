interface Env {
  ASSETS: Fetcher;
  GATEWAY_BASE_URL: string;
  GATEWAY_SECRET: string;
  WINDOWS_AGENT_TOKEN: string;
}

const API_PREFIX = "/api/";
const ALLOWED_METHODS = new Set(["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD"]);

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store"
    }
  });
}

function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/+$/, "");
}

function routeApi(pathname: string): { node: "windows"; upstreamPath: string; token: string } | null {
  const match = pathname.match(/^\/api\/(windows)(\/.*)?$/i);
  if (!match) return null;

  const node = match[1].toLowerCase() as "windows";
  const rest = match[2] || "/";
  return {
    node,
    upstreamPath: "/api" + rest,
    token: "WINDOWS_AGENT_TOKEN"
  };
}

function tokenFor(env: Env, tokenName: string): string {
  if (tokenName === "WINDOWS_AGENT_TOKEN") return env.WINDOWS_AGENT_TOKEN;
  throw new Error("Unknown agent token binding");
}

async function proxyApi(request: Request, env: Env): Promise<Response> {
  if (!ALLOWED_METHODS.has(request.method))
    return json({ error: "Method not allowed" }, 405);

  const incoming = new URL(request.url);
  const route = routeApi(incoming.pathname);
  if (!route)
    return json({ error: "Unknown API node. Expected /api/windows/..." }, 404);

  const base = normalizeBaseUrl(env.GATEWAY_BASE_URL || "");
  if (!base || !env.GATEWAY_SECRET || !tokenFor(env, route.token))
    return json({ error: "Worker gateway is not configured" }, 503);

  // The VPS gateway receives /windows/api/... and then forwards /api/... to the
  // appropriate GSC agent over Tailscale.
  const target = new URL(base + "/" + route.node + route.upstreamPath);
  target.search = incoming.search;

  const headers = new Headers();
  const contentType = request.headers.get("content-type");
  const accept = request.headers.get("accept");
  if (contentType) headers.set("content-type", contentType);
  if (accept) headers.set("accept", accept);

  // GSC credentials never reach browser JavaScript. They exist only as encrypted
  // Worker secrets and are injected into the server-to-server request here.
  headers.set("authorization", `Bearer ${tokenFor(env, route.token)}`);
  headers.set("x-gsc-gateway-secret", env.GATEWAY_SECRET);
  headers.set("x-gsc-node", route.node);

  const init: RequestInit = {
    method: request.method,
    headers,
    redirect: "manual"
  };
  if (request.method !== "GET" && request.method !== "HEAD")
    init.body = request.body;

  try {
    const upstream = await fetch(target.toString(), init);
    const responseHeaders = new Headers(upstream.headers);
    responseHeaders.set("cache-control", "no-store");
    responseHeaders.delete("set-cookie");
    responseHeaders.delete("www-authenticate");

    return new Response(upstream.body, {
      status: upstream.status,
      statusText: upstream.statusText,
      headers: responseHeaders
    });
  } catch (error) {
    console.error("GSC gateway request failed", error);
    return json({ error: "Gateway unavailable" }, 502);
  }
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname.startsWith(API_PREFIX))
      return proxyApi(request, env);

    return env.ASSETS.fetch(request);
  }
};
