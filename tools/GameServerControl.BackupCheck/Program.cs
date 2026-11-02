using GameServerControl.Shared;

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
}

static void AssertThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message + $" (expected {typeof(T).Name}).");
}

AssertEqual("world/file.txt", BackupSafety.NormalizeArchiveEntry("world\\file.txt"), "Backslashes must normalize to forward slashes");
AssertEqual("world/sub/file.txt", BackupSafety.NormalizeArchiveEntry("world/sub/file.txt"), "Valid relative entry changed unexpectedly");
AssertThrows<InvalidDataException>(() => BackupSafety.NormalizeArchiveEntry("../evil.txt"), "Parent traversal must be rejected");
AssertThrows<InvalidDataException>(() => BackupSafety.NormalizeArchiveEntry("world/../../evil.txt"), "Nested traversal must be rejected");
AssertThrows<InvalidDataException>(() => BackupSafety.NormalizeArchiveEntry("/absolute.txt"), "Rooted archive paths must be rejected");
AssertThrows<InvalidDataException>(() => BackupSafety.NormalizeArchiveEntry("C:\\absolute.txt"), "Windows rooted archive paths must be rejected");

AssertEqual("server-20261102-120000.zip", BackupSafety.ValidateBackupFileName("server-20261102-120000.zip"), "Valid backup filename changed unexpectedly");
AssertThrows<ArgumentException>(() => BackupSafety.ValidateBackupFileName("../server.zip"), "Backup filename traversal must be rejected");
AssertThrows<ArgumentException>(() => BackupSafety.ValidateBackupFileName("folder/server.zip"), "Backup filename folders must be rejected");
AssertThrows<ArgumentException>(() => BackupSafety.ValidateBackupFileName("server.txt"), "Non-ZIP backup filename must be rejected");

var root = Path.Combine(Path.GetTempPath(), "gsc-backup-check", Guid.NewGuid().ToString("n"));
var resolved = BackupSafety.ResolveUnderRoot(root, "world/file.txt");
if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    throw new InvalidOperationException("Resolved path escaped the requested root.");
AssertThrows<InvalidDataException>(() => BackupSafety.ResolveUnderRoot(root, "../evil.txt"), "Resolved parent traversal must be rejected");

_ = typeof(BackupCreateRequest);
_ = typeof(BackupSettings);
_ = BackupKind.Archive;

Console.WriteLine("Backup safety contract passed.");
