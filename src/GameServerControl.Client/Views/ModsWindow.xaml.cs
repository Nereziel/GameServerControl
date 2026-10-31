using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class ModsWindow : Window
{
    private readonly AgentClient _client;
    private readonly string _serverId;
    private readonly Action<string> _toast;
    private readonly List<ModRowVm> _allInstalledRows = new();
    private ModUpdateInfo[] _pendingUpdates = Array.Empty<ModUpdateInfo>();

    public ObservableCollection<ModRowVm> Rows { get; } = new();
    public ObservableCollection<SearchRowVm> SearchRows { get; } = new();

    public ModsWindow(AgentClient client, string serverId, string serverDisplayName, Action<string> toast)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _serverId = serverId;
        _toast = toast;
        Title = $"Mods — {serverDisplayName}";
        HeaderText.Text = $"Server-side mods · {serverDisplayName}";
        ModList.ItemsSource = Rows;
        SearchList.ItemsSource = SearchRows;
        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            await SearchAsync("");
        };
    }

    // ---- installed tab ----

    private async Task ReloadAsync()
    {
        StatusText.Text = "Loading installed mods…";
        _allInstalledRows.Clear();
        Rows.Clear();
        try
        {
            var resp = await _client.ListModsAsync(_serverId);
            ModsFolderText.Text = resp.ModsFolder is null ? "" : $"Mod folder: {resp.ModsFolder}";
            if (!resp.Supported)
            {
                StatusText.Text = $"Mod management not available for this server. {resp.UnsupportedReason ?? ""}";
                EmptyText.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var m in resp.Mods)
            {
                var update = _pendingUpdates.FirstOrDefault(u =>
                    string.Equals(u.ModId, m.ModId, StringComparison.OrdinalIgnoreCase));
                _allInstalledRows.Add(new ModRowVm(m, update));
            }

            ApplyInstalledFilter();
            StatusText.Text = $"{resp.Mods.Length} mod(s) installed. Restart the server for changes to take effect.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"List failed: {ex.Message}";
        }
    }

    private void ApplyInstalledFilter()
    {
        var updatesOnly = UpdatesOnlyCheckbox?.IsChecked == true;
        Rows.Clear();
        foreach (var row in _allInstalledRows.Where(r => !updatesOnly || r.UpdateAvailable))
            Rows.Add(row);

        if (_allInstalledRows.Count == 0)
        {
            EmptyText.Text = "No mods installed yet. Use the Browse tab to find one.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (updatesOnly && Rows.Count == 0)
        {
            EmptyText.Text = _pendingUpdates.Length == 0
                ? "No updates found. Run 'Check for updates' to refresh marketplace versions."
                : "No installed mods match the update filter.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void UpdatesOnlyChanged(object sender, RoutedEventArgs e) => ApplyInstalledFilter();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) { StatusText.Text = "Enter a URL first."; return; }
        await InstallFromUrlAsync(url, displayName: null);
        UrlBox.Clear();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string modId) return;
        var confirm = MessageBox.Show(this, $"Uninstall '{modId}'?\n\nFiles will be deleted from disk.", "Uninstall mod",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var ok = await _client.UninstallModAsync(_serverId, modId);
            if (ok) { _toast($"Uninstalled '{modId}'."); await ReloadAsync(); }
            else { StatusText.Text = "Uninstall failed."; }
        }
        catch (Exception ex) { StatusText.Text = $"Uninstall failed: {ex.Message}"; }
    }

    // ---- update check ----

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdatesSummary.Text = "Checking marketplace versions…";
        UpdateAllBtn.IsEnabled = false;
        try
        {
            var resp = await _client.CheckModUpdatesAsync(_serverId);
            _pendingUpdates = resp.Updates;
            if (!resp.Supported)
            {
                UpdatesSummary.Text = "Updates not supported: " + (resp.UnsupportedReason ?? "n/a");
                _pendingUpdates = Array.Empty<ModUpdateInfo>();
                await ReloadAsync();
                return;
            }

            if (_pendingUpdates.Length == 0)
            {
                UpdatesSummary.Text = "All mods are up to date.";
                await ReloadAsync();
                return;
            }

            UpdatesSummary.Text = $"{_pendingUpdates.Length} update(s) available. Use Update on a single mod or Update all.";
            UpdateAllBtn.IsEnabled = true;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            UpdatesSummary.Text = "Check failed: " + ex.Message;
        }
    }

    private async void UpdateOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModRowVm row || !row.UpdateAvailable ||
            string.IsNullOrWhiteSpace(row.UpdateDownloadUrl)) return;

        btn.IsEnabled = false;
        UpdatesSummary.Text = $"Updating {row.DisplayName} → {row.LatestVersion}…";
        try
        {
            var result = await _client.UpdateModAsync(_serverId, row.ModId, row.UpdateDownloadUrl, row.DisplayName);
            if (!result.Ok)
            {
                UpdatesSummary.Text = $"Update failed for {row.DisplayName}: {result.Error}";
                return;
            }

            _toast($"Updated '{row.DisplayName}' to {row.LatestVersion}.");
            _pendingUpdates = _pendingUpdates
                .Where(u => !string.Equals(u.ModId, row.ModId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            UpdateAllBtn.IsEnabled = _pendingUpdates.Length > 0;
            UpdatesSummary.Text = _pendingUpdates.Length == 0
                ? "All mods are up to date."
                : $"{_pendingUpdates.Length} update(s) still available.";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            UpdatesSummary.Text = $"Update failed for {row.DisplayName}: {ex.Message}";
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdates.Length == 0) return;
        UpdateAllBtn.IsEnabled = false;
        var total = _pendingUpdates.Length;
        var done = 0;
        var failed = 0;
        foreach (var u in _pendingUpdates)
        {
            UpdatesSummary.Text = $"Updating {u.DisplayName} ({done + failed + 1}/{total})…";
            try
            {
                var r = await _client.UpdateModAsync(_serverId, u.ModId, u.LatestDownloadUrl, u.DisplayName);
                if (r.Ok) done++; else failed++;
            }
            catch { failed++; }
        }

        UpdatesSummary.Text = $"Updated {done}, failed {failed}. Restart the server for changes to take effect.";
        _pendingUpdates = Array.Empty<ModUpdateInfo>();
        await ReloadAsync();
    }

    // ---- browse tab ----

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, new RoutedEventArgs());
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(SearchBox.Text);
    private async void BrowseFilterChanged(object sender, RoutedEventArgs e) => await SearchAsync(SearchBox.Text);

    private async Task SearchAsync(string query)
    {
        SearchRows.Clear();
        SearchEmptyText.Visibility = Visibility.Collapsed;
        var serverSideOnly = ShowAllCheckbox?.IsChecked != true;
        var showDeprecated = ShowDeprecatedCheckbox?.IsChecked == true;
        var modeLabel = serverSideOnly ? "server-side only" : "all compatibility types";
        StatusText.Text = string.IsNullOrEmpty(query)
            ? $"Loading popular mods ({modeLabel})…"
            : $"Searching for \"{query}\" ({modeLabel})…";

        try
        {
            // Fetch more than the visible page so client-side deprecated filtering does not
            // accidentally leave a short list when popular results contain deprecated packages.
            var resp = await _client.SearchModsAsync(_serverId, query, limit: 100, serverSideOnly: serverSideOnly);
            if (!resp.Supported)
            {
                SearchSourceText.Text = resp.UnsupportedReason ?? "";
                SearchEmptyText.Text = resp.UnsupportedReason ?? "Search not available for this server.";
                SearchEmptyText.Visibility = Visibility.Visible;
                StatusText.Text = "";
                return;
            }

            var results = resp.Results
                .Where(r => showDeprecated || !r.Deprecated)
                .Take(50)
                .ToArray();

            foreach (var r in results) SearchRows.Add(new SearchRowVm(r));

            var deprecatedLabel = showDeprecated ? "deprecated shown" : "deprecated hidden";
            SearchSourceText.Text = resp.Source is null
                ? $"{results.Length} result(s) · {deprecatedLabel}"
                : $"Source: {resp.Source} · {results.Length} shown · {modeLabel} · {deprecatedLabel} · sorted by downloads";

            if (results.Length == 0)
            {
                SearchEmptyText.Text = serverSideOnly
                    ? "No matching server-side mods. Try another query, enable client-required mods, or show deprecated packages."
                    : "No matching mods. Try another query or show deprecated packages.";
                SearchEmptyText.Visibility = Visibility.Visible;
            }
            else
            {
                SearchEmptyText.Visibility = Visibility.Collapsed;
            }

            StatusText.Text = $"{results.Length} mod(s) shown. Deprecated packages are {(showDeprecated ? "visible" : "hidden by default")}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async void SearchInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SearchRowVm row) return;
        if (row.Deprecated)
        {
            var confirm = MessageBox.Show(this,
                $"'{row.Name}' is marked DEPRECATED by the marketplace.\n\nIt may be abandoned, incompatible or replaced. Install it anyway?",
                "Deprecated mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        StatusText.Text = $"Installing {row.Name}…";
        await InstallFromUrlAsync(row.DownloadUrl, displayName: row.Name);
    }

    private async Task InstallFromUrlAsync(string url, string? displayName)
    {
        try
        {
            var result = await _client.InstallModAsync(_serverId, url, displayName);
            if (result.Ok && result.Mod is not null)
            {
                _toast($"Installed mod '{result.Mod.DisplayName}'.");
                await ReloadAsync();
            }
            else
            {
                StatusText.Text = $"Install failed: {result.Error}";
            }
        }
        catch (Exception ex) { StatusText.Text = $"Install failed: {ex.Message}"; }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string url || string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Couldn't open: {ex.Message}"; }
    }

    // ---- view models ----

    public sealed class ModRowVm
    {
        public ModRowVm(ModInfo info, ModUpdateInfo? update)
        {
            ModId = info.ModId;
            DisplayName = info.DisplayName;
            Version = info.Version;
            SizeKb = info.SizeBytes / 1024;
            InstalledAtLabel = info.InstalledAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "(unknown)";
            UpdateAvailable = update is not null;
            LatestVersion = update?.LatestVersion;
            UpdateDownloadUrl = update?.LatestDownloadUrl;
            UpdateLabel = update is null ? "" : $"UPDATE → {update.LatestVersion}";
        }

        public string ModId { get; }
        public string DisplayName { get; }
        public string? Version { get; }
        public long SizeKb { get; }
        public string InstalledAtLabel { get; }
        public bool UpdateAvailable { get; }
        public string? LatestVersion { get; }
        public string? UpdateDownloadUrl { get; }
        public string UpdateLabel { get; }
    }

    public sealed class SearchRowVm
    {
        public SearchRowVm(ModSearchResult r)
        {
            Name = r.Name;
            Owner = r.Owner;
            Version = r.Version;
            IconUrl = r.IconUrl;
            DownloadUrl = r.DownloadUrl;
            PackageUrl = r.PackageUrl;
            RatingScore = r.RatingScore;
            ServerSideOnly = r.ServerSideOnly;
            ClientRequired = !r.ServerSideOnly;
            Deprecated = r.Deprecated;
            var d = (r.Description ?? "").Replace("\n", " ").Trim();
            DescriptionShort = d.Length > 180 ? d[..180] + "…" : d;
            DownloadsLabel = r.Downloads switch
            {
                >= 1_000_000 => $"{r.Downloads / 1_000_000.0:0.#}M",
                >= 1_000 => $"{r.Downloads / 1_000.0:0.#}k",
                _ => r.Downloads.ToString()
            };
            CategoriesLabel = string.Join(", ", r.Categories.Take(3));
        }

        public string Name { get; }
        public string Owner { get; }
        public string Version { get; }
        public string? IconUrl { get; }
        public string DownloadUrl { get; }
        public string PackageUrl { get; }
        public string DescriptionShort { get; }
        public string DownloadsLabel { get; }
        public int RatingScore { get; }
        public string CategoriesLabel { get; }
        public bool ServerSideOnly { get; }
        public bool ClientRequired { get; }
        public bool Deprecated { get; }
    }
}
