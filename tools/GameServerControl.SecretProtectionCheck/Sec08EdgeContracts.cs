using System.Runtime.CompilerServices;
using GameServerControl.Agent.Security;

internal static class Sec08EdgeContracts
{
    [ModuleInitializer]
    internal static void Validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-sec08-prefix-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            const string prefixLikePlaintext = "enc:v1:user-selected-plaintext-password";
            var protector = new SecretProtector(root);
            var encrypted = protector.Protect(prefixLikePlaintext);
            if (encrypted == prefixLikePlaintext || !protector.IsProtected(encrypted))
                throw new InvalidOperationException("Runtime plaintext beginning with enc:v1: was mistaken for a stored envelope.");
            if (protector.Unprotect(encrypted) != prefixLikePlaintext)
                throw new InvalidOperationException("Prefix-like runtime plaintext did not round-trip through secret protection.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        const string preserveLoop = "appsettings.json servers.json tokens.json public-relay.json secret-protection.key";
        foreach (var installer in new[] { "deploy/linux/install-from-release.sh", "deploy/linux/install.sh" })
        {
            var source = File.ReadAllText(installer);
            if (!source.Contains(preserveLoop, StringComparison.Ordinal))
                throw new InvalidOperationException($"{installer} does not preserve the Linux secret-protection key with encrypted Agent state.");
        }
    }
}
