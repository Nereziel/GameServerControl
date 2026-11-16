using System.Net;
using System.Text;
using GameServerControl.Agent.Mods;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ModUpdateIdentityIntegrationTests
{
    [Fact]
    public async Task Update_forwards_installed_mod_id_to_manager_before_any_install()
    {
        var manager = new FakeModManager();
        using var factory = new AgentTestFactory(modManager: manager);
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var request = new StringContent(
            "{\"url\":\"https://example.invalid/new-mod.zip\",\"displayName\":\"New mod\"}",
            Encoding.UTF8, "application/json");

        using var response = await admin.PostAsync("/api/servers/alpha/mods/old-mod/update", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("old-mod", manager.ExpectedAtInstall);
        Assert.Equal(1, manager.InstallCount);
        Assert.False(manager.DidInstall);
    }

    [Fact]
    public async Task Ordinary_new_mod_install_has_no_expected_id()
    {
        var manager = new FakeModManager();
        using var factory = new AgentTestFactory(modManager: manager);
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var request = new StringContent(
            "{\"url\":\"https://example.invalid/new-mod.zip\",\"displayName\":\"New mod\"}",
            Encoding.UTF8, "application/json");

        using var response = await admin.PostAsync("/api/servers/alpha/mods/install", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(manager.ExpectedAtInstall);
        Assert.True(manager.DidInstall);
    }

    private sealed class FakeModManager : IModManager
    {
        public string? ExpectedAtInstall { get; private set; }
        public int InstallCount { get; private set; }
        public bool DidInstall { get; private set; }

        public bool Supports(ServerDef server) => true;
        public string ModsFolder(ServerDef server) => "";
        public string? MarketplaceSource(ServerDef server) => null;
        public Task<ModInfo[]> ListAsync(ServerDef server, CancellationToken ct) =>
            Task.FromResult(new[] { MakeMod("old-mod") });
        public Task<ModSearchResult[]> SearchAsync(
            ServerDef server, string query, int limit, bool serverSideOnly, CancellationToken ct) =>
            Task.FromResult(Array.Empty<ModSearchResult>());
        public Task<bool> UninstallAsync(ServerDef server, string modId, CancellationToken ct) =>
            throw new InvalidOperationException("Update must never delete the existing mod.");
        public Task<ModInstallResult> InstallFromUrlAsync(
            ServerDef server, string url, string? displayName, CancellationToken ct,
            string? expectedModId = null)
        {
            ExpectedAtInstall = expectedModId;
            InstallCount++;
            if (!ModInstallTransaction.MatchesExpectedModId(expectedModId, "new-mod"))
                return Task.FromResult(new ModInstallResult(false, null,
                    "Downloaded package does not match installed mod."));
            DidInstall = true;
            return Task.FromResult(new ModInstallResult(true, MakeMod("new-mod"), null));
        }

        private static ModInfo MakeMod(string id) =>
            new(id, id, "1.0", null, DateTimeOffset.UtcNow, 0, Array.Empty<string>());
    }
}
