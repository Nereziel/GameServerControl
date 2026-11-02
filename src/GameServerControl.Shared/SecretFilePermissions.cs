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

    public static void WriteAllTextAtomic(string path, string contents)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Secret file path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Secret file path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, PrivateDirectoryMode);

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

            File.Move(tmp, fullPath, overwrite: true);
            RestrictExisting(fullPath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
