using System.Security.Cryptography;
using System.Text;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Security;

public sealed class SecretProtector
{
    private const string Prefix = "enc:v1:";
    private const string DpapiPrefix = Prefix + "dpapi:";
    private const string AesPrefix = Prefix + "aesgcm:";
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("GameServerControl:Secret:v1");
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _keyPath;
    private readonly object _keyGate = new();
    private byte[]? _unixKey;

    public SecretProtector(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("Secret data directory is required.", nameof(dataDir));
        var fullDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(fullDir);
        _keyPath = Path.Combine(fullDir, "secret-protection.key");
        if (!OperatingSystem.IsWindows()) SecretFilePermissions.RestrictExisting(_keyPath);
    }

    public bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypts runtime plaintext. Callers handling existing storage must check IsProtected first.</summary>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0) return plaintext;
        return OperatingSystem.IsWindows() ? ProtectWindows(plaintext) : ProtectUnix(plaintext);
    }

    public string Unprotect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsProtected(value)) return value;
        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return UnprotectWindows(value[DpapiPrefix.Length..]);
        if (value.StartsWith(AesPrefix, StringComparison.Ordinal)) return UnprotectUnix(value[AesPrefix.Length..]);
        throw new CryptographicException("Unsupported secret protection envelope.");
    }

    private static string ProtectWindows(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI protection is available only on Windows.");
        var clear = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, AssociatedData, DataProtectionScope.LocalMachine);
            return DpapiPrefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static string UnprotectWindows(string payload)
    {
        if (!OperatingSystem.IsWindows())
            throw new CryptographicException("Windows DPAPI secret cannot be decrypted on this platform.");
        try
        {
            var protectedBytes = Convert.FromBase64String(payload);
            var clear = ProtectedData.Unprotect(protectedBytes, AssociatedData, DataProtectionScope.LocalMachine);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (FormatException ex) { throw new CryptographicException("Invalid DPAPI secret envelope.", ex); }
    }

    private string ProtectUnix(string plaintext)
    {
        var key = GetOrCreateUnixKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var clear = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, clear, cipher, tag, AssociatedData);
            var payload = new byte[nonce.Length + tag.Length + cipher.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
            Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
            return AesPrefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private string UnprotectUnix(string payloadText)
    {
        if (OperatingSystem.IsWindows())
            throw new CryptographicException("Unix AES-GCM secret cannot be decrypted on Windows.");
        byte[] payload;
        try { payload = Convert.FromBase64String(payloadText); }
        catch (FormatException ex) { throw new CryptographicException("Invalid AES-GCM secret envelope.", ex); }
        if (payload.Length < 29) throw new CryptographicException("Invalid AES-GCM secret envelope length.");

        var key = GetOrCreateUnixKey();
        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var cipher = payload.AsSpan(28);
        var clear = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, clear, AssociatedData);
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private byte[] GetOrCreateUnixKey()
    {
        lock (_keyGate)
        {
            if (_unixKey is not null) return _unixKey;
            if (File.Exists(_keyPath)) return _unixKey = ReadUnixKey();

            var created = RandomNumberGenerator.GetBytes(32);
            try
            {
                using var stream = new FileStream(_keyPath, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    UnixCreateMode = PrivateFileMode
                });
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(Convert.ToBase64String(created));
                writer.Flush();
                SecretFilePermissions.RestrictExisting(_keyPath);
                _unixKey = created;
                return _unixKey;
            }
            catch (IOException) when (File.Exists(_keyPath))
            {
                CryptographicOperations.ZeroMemory(created);
                return _unixKey = ReadUnixKey();
            }
        }
    }

    private byte[] ReadUnixKey()
    {
        SecretFilePermissions.RestrictExisting(_keyPath);
        byte[] existing;
        try { existing = Convert.FromBase64String(File.ReadAllText(_keyPath).Trim()); }
        catch (FormatException ex) { throw new CryptographicException("Invalid secret-protection.key contents.", ex); }
        if (existing.Length != 32) throw new CryptographicException("secret-protection.key must contain exactly 32 bytes.");
        return existing;
    }
}
