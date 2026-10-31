using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class ServerViewModel : ObservableObject
{
    public sealed record Callbacks(
        Action<ServerViewModel> Edit,
        Action<ServerViewModel> Delete,
        Action<ServerViewModel> Configure,
        Action<ServerViewModel> Console,
        Action<ServerViewModel> Log,
        Action<ServerViewModel>? Mods = null,
        Action<ServerViewModel>? Backups = null,
        Action<ServerViewModel>? Schedule = null,
        Action<ServerViewModel>? Stats = null,
        Action<ServerViewModel>? Players = null);

    private readonly Func<AgentClient?> _getClient;
    private readonly Action<string> _toast;
    private readonly Callbacks _cb;

    public ServerDef Def { get; private set; }
    public string AgentId { get; }
    public string AgentNickname { get; }
    public bool HasMultipleAgents { get; set; }

    public ServerViewModel(ServerDef def, Func<AgentClient?> getClient, Action<string> toast, Callbacks callbacks,
        string agentId = "", string agentNickname = "")
    {
        _getClient = getClient;
        _toast = toast;
        _cb = callbacks;
        Def = def;
        Id = def.Id;
        Name = def.Name;
        VmName = def.VmName;
        GameType = def.GameType.ToString();
        AgentId = agentId;
        AgentNickname = agentNickname;
    }

    public GameProfile? Profile => GameProfiles.FindFor(Def);
    public GameCapabilities Capabilities => Profile?.Capabilities ?? new GameCapabilities(
        ManagedConfig: Def.GameType == GameType.Custom,
        Rcon: Def.GameType == GameType.Custom,
        LiveLog: !string.IsNullOrWhiteSpace(Def.LogPathInGuest));
    public string GameLabel => Profile?.Label ?? GameType;
    public string GameIcon => Profile?.Icon ?? "🖥";
    public bool SupportsManagedConfig => Capabilities.ManagedConfig;
    public bool SupportsMods => Capabilities.Mods;
    public bool SupportsRcon => Capabilities.Rcon;
    public bool SupportsPlayerManagement => Capabilities.PlayerManagement;
    public bool SupportsLiveLog => Capabilities.LiveLog;
    public bool HasRcon => SupportsRcon && Def.RconPort is > 0 && !string.IsNullOrWhiteSpace(Def.RconPassword);
    public bool HasPlayers => HasRcon && SupportsPlayerManagement;
    public bool HasScheduledTask => !string.IsNullOrWhiteSpace(Def.ScheduledTaskName);
    public bool HasLogPath => SupportsLiveLog && !string.IsNullOrWhiteSpace(Def.LogPathInGuest);

    [RelayCommand] private void Edit() => _cb.Edit(this);
    [RelayCommand] private void Delete() => _cb.Delete(this);
    [RelayCommand] private void Configure() => _cb.Configure(this);
    [RelayCommand] private void Console() => _cb.Console(this);
    [RelayCommand] private void Log() => _cb.Log(this);
    [RelayCommand] private void Mods() => _cb.Mods?.Invoke(this);
    [RelayCommand] private void Backups() => _cb.Backups?.Invoke(this);
    [RelayCommand] private void Schedule() => _cb.Schedule?.Invoke(this);
    [RelayCommand] private void Stats() => _cb.Stats?.Invoke(this);
    [RelayCommand] private void Players() => _cb.Players?.Invoke(this);

    public const string DonateUrl = "https://paypal.me/TKECLIPSE";

    [ObservableProperty] private bool isAutostartEnabled;
    [ObservableProperty] private bool isAutostartLoading;

    public async Task LoadAutostartAsync()
    {
        var c = _getClient();
        if (c is null || !HasScheduledTask) return;
        IsAutostartLoading = true;
        try
        {
            var s = await c.GetAutostartAsync(Id);
            if (s.Supported && s.Enabled is bool b) IsAutostartEnabled = b;
        }
        catch { }
        finally { IsAutostartLoading = false; }
    }

    public async Task SetAutostartAsync(bool desired)
    {
        var c = _getClient();
        if (c is null || !HasScheduledTask) return;
        IsAutostartLoading = true;
        try
        {
            await c.SetAutostartAsync(Id, desired);
            IsAutostartEnabled = desired;
            _toast($"[{Name}] Autostart {(desired ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            _toast($"[{Name}] Autostart change failed: {ex.Message}");
        }
        finally { IsAutostartLoading = false; }
    }

    public string Id { get; }
    public string Name { get; }
    public string VmName { get; }
    public string GameType { get; }

    [ObservableProperty] private VmState vmState = VmState.Unknown;
    [ObservableProperty] private ProcessState processState = ProcessState.Unknown;
    [ObservableProperty] private int? pidInGuest;
    [ObservableProperty] private DateTimeOffset? startedAt;
    [ObservableProperty] private DateTimeOffset? lastBackupAt;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private double? cpuPercent;
    [ObservableProperty] private long? memoryMB;
    [ObservableProperty] private string? inviteCode;

    public bool HasInviteCode => !string.IsNullOrEmpty(InviteCode);
    public bool HasCpuRam => CpuPercent is not null || MemoryMB is not null;
    public string CpuRamText
    {
        get
        {
            var c = CpuPercent is double cp ? $"{cp:0.#}% CPU" : "";
            var m = MemoryMB is long mb ? $"{mb:N0} MB" : "";
            return string.Join(" · ", new[] { c, m }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    public bool IsOn => ProcessState == ProcessState.Running;
    public bool IsBareMetal => Def.HostingMode == HostingMode.BareMetal;
    public string HostingLabel => IsBareMetal ? "This machine" : ("Hyper-V: " + VmName);

    public string VmBadge => VmState switch
    {
        VmState.Running => "VM RUNNING",
        VmState.Off => "VM OFF",
        VmState.Starting => "VM STARTING",
        VmState.Stopping => "VM STOPPING",
        VmState.Saved => "VM SAVED",
        VmState.Paused => "VM PAUSED",
        VmState.NotApplicable => "LOCAL",
        _ => "VM ?"
    };

    public string ProcBadge => ProcessState switch
    {
        ProcessState.Running => "RUNNING" + (PidInGuest is int p ? $" · pid {p}" : ""),
        ProcessState.NotRunning => "STOPPED",
        ProcessState.Starting => "STARTING",
        ProcessState.Stopping => "STOPPING",
        _ => "UNKNOWN"
    };

    public string VmBadgeColor => VmState == VmState.Running ? "#3FB950" :
        VmState == VmState.Off ? "#6E7681" :
        VmState == VmState.Unknown ? "#8B939F" : "#58A6FF";

    public string ProcBadgeColor => ProcessState == ProcessState.Running ? "#3FB950" :
        ProcessState == ProcessState.NotRunning ? "#6E7681" :
        ProcessState == ProcessState.Unknown ? "#8B939F" : "#58A6FF";

    public void ApplyStatus(ServerStatus s)
    {
        VmState = s.VmState;
        ProcessState = s.ProcessState;
        PidInGuest = s.PidInGuest;
        StartedAt = s.StartedAt;
        LastBackupAt = s.LastBackupAt;
        LastError = s.LastError;
        CpuPercent = s.CpuPercent;
        MemoryMB = s.MemoryMB;
        if (s.Metadata is not null && s.Metadata.TryGetValue("InviteCode", out var inv)) InviteCode = inv;
        else if (s.Metadata is not null) InviteCode = null;
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(VmBadge));
        OnPropertyChanged(nameof(ProcBadge));
        OnPropertyChanged(nameof(VmBadgeColor));
        OnPropertyChanged(nameof(ProcBadgeColor));
        OnPropertyChanged(nameof(HasInviteCode));
        OnPropertyChanged(nameof(HasCpuRam));
        OnPropertyChanged(nameof(CpuRamText));
    }

    [RelayCommand] private Task Start() => Invoke(ServerActionKind.Start, "Starting…");
    [RelayCommand] private Task Stop() => Invoke(ServerActionKind.Stop, "Stopping…");
    [RelayCommand] private Task Restart() => Invoke(ServerActionKind.Restart, "Restarting…");
    [RelayCommand] private Task Backup() => Invoke(ServerActionKind.Backup, "Creating backup…");
    [RelayCommand] private Task Update() => Invoke(ServerActionKind.Update, "Running SteamCMD update…");
    [RelayCommand] private Task ApplyConfig() => Invoke(ServerActionKind.ApplyConfig, "Applying config (restart)…");

    public async Task ToggleAsync(bool desired)
    {
        if (desired && !IsOn) await Invoke(ServerActionKind.Start, "Starting…");
        else if (!desired && IsOn) await Invoke(ServerActionKind.Stop, "Stopping…");
    }

    private async Task Invoke(ServerActionKind kind, string busyMessage)
    {
        var client = _getClient();
        if (client is null) { _toast("Not connected to agent. Open settings to configure."); return; }
        if (IsBusy) return;
        IsBusy = true;
        _toast($"[{Name}] {busyMessage}");
        try
        {
            var r = await client.ActionAsync(Id, kind);
            _toast($"[{Name}] {(r.Success ? "OK" : "FAIL")}: {r.Message}");
            if (HasScheduledTask && kind is ServerActionKind.Start or ServerActionKind.Stop or ServerActionKind.Restart)
                await LoadAutostartAsync();
        }
        catch (Exception ex)
        {
            _toast($"[{Name}] ERROR: {ex.Message}");
        }
        finally { IsBusy = false; }
    }
}
