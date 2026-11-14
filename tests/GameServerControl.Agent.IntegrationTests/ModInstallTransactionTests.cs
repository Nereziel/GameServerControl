using GameServerControl.Agent.Mods;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ModInstallTransactionTests
{
    [Fact]
    public void Failed_directory_swap_restores_old_mod()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-update-" + Guid.NewGuid().ToString("N"));
        var mod = Path.Combine(root, "Example");
        var staged = Path.Combine(root, "Stage");
        Directory.CreateDirectory(mod);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(mod, "old.txt"), "old");
        File.WriteAllText(Path.Combine(staged, "new.txt"), "new");
        try
        {
            Assert.Throws<IOException>(() => ModInstallTransaction.ReplaceDirectory(staged, mod,
                (source, destination) =>
                {
                    if (source == staged) throw new IOException("simulated failed swap");
                    Directory.Move(source, destination);
                }));
            Assert.True(File.Exists(Path.Combine(mod, "old.txt")));
            Assert.False(File.Exists(Path.Combine(mod, "new.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Directory_swap_commits_new_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-update-" + Guid.NewGuid().ToString("N"));
        var mod = Path.Combine(root, "Example");
        var staged = Path.Combine(root, "Stage");
        Directory.CreateDirectory(mod);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(mod, "old.txt"), "old");
        File.WriteAllText(Path.Combine(staged, "new.txt"), "new");
        try
        {
            Assert.False(ModInstallTransaction.ReplaceDirectory(staged, mod));
            Assert.True(File.Exists(Path.Combine(mod, "new.txt")));
            Assert.False(File.Exists(Path.Combine(mod, "old.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_new_file_does_not_replace_original()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-file-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "mod.dll");
        File.WriteAllText(original, "original");
        try
        {
            Assert.Throws<FileNotFoundException>(() =>
                ModInstallTransaction.ReplaceFiles(root, new[] {
                    (Source: Path.Combine(root, "missing.dll"), Destination: original)
                }));
            Assert.Equal("original", File.ReadAllText(original));
            Assert.Throws<InvalidOperationException>(() =>
                ModInstallTransaction.ReplaceFiles(root, new[] {
                    (Source: original, Destination: Path.Combine(root, "..", "outside.dll"))
                }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
