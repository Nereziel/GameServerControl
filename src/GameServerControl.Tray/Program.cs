using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GameServerControl.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--copy-token", StringComparer.OrdinalIgnoreCase))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName
                ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            TrayApplicationContext.CopyAgentToken(root, allowElevation: false);
            return;
        }

        using var mutex = new Mutex(true, @"Local\GameServerControl.Tray", out var createdNew);
        if (!createdNew) return;

        var adminUrl = "http://localhost:5099/";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--url", StringComparison.OrdinalIgnoreCase))
                adminUrl = args[i + 1];
        }

        Application.Run(new TrayApplicationContext(adminUrl));
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "GameServerControlAgent";
    private const string RunValueName = "GameServerControlTray";
    private const string ReleasesApi = "https://api.github.com/repos/Nereziel/GameServerControl/releases/latest";

    private readonly string _adminUrl;
    private readonly string _installRoot;
    private readonly string _updateHelper;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _checkUpdateItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HttpClient _http;
    private readonly Dictionary<ServiceVisualState, Icon> _icons;
    private string? _pendingText;
    private bool _initialUpdateChecked;
    private bool _checkingForUpdates;
    private bool _startingUpdate;

    public TrayApplicationContext(string adminUrl)
    {
        _adminUrl = adminUrl.TrimEnd('/') + "/";
        _installRoot = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        _updateHelper = Path.Combine(_installRoot, "Update-GameServerControl.ps1");

        _icons = new Dictionary<ServiceVisualState, Icon>
        {
            [ServiceVisualState.Running] = CreateStatusIcon(Color.FromArgb(63, 185, 80)),
            [ServiceVisualState.Stopped] = CreateStatusIcon(Color.FromArgb(120, 128, 140)),
            [ServiceVisualState.Busy] = CreateStatusIcon(Color.FromArgb(210, 153, 34)),
            [ServiceVisualState.Unavailable] = CreateStatusIcon(Color.FromArgb(248, 81, 73))
        };

        _statusItem = new ToolStripMenuItem("Agent: checking…") { Enabled = false };
        var openItem = new ToolStripMenuItem("Open administration", null, (_, _) => OpenAdministration());
        var tokenItem = new ToolStripMenuItem("Copy Agent API token", null, (_, _) => CopyAgentToken(_installRoot, allowElevation: true));
        var manageTokensItem = new ToolStripMenuItem("Manage API tokens…", null, (_, _) => OpenAdministration(manageTokens: true));
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);

        _startItem = new ToolStripMenuItem("Start Agent", null, (_, _) => RunServiceAction("start"));
        _stopItem = new ToolStripMenuItem("Stop Agent", null, (_, _) => RunServiceAction("stop"));
        _restartItem = new ToolStripMenuItem("Restart Agent", null, (_, _) => RunServiceAction("restart"));
        _checkUpdateItem = new ToolStripMenuItem("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(showNoUpdate: true));
        _updateItem = new ToolStripMenuItem("Update GameServerControl…", null, async (_, _) => await StartUpdateAsync()) { Enabled = false };
        _autostartItem = new ToolStripMenuItem("Start tray with Windows") { CheckOnClick = true };
        _autostartItem.Checked = IsAutostartEnabled();
        _autostartItem.CheckedChanged += (_, _) => SetAutostart(_autostartItem.Checked);
        var exitItem = new ToolStripMenuItem("Exit tray icon", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            openItem,
            tokenItem,
            manageTokensItem,
            new ToolStripSeparator(),
            _startItem,
            _stopItem,
            _restartItem,
            new ToolStripSeparator(),
            _checkUpdateItem,
            _updateItem,
            new ToolStripSeparator(),
            _autostartItem,
            exitItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Icon = _icons[ServiceVisualState.Busy],
            Text = "GameServerControl - checking Agent"
        };
        _notifyIcon.DoubleClick += (_, _) => OpenAdministration();

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameServerControl-Tray", CurrentVersionString()));

        _timer = new System.Windows.Forms.Timer { Interval = 2000, Enabled = true };
        _timer.Tick += async (_, _) =>
        {
            RefreshServiceState();
            if (!_initialUpdateChecked)
            {
                _initialUpdateChecked = true;
                await CheckForUpdatesAsync(showNoUpdate: false);
            }
        };

        RefreshServiceState();
    }

    private void OpenAdministration(bool manageTokens = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo(manageTokens ? _adminUrl + "?manageTokens=1" : _adminUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not open administration", ex.Message, ToolTipIcon.Error);
        }
    }

    // Only the local, reversible bootstrap credential can be displayed again.
    // User-created tokens are stored as one-way hashes and shown on creation only.
    private static string ReadBootstrapToken(string installRoot)
    {
        var configPath = Path.Combine(installRoot, "Agent", "appsettings.json");
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!config.RootElement.TryGetProperty("Agent", out var agent) ||
            !agent.TryGetProperty("ApiToken", out var tokenValue))
            throw new InvalidDataException("Agent:ApiToken is missing from appsettings.json.");

        var stored = tokenValue.GetString();
        if (string.IsNullOrWhiteSpace(stored))
            throw new InvalidDataException("The Agent bootstrap token has not been generated yet.");
        const string prefix = "enc:v1:dpapi:";
        if (!stored.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (stored.StartsWith("enc:v1:", StringComparison.Ordinal))
                throw new InvalidOperationException("This credential uses a non-Windows encryption format.");
            return stored;
        }

        var cipher = Convert.FromBase64String(stored[prefix.Length..]);
        var entropy = Encoding.UTF8.GetBytes("GameServerControl:Secret:v1");
        var plaintext = ProtectedData.Unprotect(cipher, entropy, DataProtectionScope.LocalMachine);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal static void CopyAgentToken(string installRoot, bool allowElevation)
    {
        string token;
        try { token = ReadBootstrapToken(installRoot); }
        catch (UnauthorizedAccessException) when (allowElevation)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GameServerControl.Tray.exe"),
                    Arguments = "--copy-token",
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (Exception ex)
            {
                MessageBox.Show("Could not request administrator access: " + ex.Message,
                    "GameServerControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read the locally installed Agent token. " +
                "Verify the Agent installation and file permissions.\n\n" + ex.Message,
                "GameServerControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Clipboard.SetText(token);
            MessageBox.Show("Agent API token copied to clipboard. Other local applications may read clipboard contents.",
                "GameServerControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not copy Agent API token: " + ex.Message,
                "GameServerControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshServiceState()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            service.Refresh();
            var status = service.Status;
            var visual = status switch
            {
                ServiceControllerStatus.Running => ServiceVisualState.Running,
                ServiceControllerStatus.Stopped => ServiceVisualState.Stopped,
                ServiceControllerStatus.StartPending or
                ServiceControllerStatus.StopPending or
                ServiceControllerStatus.ContinuePending or
                ServiceControllerStatus.PausePending => ServiceVisualState.Busy,
                _ => ServiceVisualState.Busy
            };

            var statusText = _pendingText ?? status switch
            {
                ServiceControllerStatus.Running => "Running",
                ServiceControllerStatus.Stopped => "Stopped",
                ServiceControllerStatus.StartPending => "Starting…",
                ServiceControllerStatus.StopPending => "Stopping…",
                _ => status.ToString()
            };

            if (_pendingText is not null && status is ServiceControllerStatus.Running or ServiceControllerStatus.Stopped)
                _pendingText = null;

            SetVisualState(visual, statusText);
            _startItem.Enabled = status == ServiceControllerStatus.Stopped;
            _stopItem.Enabled = status == ServiceControllerStatus.Running;
            _restartItem.Enabled = status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            SetVisualState(ServiceVisualState.Unavailable, "Service not installed");
            _startItem.Enabled = _stopItem.Enabled = _restartItem.Enabled = false;
        }
        catch (Exception ex)
        {
            SetVisualState(ServiceVisualState.Unavailable, "Unavailable");
            _startItem.Enabled = _stopItem.Enabled = _restartItem.Enabled = false;
            Debug.WriteLine(ex);
        }
    }

    private void SetVisualState(ServiceVisualState state, string text)
    {
        _notifyIcon.Icon = _icons[state];
        _notifyIcon.Text = LimitTooltip($"GameServerControl - {text}");
        _statusItem.Text = $"Agent: {text}";
    }

    private void RunServiceAction(string action)
    {
        var command = action switch
        {
            "start" => $"Start-Service -Name '{ServiceName}' -ErrorAction Stop",
            "stop" => $"Stop-Service -Name '{ServiceName}' -Force -ErrorAction Stop",
            "restart" => $"$s=Get-Service -Name '{ServiceName}' -ErrorAction Stop; if ($s.Status -eq 'Running') {{ Restart-Service -Name '{ServiceName}' -Force -ErrorAction Stop }} else {{ Start-Service -Name '{ServiceName}' -ErrorAction Stop }}",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        try
        {
            StartElevatedPowerShell(command);
            _pendingText = action switch
            {
                "start" => "Starting…",
                "stop" => "Stopping…",
                _ => "Restarting…"
            };
            SetVisualState(ServiceVisualState.Busy, _pendingText);
            _startItem.Enabled = _stopItem.Enabled = _restartItem.Enabled = false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowBalloon("Action cancelled", "Administrator approval was cancelled.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ShowBalloon("Service action failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private static Version ReadInstalledAgentVersion(string installRoot, out string versionText)
    {
        var exe = Path.Combine(installRoot, "Agent", "GameServerControl.Agent.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Installed Agent executable was not found.", exe);
        versionText = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
        var version = ParseVersion(versionText);
        if (version is null || version.Build < 0)
            throw new InvalidDataException("Installed Agent does not report a usable version.");
        return version;
    }

    private async Task<(string Tag, Version Version)> FetchLatestReleaseAsync()
    {
        // Always resolve at click time. The cached menu label, Tray binary
        // version, and inherited GSC_VERSION may be stale or unrelated.
        using var response = await _http.GetAsync(ReleasesApi);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var tag = doc.RootElement.GetProperty("tag_name").GetString();
        var parsed = ParseVersion(tag);
        if (tag is null || parsed is null || parsed.Build < 0 ||
            !string.Equals(tag, "v" + parsed.ToString(3), StringComparison.Ordinal))
            throw new InvalidDataException("GitHub did not return a stable vMAJOR.MINOR.PATCH release.");
        return (tag, parsed);
    }

    private async Task StartUpdateAsync()
    {
        if (_startingUpdate || _checkingForUpdates) return;
        if (!File.Exists(_updateHelper))
        {
            ShowBalloon("Updater not found", _updateHelper, ToolTipIcon.Error);
            return;
        }

        _startingUpdate = true;
        _updateItem.Enabled = false;
        var started = false;
        try
        {
            // Resolve the release again before asking for consent. Do not
            // reinstall the running Agent or accidentally downgrade it.
            var (tag, latest) = await FetchLatestReleaseAsync();
            var installed = ReadInstalledAgentVersion(_installRoot, out var currentText);
            if (latest <= installed)
            {
                _updateItem.Text = "GameServerControl is up to date";
                ShowBalloon("No update needed", $"Installed Agent {currentText}; latest release {tag}.", ToolTipIcon.Info);
                return;
            }

            var confirmation = MessageBox.Show(
                $"Update GameServerControl Agent from {currentText} to {tag}?\n\n" +
                "The Agent will restart briefly. Existing game processes are not intentionally stopped.",
                "Update GameServerControl",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirmation != DialogResult.Yes) return;

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = _installRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(_updateHelper);
            psi.ArgumentList.Add("-FromTray");
            psi.ArgumentList.Add("-Tag");
            psi.ArgumentList.Add(tag);
            // Propagate the *selected* release through elevation and the
            // release-verified installer. Never trust inherited pin variables.
            psi.Environment["GSC_VERSION"] = tag;
            psi.Environment["GSC_REPO"] = "Nereziel/GameServerControl";
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("PowerShell updater did not start.");
            started = true;

            // The parent updater restores the Tray even if UAC is cancelled or
            // the Agent rollback gate rejects the new binary.
            ExitThread();
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not start update", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _startingUpdate = false;
            if (!started) _updateItem.Enabled = true;
        }
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdate)
    {
        if (_checkingForUpdates || _startingUpdate) return;
        _checkingForUpdates = true;
        _checkUpdateItem.Enabled = false;
        try
        {
            var (tag, latest) = await FetchLatestReleaseAsync();
            var installed = ReadInstalledAgentVersion(_installRoot, out var currentText);
            var available = latest > installed;
            _updateItem.Text = available ? $"Update Agent to {tag}…" : "GameServerControl is up to date";
            _updateItem.Enabled = available;
            if (available)
                ShowBalloon("GameServerControl update available", $"Agent {currentText} → {tag}", ToolTipIcon.Info);
            else if (showNoUpdate)
                ShowBalloon("GameServerControl", $"No newer release found. Installed Agent: {currentText}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _updateItem.Enabled = false;
            if (showNoUpdate)
                ShowBalloon("Update check failed", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _checkingForUpdates = false;
            _checkUpdateItem.Enabled = true;
        }
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out var v) ? v : null;
    }

    private static string CurrentVersionString()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static void StartElevatedPowerShell(string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(RunValueName) is string value && value.Contains("GameServerControl.Tray.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GameServerControl.Tray.exe");
                key.SetValue(RunValueName, $"\"{exe}\" --url \"{_adminUrl}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not change tray autostart", ex.Message, ToolTipIcon.Warning);
            _autostartItem.Checked = IsAutostartEnabled();
        }
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _http.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        base.ExitThreadCore();
    }

    private static string LimitTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static Icon CreateStatusIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var outer = new SolidBrush(Color.FromArgb(30, 30, 34));
            using var inner = new SolidBrush(color);
            graphics.FillEllipse(outer, 0, 0, 15, 15);
            graphics.FillEllipse(inner, 3, 3, 9, 9);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private enum ServiceVisualState
    {
        Running,
        Stopped,
        Busy,
        Unavailable
    }
}
