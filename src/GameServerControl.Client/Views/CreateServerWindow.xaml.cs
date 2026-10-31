using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Client.ViewModels;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class CreateServerWindow : Window
{
    private readonly AgentClient _client;
    private readonly StatusHubClient _hub;
    private readonly Action _onCreated;
    private readonly ObservableCollection<InstallableGame> _games = new();
    private InstallableGame? _selected;
    private string? _activeJobId;

    public sealed record InstallableGame(
        string Key,
        string Label,
        string Icon,
        string SteamAppId,
        string EstimatedSize,
        string DefaultDirName,
        GamePreset Preset);

    public CreateServerWindow(AgentClient client, StatusHubClient hub, Action onCreated)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _hub = hub;
        _onCreated = onCreated;

        AddGame("valheim", "~1 GB");
        AddGame("dragonwilds", "varies");
        AddGame("palworld", "~7 GB");
        AddGame("satisfactory", "~12 GB");
        AddGame("ark-asa", "~50 GB");
        AddGame("ark-se", "~14 GB");
        AddGame("rust", "~8 GB");
        AddGame("vrising", "~3 GB");
        AddGame("conan", "~50 GB");
        AddGame("squad", "~40 GB");
        AddGame("soulmask", "~20 GB");
        AddGame("7dtd", "~2 GB");
        AddGame("terraria", "~0.5 GB");
        AddGame("dst", "~1 GB");
        AddGame("zomboid", "~1 GB");
        GameList.ItemsSource = _games;

        ShowStep(1);
        _hub.InstallProgress += OnInstallProgress;
        Closed += (_, _) => _hub.InstallProgress -= OnInstallProgress;
    }

    private void AddGame(string key, string size)
    {
        var p = GamePresets.FindByKey(key) ?? throw new InvalidOperationException($"Missing game profile '{key}'.");
        if (string.IsNullOrWhiteSpace(p.SteamAppId)) return;
        var profile = GameProfiles.FindByKey(key);
        _games.Add(new InstallableGame(
            key,
            p.Label,
            profile?.Icon ?? "",
            p.SteamAppId!,
            size,
            profile?.InstallDirectoryName ?? Path.GetFileName(p.DefaultWorkingDir.TrimEnd('\\', '/')),
            p));
    }

    // ===== Step 1: game picker =====

    private void GameRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string key) return;
        _selected = _games.FirstOrDefault(g => g.Key == key);
        if (_selected is null) return;

        DisplayNameBox.Text = _selected.Label;
        IdBox.Text = SlugifyName(_selected.Label);
        InstallPathBox.Text = Path.Combine(DefaultInstallRoot(), _selected.DefaultDirName);
        Step2GameHeader.Text = $"{_selected.Label}  ·  Steam App ID {_selected.SteamAppId}";
        UpdatePreview();

        DisplayNameBox.TextChanged -= OnFieldChanged;
        IdBox.TextChanged -= OnFieldChanged;
        InstallPathBox.TextChanged -= OnFieldChanged;
        DisplayNameBox.TextChanged += OnFieldChanged;
        IdBox.TextChanged += OnFieldChanged;
        InstallPathBox.TextChanged += OnFieldChanged;
        ShowStep(2);
    }

    private void OnFieldChanged(object? sender, EventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_selected is null) return;
        var path = (InstallPathBox.Text ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = _selected.Preset;
        var exeAbs = string.IsNullOrEmpty(path) ? p.DefaultExeRelative : Path.Combine(path, p.DefaultExeRelative);
        var args = RebaseValues(p.DefaultStartArgs, p.DefaultWorkingDir, path);
        PreviewExe.Text = "EXE: " + exeAbs;
        PreviewArgs.Text = "ARGS: " + (args.Length == 0 ? "(none)" : string.Join(" ", args));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to install the dedicated server",
            InitialDirectory = (InstallPathBox.Text ?? "").Trim()
        };
        if (dlg.ShowDialog() == true)
        {
            InstallPathBox.Text = dlg.FolderName;
            UpdatePreview();
        }
    }

    // ===== Step 3: install =====

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        Step2Error.Visibility = Visibility.Collapsed;
        if (_selected is null) { Show2Err("Pick a game first."); return; }

        var name = (DisplayNameBox.Text ?? "").Trim();
        var id = (IdBox.Text ?? "").Trim();
        var path = (InstallPathBox.Text ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (name.Length == 0) { Show2Err("Display name is required."); return; }
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,63}$")) { Show2Err("ID must be lowercase letters, digits, and hyphens."); return; }
        if (path.Length == 0) { Show2Err("Install location is required."); return; }

        var preset = _selected.Preset;
        var exeAbs = Path.Combine(path, preset.DefaultExeRelative);
        var startArgs = RebaseValues(preset.DefaultStartArgs, preset.DefaultWorkingDir, path);
        var saveDirs = RebaseValues(preset.DefaultSaveDirs, preset.DefaultWorkingDir, path);
        var logPath = RebaseValue(preset.DefaultLogPath, preset.DefaultWorkingDir, path);
        var caps = preset.Capabilities;

        var def = new ServerDef(
            Id: id,
            Name: name,
            VmName: "",
            GameType: preset.GameType,
            GuestExePath: exeAbs,
            GuestWorkingDir: path,
            StartArgs: startArgs,
            SaveDirs: saveDirs,
            SteamAppId: _selected.SteamAppId,
            GuestCredentialId: null,
            LogPathInGuest: caps.LiveLog ? logPath : null,
            RconHost: caps.Rcon ? "auto" : null,
            RconPort: caps.Rcon ? preset.DefaultRconPort : null,
            RconPassword: null,
            HostingMode: HostingMode.BareMetal,
            ScheduledTaskName: null,
            StopProcessNames: null,
            DiscordWebhookUrl: null);

        ShowStep(3);
        Step3Header.Text = $"Installing {_selected.Label}…";
        AppendLog("$ steamcmd +force_install_dir \"" + path + "\" +login anonymous +app_update " + _selected.SteamAppId + " validate +quit");
        if (saveDirs.Length > 0) AppendLog("Save data: " + string.Join("; ", saveDirs));
        if (!string.IsNullOrWhiteSpace(logPath)) AppendLog("Live log: " + logPath);

        try
        {
            var ack = await _client.InstallServerAsync(new InstallServerRequest(_selected.SteamAppId, path, def));
            _activeJobId = ack.JobId;
            Step3Status.Text = $"Job: {ack.JobId} (live updates from agent)";
        }
        catch (Exception ex)
        {
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 0;
            AppendLog("ERROR: " + ex.Message);
            Step3Status.Text = "Install request failed — fix above and try again.";
            BackBtn.Visibility = Visibility.Visible;
        }
    }

    private static string[] RebaseValues(IEnumerable<string> values, string oldRoot, string newRoot)
        => values.Select(v => RebaseValue(v, oldRoot, newRoot) ?? "").Where(v => v.Length > 0).ToArray();

    private static string? RebaseValue(string? value, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var from = (oldRoot ?? "").TrimEnd('\\', '/');
        var to = (newRoot ?? "").TrimEnd('\\', '/');
        return from.Length == 0 || to.Length == 0
            ? value
            : value.Replace(from, to, StringComparison.OrdinalIgnoreCase);
    }

    private void OnInstallProgress(InstallProgress p)
    {
        if (_activeJobId is null || p.JobId != _activeJobId) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            AppendLog(p.Line);
            if (p.PercentHint is int pct)
            {
                InstallProgressBar.IsIndeterminate = false;
                InstallProgressBar.Value = pct;
                InstallProgressBar.Maximum = 100;
            }
            Step3Status.Text = p.Phase switch
            {
                "queued" => "Queued…",
                "steamcmd" => p.PercentHint is int v ? $"SteamCMD · {v}%" : "SteamCMD running…",
                "download" => "Downloading fallback package…",
                "verify" => "Verifying installed files…",
                "register" => "Registering with dashboard…",
                "done" => "Done. Server is ready.",
                "failed" => "Failed. See log above.",
                _ => p.Phase
            };
            if (!p.Finished) return;
            InstallProgressBar.IsIndeterminate = false;
            if (p.Success)
            {
                InstallProgressBar.Value = 100;
                Step3Header.Text = $"{_selected?.Label} installed.";
                DoneBtn.Visibility = Visibility.Visible;
                _onCreated?.Invoke();
            }
            else
            {
                Step3Header.Text = "Install failed.";
                BackBtn.Visibility = Visibility.Visible;
            }
        });
    }

    // ===== Navigation =====

    private void ShowStep(int step)
    {
        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step1Pill.Background = step >= 1 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BgBrush3");
        Step2Pill.Background = step >= 2 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BgBrush3");
        Step3Pill.Background = step >= 3 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BgBrush3");
        BackBtn.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Visibility = Visibility.Collapsed;
        InstallBtn.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        DoneBtn.Visibility = Visibility.Collapsed;
        StepSubtitle.Text = step switch
        {
            1 => "Pick a game. GameServerControl fills game-specific defaults automatically.",
            2 => "Name the server and choose its folder. Saves/logs are kept there when the game supports it.",
            _ => "Installation continues in the background on the Agent."
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Step3Panel.Visibility == Visibility.Visible) ShowStep(2);
        else if (Step2Panel.Visibility == Visibility.Visible) ShowStep(1);
    }

    private void Next_Click(object sender, RoutedEventArgs e) { }
    private void Done_Click(object sender, RoutedEventArgs e) => Close();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Show2Err(string msg) { Step2Error.Text = msg; Step2Error.Visibility = Visibility.Visible; }

    private void AppendLog(string line)
    {
        var current = InstallLog.Text;
        var lines = current.Split('\n');
        if (lines.Length > 1000) current = string.Join('\n', lines.Skip(lines.Length - 800));
        InstallLog.Text = current + line + "\n";
        LogScroll.ScrollToEnd();
    }

    private static string SlugifyName(string s)
    {
        var lower = s.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "" : slug;
    }

    private static string DefaultInstallRoot() =>
        OperatingSystem.IsWindows()
            ? @"C:\GameServers"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "gameservers");
}
