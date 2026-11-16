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

    [Theory]
    [InlineData(null, "example", true)]
    [InlineData("example", "example", true)]
    [InlineData("EXAMPLE", "example", true)]
    [InlineData("installed", "replacement", false)]
    [InlineData("installed", "", false)]
    public void Mod_update_identity_is_checked_before_installation(string? expected, string actual, bool accepted)
    {
        Assert.Equal(accepted, ModInstallTransaction.MatchesExpectedModId(expected, actual));
    }

    [Fact]
    public void Metadata_write_failure_rolls_back_directory_and_preserves_original()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mod-metadata-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "original-mod");
        var staged = Path.Combine(root, "stage");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(installed, "mod.dll"), "old");
        File.WriteAllText(Path.Combine(staged, "mod.dll"), "new");
        var sidecar = Path.Combine(root, "metadata.json");
        ModMetadataStore.WriteAtomic(sidecar, "{\\"version\\":1}");
        try
        {
            using (var tx = ModInstallTransaction.BeginDirectory(staged, installed))
            {
                Assert.Throws<IOException>(() => tx.Complete(() =>
                {
                    ModMetadataStore.WriteAtomic(Path.Combine(root, "metadata.json", "invalid.json"), "{}");
                }));
            }
            Assert.Equal("old", File.ReadAllText(Path.Combine(installed, "mod.dll")));
            Assert.Equal("{\\"version\\":1}", File.ReadAllText(sidecar));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Metadata_write_failure_rolls_back_overwritten_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mod-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var installed = Path.Combine(root, "mod.dll");
        var staged = Path.Combine(root, "mod.new");
        var sidecar = Path.Combine(root, ".gsc-mods.json");
        File.WriteAllText(installed, "old");
        File.WriteAllText(staged, "new");
        ModMetadataStore.WriteAtomic(sidecar, "{\\"version\\":1}");
        try
        {
            using (var tx = ModInstallTransaction.BeginFiles(root, new[] { (Source: staged, Destination: installed) }))
            {
                Assert.Throws<IOException>(() => tx.Complete(() =>
                {
                    ModMetadataStore.WriteAtomic(Path.Combine(installed, "invalid.json"), "{}");
                }));
            }
            Assert.Equal("old", File.ReadAllText(installed));
            Assert.Equal("{\\"version\\":1}", File.ReadAllText(sidecar));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Metadata_commit_succeeds_with_file_swap()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mod-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var installed = Path.Combine(root, "mod.dll");
        var staged = Path.Combine(root, "mod.new");
        var sidecar = Path.Combine(root, ".gsc-mods.json");
        File.WriteAllText(installed, "old");
        File.WriteAllText(staged, "new");
        ModMetadataStore.WriteAtomic(sidecar, "{\\"version\\":1}");
        try
        {
            using (var tx = ModInstallTransaction.BeginFiles(root, new[] { (Source: staged, Destination: installed) }))
            {
                Assert.False(tx.Complete(() => ModMetadataStore.WriteAtomic(sidecar, "{\\"version\\":2}")));
            }
            Assert.Equal("new", File.ReadAllText(installed));
            Assert.Equal("{\\"version\\":2}", File.ReadAllText(sidecar));
            Assert.Empty(Directory.EnumerateFiles(root, "*.gsc-prev-*"));
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
