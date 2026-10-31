using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class ServerEditorViewModel : ObservableObject
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$");

    public bool IsEdit { get; }

    [ObservableProperty] private string id = "";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string vmName = "";
    [ObservableProperty] private HostingMode hostingMode = HostingMode.BareMetal;
    [ObservableProperty] private GameType gameType = GameType.Custom;
    [ObservableProperty] private string guestExePath = "";
    [ObservableProperty] private string guestWorkingDir = "";
    [ObservableProperty] private string startArgsText = "";
    [ObservableProperty] private string saveDirsText = "";
    [ObservableProperty] private string steamAppId = "";
    [ObservableProperty] private string guestCredentialId = "default";
    [ObservableProperty] private string logPathInGuest = "";
    [ObservableProperty] private string rconHost = "auto";
    [ObservableProperty] private string rconPort = "";
    [ObservableProperty] private string rconPassword = "";
    [ObservableProperty] private string scheduledTaskName = "";
    [ObservableProperty] private string stopProcessNamesText = "";
    [ObservableProperty] private string discordWebhookUrl = "";
    [ObservableProperty] private string? selectedPresetKey;
    [ObservableProperty] private string? validationError;
    [ObservableProperty] private bool showAdvanced;

    public GamePreset[] Presets { get; } = GamePresets.All;
    public Array AvailableGameTypes { get; } = Enum.GetValues(typeof(GameType));
    public Array AvailableHostingModes { get; } = Enum.GetValues(typeof(HostingMode));

    public GamePreset? CurrentPreset
        => !string.IsNullOrWhiteSpace(SelectedPresetKey)
            ? GamePresets.FindByKey(SelectedPresetKey)
            : GamePresets.All.FirstOrDefault(p => !string.IsNullOrWhiteSpace(SteamAppId) && p.SteamAppId == SteamAppId)
              ?? GamePresets.All.FirstOrDefault(p => p.GameType == GameType && p.GameType != GameType.SteamGeneric);

    public bool ShowVmSettings => HostingMode == HostingMode.Vm;
    public bool ShowRconSettings => CurrentPreset?.Capabilities.Rcon == true || GameType == GameType.Custom;
    public bool ShowLogSettings => CurrentPreset?.Capabilities.LiveLog == true || GameType == GameType.Custom;
    public bool ShowLifecycleSettings => HostingMode == HostingMode.BareMetal;
    public string ProfileNote => CurrentPreset?.Notes ?? "";
    public string StorageHint => CurrentPreset is { } p && p.DefaultSaveDirs.Length > 0
        ? "Backups use the configured save folders. Managed games are kept under the server folder where the game supports it."
        : "Set the folders that contain persistent world/save data.";

    public ServerEditorViewModel()
    {
        IsEdit = false;
        SelectedPresetKey = "custom";
    }

    public ServerEditorViewModel(DiscoveredServer discovered)
    {
        IsEdit = false;
        SelectedPresetKey = discovered.PresetKey;

        var preset = GamePresets.FindByKey(discovered.PresetKey);
        GuestExePath = discovered.ExePath;
        GuestWorkingDir = discovered.InstallPath;
        SteamAppId = discovered.SteamAppId;

        if (preset is not null && !string.IsNullOrEmpty(preset.DefaultWorkingDir))
        {
            RebasePresetPaths(preset.DefaultWorkingDir, discovered.InstallPath);
        }

        if (string.IsNullOrWhiteSpace(Name)) Name = discovered.DisplayName;
        Id = SlugifyName(discovered.DisplayName);
        NotifyUiRules();
    }

    public ServerEditorViewModel(ServerDef existing)
    {
        IsEdit = true;
        Id = existing.Id;
        Name = existing.Name;
        VmName = existing.VmName;
        HostingMode = existing.HostingMode;
        GameType = existing.GameType;
        GuestExePath = existing.GuestExePath;
        GuestWorkingDir = existing.GuestWorkingDir;
        StartArgsText = string.Join("\n", existing.StartArgs ?? Array.Empty<string>());
        SaveDirsText = string.Join("\n", existing.SaveDirs ?? Array.Empty<string>());
        SteamAppId = existing.SteamAppId ?? "";
        GuestCredentialId = existing.GuestCredentialId ?? "default";
        LogPathInGuest = existing.LogPathInGuest ?? "";
        RconHost = existing.RconHost ?? "auto";
        RconPort = existing.RconPort?.ToString() ?? "";
        RconPassword = existing.RconPassword ?? "";
        ScheduledTaskName = existing.ScheduledTaskName ?? "";
        StopProcessNamesText = string.Join("\n", existing.StopProcessNames ?? Array.Empty<string>());
        DiscordWebhookUrl = existing.DiscordWebhookUrl ?? "";
        selectedPresetKey = GamePresets.FindFor(existing)?.Key;
        NotifyUiRules();
    }

    partial void OnSelectedPresetKeyChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) { NotifyUiRules(); return; }
        var preset = GamePresets.FindByKey(value);
        if (preset is null) { NotifyUiRules(); return; }

        if (preset.Key != "custom")
        {
            GameType = preset.GameType;
            SteamAppId = preset.SteamAppId ?? "";
            GuestWorkingDir = preset.DefaultWorkingDir;
            GuestExePath = string.IsNullOrEmpty(preset.DefaultWorkingDir)
                ? preset.DefaultExeRelative
                : Path.Combine(preset.DefaultWorkingDir, preset.DefaultExeRelative);
            StartArgsText = string.Join("\n", preset.DefaultStartArgs);
            SaveDirsText = string.Join("\n", preset.DefaultSaveDirs);
            LogPathInGuest = preset.DefaultLogPath ?? "";
            RconHost = "auto";
            RconPort = preset.Capabilities.Rcon && preset.DefaultRconPort is int port ? port.ToString() : "";
            if (!preset.Capabilities.Rcon) RconPassword = "";

            if (string.IsNullOrWhiteSpace(Name)) Name = preset.Label;
            if (!IsEdit && string.IsNullOrWhiteSpace(Id)) Id = preset.Key;
        }
        NotifyUiRules();
    }

    partial void OnHostingModeChanged(HostingMode value) => NotifyUiRules();
    partial void OnGameTypeChanged(GameType value) => NotifyUiRules();
    partial void OnSteamAppIdChanged(string value) => NotifyUiRules();

    private void NotifyUiRules()
    {
        OnPropertyChanged(nameof(CurrentPreset));
        OnPropertyChanged(nameof(ShowVmSettings));
        OnPropertyChanged(nameof(ShowRconSettings));
        OnPropertyChanged(nameof(ShowLogSettings));
        OnPropertyChanged(nameof(ShowLifecycleSettings));
        OnPropertyChanged(nameof(ProfileNote));
        OnPropertyChanged(nameof(StorageHint));
    }

    private void RebasePresetPaths(string fromRoot, string toRoot)
    {
        var from = fromRoot.TrimEnd('\\', '/');
        var to = toRoot.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || from.Equals(to, StringComparison.OrdinalIgnoreCase)) return;

        StartArgsText = StartArgsText.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        SaveDirsText = SaveDirsText.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        LogPathInGuest = LogPathInGuest.Replace(from, to, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnNameChanged(string value)
    {
        if (!IsEdit && string.IsNullOrWhiteSpace(Id))
            Id = SlugifyName(value);
    }

    private static string SlugifyName(string s)
    {
        var lower = s.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "" : slug;
    }

    public bool TryBuild(out ServerDef def)
    {
        ValidationError = null;
        if (!IdPattern.IsMatch(Id))
        {
            ValidationError = "ID must be lowercase letters, digits, and hyphens.";
            def = default!;
            return false;
        }
        if (string.IsNullOrWhiteSpace(Name)) { ValidationError = "Name is required."; def = default!; return false; }
        if (HostingMode == HostingMode.Vm && string.IsNullOrWhiteSpace(VmName))
        {
            ValidationError = "VM name is required when running inside Hyper-V.";
            def = default!;
            return false;
        }
        if (string.IsNullOrWhiteSpace(GuestExePath)) { ValidationError = "Server EXE path is required."; def = default!; return false; }

        int? rconPortVal = null;
        if (ShowRconSettings && !string.IsNullOrWhiteSpace(RconPort))
        {
            if (!int.TryParse(RconPort.Trim(), out var p) || p <= 0 || p > 65535)
            {
                ValidationError = "RCON port must be 1-65535 or empty.";
                def = default!;
                return false;
            }
            rconPortVal = p;
        }

        def = new ServerDef(
            Id: Id.Trim(),
            Name: Name.Trim(),
            VmName: HostingMode == HostingMode.Vm ? VmName?.Trim() ?? "" : "",
            GameType: GameType,
            GuestExePath: GuestExePath.Trim(),
            GuestWorkingDir: string.IsNullOrWhiteSpace(GuestWorkingDir)
                ? Path.GetDirectoryName(GuestExePath.Trim()) ?? ""
                : GuestWorkingDir.Trim(),
            StartArgs: SplitLines(StartArgsText),
            SaveDirs: SplitLines(SaveDirsText),
            SteamAppId: string.IsNullOrWhiteSpace(SteamAppId) ? null : SteamAppId.Trim(),
            GuestCredentialId: HostingMode == HostingMode.Vm
                ? (string.IsNullOrWhiteSpace(GuestCredentialId) ? "default" : GuestCredentialId.Trim())
                : null,
            LogPathInGuest: ShowLogSettings && !string.IsNullOrWhiteSpace(LogPathInGuest) ? LogPathInGuest.Trim() : null,
            RconHost: ShowRconSettings ? (string.IsNullOrWhiteSpace(RconHost) ? "auto" : RconHost.Trim()) : null,
            RconPort: ShowRconSettings ? rconPortVal : null,
            RconPassword: ShowRconSettings && !string.IsNullOrWhiteSpace(RconPassword) ? RconPassword : null,
            HostingMode: HostingMode,
            ScheduledTaskName: HostingMode == HostingMode.BareMetal && !string.IsNullOrWhiteSpace(ScheduledTaskName) ? ScheduledTaskName.Trim() : null,
            StopProcessNames: HostingMode == HostingMode.BareMetal && SplitLines(StopProcessNamesText) is { Length: > 0 } sn ? sn : null,
            DiscordWebhookUrl: string.IsNullOrWhiteSpace(DiscordWebhookUrl) ? null : DiscordWebhookUrl.Trim());
        return true;
    }

    private static string[] SplitLines(string s)
        => string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
}
