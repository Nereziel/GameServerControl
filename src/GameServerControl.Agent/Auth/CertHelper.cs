using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GameServerControl.Agent.Auth;

public static class CertHelper
{
    /// <summary>
    /// Loads <paramref name="pfxPath"/> if present, otherwise mints a fresh self-signed
    /// cert valid for 5 years and persists it to disk. SANs are derived from the current
    /// machine instead of assuming a specific Tailnet address.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath, string pfxPassword, string[] extraSanHosts, IPAddress[] extraSanIps)
    {
        if (File.Exists(pfxPath))
            return new X509Certificate2(pfxPath, pfxPassword, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=GameServerControl Agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));

        var san = new SubjectAlternativeNameBuilder();
        var hostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        if (!string.IsNullOrWhiteSpace(Environment.MachineName)) hostNames.Add(Environment.MachineName);
        try
        {
            var dnsHost = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(dnsHost)) hostNames.Add(dnsHost);
        }
        catch { /* best effort */ }
        foreach (var h in extraSanHosts.Where(h => !string.IsNullOrWhiteSpace(h))) hostNames.Add(h.Trim());
        foreach (var h in hostNames) san.AddDnsName(h);

        var localIps = GetLocalAddresses();
        localIps.Add(IPAddress.Loopback);
        localIps.Add(IPAddress.IPv6Loopback);

        // Keep compatibility with callers that provide explicit SAN IPs, but only include
        // addresses actually assigned to this host. This prevents stale/hard-coded Tailnet
        // addresses from leaking into a newly generated certificate.
        foreach (var ip in extraSanIps)
            if (IsLocalAddress(ip)) localIps.Add(ip);

        foreach (var ip in localIps) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var bytes = cert.Export(X509ContentType.Pfx, pfxPassword);
        File.WriteAllBytes(pfxPath, bytes);
        return new X509Certificate2(bytes, pfxPassword, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    private static HashSet<IPAddress> GetLocalAddresses()
    {
        var result = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (IPAddress.IsLoopback(ip)) continue;
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal) continue;
                    result.Add(ip);
                }
            }
        }
        catch { /* best effort */ }
        return result;
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        return GetLocalAddresses().Contains(address);
    }
}
