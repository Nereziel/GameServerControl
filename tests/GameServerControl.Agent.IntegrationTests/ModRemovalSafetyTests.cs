using GameServerControl.Agent.Mods;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ModRemovalSafetyTests
{
    [Fact]
    public void Sidecar_paths_cannot_escape_or_target_other_mod_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mod-path-test-" + Guid.NewGuid().ToString("N"));
        var ownFolder = Path.Combine(root, "Mods", "example");
        Directory.CreateDirectory(ownFolder);
        try
        {
            var installed = Path.Combine(ownFolder, "mod.dll");
            File.WriteAllText(installed, "test");
            Assert.True(ModRemovalSafety.TryResolveInstalledFile(root, ownFolder,
                Path.Combine("Mods", "example", "mod.dll"), out var resolved));
            Assert.Equal(installed, resolved);
            Assert.False(ModRemovalSafety.TryResolveInstalledFile(root, ownFolder,
                Path.Combine("Mods", "other", "another.dll"), out _));
            Assert.False(ModRemovalSafety.TryResolveInstalledFile(root, ownFolder,
                Path.Combine("Mods", "example", "..", "..", "..", "outside.dll"), out _));
            Assert.False(ModRemovalSafety.TryResolveInstalledFile(root, ownFolder, installed, out _));
            Assert.False(ModRemovalSafety.TryResolveModDirectory(Path.Combine(root, "Mods"), "..", out _));
            Assert.False(ModRemovalSafety.TryResolveModDirectory(Path.Combine(root, "Mods"), "../outside", out _));
            Assert.True(ModRemovalSafety.TryResolveModDirectory(Path.Combine(root, "Mods"), "example", out _));
            Assert.True(ModRemovalSafety.CanDeleteDirectoryTree(ownFolder));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Sidecar_paths_reject_symlink_redirection()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mod-links-test-" + Guid.NewGuid().ToString("N"));
        var ownFolder = Path.Combine(root, "Mods", "example");
        var outsideFolder = Path.Combine(root, "external");
        Directory.CreateDirectory(ownFolder);
        Directory.CreateDirectory(outsideFolder);
        try
        {
            var linked = Path.Combine(ownFolder, "linked");
            try { Directory.CreateSymbolicLink(linked, outsideFolder); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                or PlatformNotSupportedException or NotSupportedException)
            {
                // Some Windows runners lack symlink privileges.
                return;
            }
            Assert.False(ModRemovalSafety.TryResolveInstalledFile(root, ownFolder,
                Path.Combine("Mods", "example", "linked", "outside.dll"), out _));
            Assert.False(ModRemovalSafety.CanDeleteDirectoryTree(ownFolder));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
