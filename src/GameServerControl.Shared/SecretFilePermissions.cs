using System.Text;

namespace GameServerControl.Shared;

public static class SecretFilePermissions
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void RestrictExisting(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            File.SetUnixFileMode(directory, PrivateDirectoryMode);
        if (File.Exists(fullPath))
            File.SetUnixFileMode(fullPath, PrivateFileMode);
    }

    public static void WriteAllTextAtomic(string path, string contents, string? backupPath = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Secret file path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Secret file path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, PrivateDirectoryMode);

        string? fullBackupPath = null;
        if (!string.IsNullOrWhiteSpace(backupPath))
        {
            fullBackupPath = Path.GetFullPath(backupPath);
            var backupDirectory = Path.GetDirectoryName(fullBackupPath)
                ?? throw new ArgumentException("Secret backup path must have a parent directory.", nameof(backupPath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetFullPath(backupDirectory), Path.GetFullPath(directory), comparison))
                throw new ArgumentException("Secret backup must be stored in the same restricted directory as the primary file.", nameof(backupPath));
            if (string.Equals(fullBackupPath, fullPath, comparison))
                throw new ArgumentException("Secret backup path must differ from the primary file path.", nameof(backupPath));
        }

        var tmp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(tmp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            else
            {
                using var stream = new FileStream(tmp, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    UnixCreateMode = PrivateFileMode
                });
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(contents);
            }

            if (fullBackupPath is not null && File.Exists(fullPath))
            {
                File.Copy(fullPath, fullBackupPath, overwrite: true);
                RestrictExisting(fullBackupPath);
            }

            File.Move(tmp, fullPath, overwrite: true);
            RestrictExisting(fullPath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
