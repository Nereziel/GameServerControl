using System.Security.Cryptography;
using System.Text;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Security;

/// <summary>
/// Protects reusable Agent secrets with a versioned AES-GCM envelope. The random
/// 256-bit master key is DPAPI LocalMachine wrapped on Windows and stored in the
/// already-restricted Agent state directory on non-Windows platforms.
/// </summary>
public sealed class SecretProtector
{
    public const string ValuePrefix = "gsc-secret-v1:";
    private const string DpapiKeyPrefix = "gsc-key-v1:dpapi:";
    private const string RawKeyPrefix = "gsc-key-v1:raw:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] DpapiEntropy = SHA256.HashData(Encoding.UTF8.GetBytes("GameServerControl.SecretProtector.v1"));

    private readonly byte[] _key;
    public string KeyPath { get; }

    public SecretProtector(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Secret state base directory is required.", nameof(baseDirectory));

        var fullBase = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(fullBase);
        KeyPath = Path.Combine(fullBase, "secret.key");
        _key = LoadOrCreateKey(KeyPath);
    }

    public static bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(ValuePrefix, StringComparison.Ordinal);

    public string Protect(string plaintext, string purpose)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        ValidatePurpose(purpose);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        var associatedData = PurposeBytes(purpose);

        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plainBytes, cipher, tag, associatedData);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, payload, NonceSize + TagSize, cipher.Length);
        CryptographicOperations.ZeroMemory(plainBytes);
        return ValuePrefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        if (!IsProtected(protectedValue))
            throw new CryptographicException("Value is not a supported GameServerControl secret envelope.");
        ValidatePurpose(purpose);

        byte[] payload;
        try { payload = Convert.FromBase64String(protectedValue[ValuePrefix.Length..]); }
        catch (FormatException ex) { throw new CryptographicException("Secret envelope payload is not valid base64.", ex); }
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("Secret envelope payload is truncated.");

        var cipherLength = payload.Length - NonceSize - TagSize;
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize, cipherLength);
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, PurposeBytes(purpose));
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] PurposeBytes(string purpose)
        => Encoding.UTF8.GetBytes("GameServerControl.Secret.v1:" + purpose);

    private static void ValidatePurpose(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("Secret purpose is required.", nameof(purpose));
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            SecretFilePermissions.RestrictExisting(path);
            var existing = File.ReadAllText(path).Trim();
            return ParseExistingKey(existing);
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        string envelope;
        if (OperatingSystem.IsWindows())
        {
            var wrapped = ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.LocalMachine);
            envelope = DpapiKeyPrefix + Convert.ToBase64String(wrapped);
        }
        else
        {
            envelope = RawKeyPrefix + Convert.ToBase64String(key);
        }

        try
        {
            SecretFilePermissions.WriteAllTextAtomic(path, envelope);
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static byte[] ParseExistingKey(string envelope)
    {
        try
        {
            byte[] key;
            if (OperatingSystem.IsWindows())
            {
                if (!envelope.StartsWith(DpapiKeyPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Existing Windows secret.key has an unsupported or non-DPAPI format.");
                var wrapped = Convert.FromBase64String(envelope[DpapiKeyPrefix.Length..]);
                key = ProtectedData.Unprotect(wrapped, DpapiEntropy, DataProtectionScope.LocalMachine);
            }
            else
            {
                if (!envelope.StartsWith(RawKeyPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Existing secret.key has an unsupported platform format.");
                key = Convert.FromBase64String(envelope[RawKeyPrefix.Length..]);
            }

            if (key.Length != KeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException("Existing secret.key does not contain a 256-bit master key.");
            }
            return key;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new CryptographicException("Existing secret.key could not be opened. Refusing to replace encryption key material.", ex);
        }
    }
}
