using System.IO.Compression;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Mods;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class MinecraftBedrockAddOnTests
{
    private const string PackId = "33e063e8-49b8-4b10-b822-5b0f20495a5e";
    private const string ModuleId = "95986c19-4610-4160-935c-ea656733e989";

    [Fact]
    public async Task Import_and_uninstall_activate_and_deactivate_world_behavior_pack()
    {
        var (def, tracker, root) = CreateServer();
        try
        {
            var service = new MinecraftBedrockAddOnManager(
                NullLogger<MinecraftBedrockAddOnManager>.Instance, tracker);
            await using var upload = CreatePack("data");
            var result = await service.ImportAsync(def, "example.mcpack", upload, CancellationToken.None);
            Assert.True(result.Ok, result.Error);
            var installed = Path.Combine(root, "behavior_packs", PackId);
            Assert.True(File.Exists(Path.Combine(installed, "manifest.json")));
            var active = Path.Combine(root, "worlds", "Bedrock level", "world_behavior_packs.json");
            Assert.Equal(PackId, (string?)JsonNode.Parse(File.ReadAllText(active))?.AsArray()[0]?["pack_id"]);
            var listed = await service.ListAsync(def, CancellationToken.None);
            Assert.Contains(listed, mod => mod.ModId == "behavior-" + PackId);
            var deleted = await service.UninstallAsync(def, "behavior-" + PackId, CancellationToken.None);
            Assert.True(deleted);
            Assert.False(Directory.Exists(installed));
            Assert.Empty(JsonNode.Parse(File.ReadAllText(active))!.AsArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Script_packs_are_rejected_without_modifying_world()
    {
        var (def, tracker, root) = CreateServer();
        try
        {
            var service = new MinecraftBedrockAddOnManager(
                NullLogger<MinecraftBedrockAddOnManager>.Instance, tracker);
            await using var upload = CreatePack("script");
            var result = await service.ImportAsync(def, "unsafe.mcpack", upload, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.False(Directory.Exists(Path.Combine(root, "behavior_packs", PackId)));
            Assert.False(File.Exists(Path.Combine(root, "worlds", "Bedrock level", "world_behavior_packs.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Corrupt_world_metadata_causes_pack_rollback()
    {
        var (def, tracker, root) = CreateServer();
        try
        {
            var config = Path.Combine(root, "worlds", "Bedrock level", "world_behavior_packs.json");
            File.WriteAllText(config, "broken JSON");
            var service = new MinecraftBedrockAddOnManager(
                NullLogger<MinecraftBedrockAddOnManager>.Instance, tracker);
            await using var upload = CreatePack("data");
            var result = await service.ImportAsync(def, "example.mcpack", upload, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal("broken JSON", File.ReadAllText(config));
            Assert.False(Directory.Exists(Path.Combine(root, "behavior_packs", PackId)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Import_is_rejected_while_server_is_running()
    {
        var (def, tracker, root) = CreateServer();
        try
        {
            tracker.Update(def.Id, state => state with { ProcessState = ProcessState.Running });
            var service = new MinecraftBedrockAddOnManager(
                NullLogger<MinecraftBedrockAddOnManager>.Instance, tracker);
            await using var upload = CreatePack("data");
            var result = await service.ImportAsync(def, "example.mcpack", upload, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.False(Directory.Exists(Path.Combine(root, "behavior_packs", PackId)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (ServerDef Def, StatusTracker Tracker, string Root) CreateServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-bedrock-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "worlds", "Bedrock level"));
        File.WriteAllText(Path.Combine(root, "server.properties"), "level-name=Bedrock level\n");
        var def = new ServerDef("bedrock-test", "Bedrock Test", "", GameType.Minecraft,
            Path.Combine(root, OperatingSystem.IsWindows() ? "bedrock_server.exe" : "bedrock_server"),
            root, Array.Empty<string>(), new[] { Path.Combine(root, "worlds") }, null, null, null,
            HostingMode: HostingMode.BareMetal, GameProfileKey: "minecraft-bedrock");
        var tracker = new StatusTracker();
        tracker.Update(def.Id, status => status with { ProcessState = ProcessState.NotRunning });
        return (def, tracker, root);
    }

    private static MemoryStream CreatePack(string moduleType)
    {
        var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
        {
            var json = $$"""
                {
                  "format_version": 2,
                  "header": {"name": "Test Pack", "description": "Test", "uuid": "{{PackId}}", "version": [1,0,0]},
                  "modules": [{"type": "{{moduleType}}", "uuid": "{{ModuleId}}", "version": [1,0,0]}]
                }
                """;
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write(json);
            using var sample = new StreamWriter(zip.CreateEntry("texts/en_US.lang").Open());
            sample.Write("sample.name=Sample");
        }
        data.Position = 0;
        return data;
    }
}
