using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GameServerControl.Agent.Mods;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class MinecraftProvisioningTests
{
    private static MinecraftInstallRequest Request(string key) => new(
        key, Path.Combine(Path.GetTempPath(), "gsc-mc-" + Guid.NewGuid().ToString("N")),
        "minecraft-alpha", "Minecraft Alpha", key == "minecraft-bedrock" ? null : "1.21.11", 4096,
        key == "minecraft-bedrock" ? 19132 : 25565,
        AcceptEula: true, AcceptBedrockTerms: true);

    [Theory]
    [InlineData("minecraft", NetworkProtocol.Tcp, 25565)]
    [InlineData("minecraft-paper", NetworkProtocol.Tcp, 25565)]
    [InlineData("minecraft-fabric", NetworkProtocol.Tcp, 25565)]
    [InlineData("minecraft-bedrock", NetworkProtocol.Udp, 19132)]
    public void Profiles_are_distinct_and_expose_correct_protocol(
        string key, NetworkProtocol protocol, int port)
    {
        var profile = GameProfiles.FindByKey(key);
        Assert.NotNull(profile);
        Assert.True(profile.Installable);
        Assert.Equal(GameType.Minecraft, profile.GameType);
        Assert.Equal(GameConsoleMode.Stdin, profile.Capabilities.ConsoleMode);
        Assert.Equal(protocol, Assert.Single(profile.DefaultPorts!).Protocol);
        Assert.Equal(port, Assert.Single(profile.DefaultPorts!).Port);
        var def = new ServerDef(
            "minecraft-alpha", "Minecraft Alpha", "", GameType.Minecraft,
            "/usr/bin/java", "/srv/minecraft-alpha", Array.Empty<string>(),
            Array.Empty<string>(), null, null, null,
            HostingMode: HostingMode.BareMetal, GameProfileKey: key);
        Assert.Equal(key, GameProfiles.FindFor(def)!.Key);
    }

    [Fact]
    public void Java_eula_must_be_accepted_explicitly()
    {
        Assert.Contains("EULA", MinecraftServerPackages.Validate(Request("minecraft") with { AcceptEula = false }));
        Assert.Null(MinecraftServerPackages.Validate(Request("minecraft")));
    }

    [Fact]
    public void Bedrock_terms_must_be_accepted_explicitly()
    {
        Assert.Contains("license", MinecraftServerPackages.Validate(
            Request("minecraft-bedrock") with { AcceptBedrockTerms = false }), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("minecraft", 1023)]
    [InlineData("minecraft-paper", 65536)]
    [InlineData("minecraft-bedrock", 1)]
    public void Invalid_ports_are_rejected(string key, int port)
    {
        Assert.Contains("port", MinecraftServerPackages.Validate(Request(key) with { Port = port }),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recovery_registers_existing_Bedrock_files_without_download_or_world_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mc-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gsc-minecraft.json"),
                """{"profileKey":"minecraft-bedrock","version":"1.21.0"}""");
            File.WriteAllText(Path.Combine(root, "server.properties"), "server-port=19145\n");
            File.WriteAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "bedrock_server.exe" : "bedrock_server"), "test");
            Directory.CreateDirectory(Path.Combine(root, "worlds", "Original World"));
            var world = Path.Combine(root, "worlds", "Original World", "level.dat");
            File.WriteAllText(world, "existing-world-data");
            var result = MinecraftServerPackages.RecoverDefinition(
                new MinecraftRecoveryRequest("recovered", "Recovered Bedrock", root));
            Assert.Equal("minecraft-bedrock", result.GameProfileKey);
            Assert.Equal(19145, Assert.Single(result.NetworkPorts!).Port);
            Assert.Equal(NetworkProtocol.Udp, Assert.Single(result.NetworkPorts!).Protocol);
            Assert.Equal(root, result.GuestWorkingDir);
            Assert.Equal("existing-world-data", File.ReadAllText(world));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Recovery_refuses_unmarked_or_incomplete_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mc-unmarked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                MinecraftServerPackages.RecoverDefinition(
                    new MinecraftRecoveryRequest("recovered", "Recovered", root)));
            File.WriteAllText(Path.Combine(root, ".gsc-minecraft.json"),
                """{"profileKey":"minecraft-bedrock","version":"1.21.0"}""");
            Assert.Throws<InvalidDataException>(() =>
                MinecraftServerPackages.RecoverDefinition(
                    new MinecraftRecoveryRequest("recovered", "Recovered", root)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Existing_installation_is_not_overwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), "gsc-mc-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Combine(path, "world.dat"), "must not erase");
            Assert.Contains("exists", MinecraftServerPackages.Validate(Request("minecraft") with { InstallPath = path }));
            Assert.Equal("must not erase", File.ReadAllText(Path.Combine(path, "world.dat")));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void Reject_non_official_download_links()
    {
        Assert.Throws<InvalidDataException>(() =>
            MinecraftServerPackages.VerifiedUrl("http://piston-data.mojang.com/v1/objects/a/server.jar",
                "piston-data.mojang.com", "/v1/"));
        Assert.Throws<InvalidDataException>(() =>
            MinecraftServerPackages.VerifiedUrl("https://attacker.invalid/bedrockdedicatedserver/fake.zip",
                "www.minecraft.net", "/bedrockdedicatedserver/"));
        Assert.Throws<InvalidDataException>(() =>
            MinecraftServerPackages.VerifiedUrl("https://www.minecraft.net/bedrockdedicatedserver/fake.zip?redirect=evil",
                "www.minecraft.net", "/bedrockdedicatedserver/"));
    }

    [Fact]
    public void Zip_extraction_blocks_parent_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mc-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archiveFile = Path.Combine(root, "archive.zip");
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        try
        {
            using (var file = File.Create(archiveFile))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using var entry = zip.CreateEntry("../outside.txt").Open();
                entry.WriteByte(65);
            }
            Assert.Throws<InvalidDataException>(() => MinecraftServerPackages.ExtractSafeZip(archiveFile, stage));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Zip_extraction_rejects_compression_bombs()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mc-bomb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archiveFile = Path.Combine(root, "bomb.zip");
            using (var output = File.Create(archiveFile))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("highly-compressible.bin", CompressionLevel.SmallestSize).Open())
            {
                var block = new byte[1024 * 1024];
                for (var i = 0; i < 5; i++) entry.Write(block);
            }
            Assert.Throws<InvalidDataException>(() =>
                MinecraftServerPackages.ExtractSafeZip(archiveFile, Path.Combine(root, "stage")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Zip_extraction_rejects_unix_symlink_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mc-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archiveFile = Path.Combine(root, "symlink.zip");
            using (var output = File.Create(archiveFile))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("behavior_packs/linked");
                entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using var writer = new StreamWriter(entry.Open());
                writer.Write("../../outside");
            }
            Assert.Throws<InvalidDataException>(() =>
                MinecraftServerPackages.ExtractSafeZip(archiveFile, Path.Combine(root, "stage")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Zip_extraction_rejects_too_many_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mc-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archiveFile = Path.Combine(root, "entries.zip");
            using (var output = File.Create(archiveFile))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                for (var i = 0; i < 4097; i++) zip.CreateEntry("entry-" + i);
            Assert.Throws<InvalidDataException>(() =>
                MinecraftServerPackages.ExtractSafeZip(archiveFile, Path.Combine(root, "stage")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Bedrock_zip_extraction_accepts_real_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-mc-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archiveFile = Path.Combine(root, "archive.zip");
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        try
        {
            using (var file = File.Create(archiveFile))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                zip.CreateEntry("behavior_packs/");
                using var output = new StreamWriter(zip.CreateEntry("behavior_packs/manifest.json").Open());
                output.Write("{}");
            }
            MinecraftServerPackages.ExtractSafeZip(archiveFile, stage);
            Assert.Equal("{}", File.ReadAllText(Path.Combine(stage, "behavior_packs", "manifest.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/abc123/versions/version789/plugin.jar", true)]
    [InlineData("https://cdn.modrinth.com/data/abc123/versions/version789/plugin.jar?url=http://localhost", false)]
    [InlineData("http://cdn.modrinth.com/data/abc123/versions/version789/plugin.jar", false)]
    [InlineData("https://127.0.0.1/data/abc123/versions/version789/plugin.jar", false)]
    [InlineData("https://cdn.modrinth.com/data/abc123/versions/version789/../plugin.jar", false)]
    public void Modrinth_url_restrictions(string url, bool valid)
    {
        Assert.Equal(valid, MinecraftJavaModManager.TryParseModrinthUrl(url, out _, out _, out _));
    }

    [Fact]
    public async Task Install_api_requires_license_and_readonly_cannot_install()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var payload = """{"profileKey":"minecraft","installPath":"/tmp/gsc-ignored-path","serverId":"mc-test","name":"Test","port":25565,"acceptEula":false}""";
        using var badRequest = new StringContent(payload, Encoding.UTF8, "application/json");
        using var denied = await admin.PostAsync("/api/servers/minecraft/install", badRequest);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        var readonlyToken = factory.CreateReadOnlyToken();
        using var viewer = factory.CreateClientWithToken(readonlyToken);
        using var otherRequest = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await viewer.PostAsync("/api/servers/minecraft/install", otherRequest);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
