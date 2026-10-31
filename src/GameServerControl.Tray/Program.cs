using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GameServerControl.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\GameServerControl.Tray", out var createdNew);
        if (!createdNew) return;

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);

        _startItem = new ToolStripMenuItem("Start Agent", null, (_, _) => RunServiceAction("start"));
        _stopItem = new ToolStripMenuItem("Stop Agent", null, (_, _) => RunServiceAction("stop"));
        _restartItem = new ToolStripMenuItem("Restart Agent", null, (_, _) => RunServiceAction("restart"));
        _checkUpdateItem = new ToolStripMenuItem("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(showNoUpdate: true));
        _updateItem = new ToolStripMenuItem("Update GameServerControl…", null, (_, _) => StartUpdate());
        _autostartItem = new ToolStripMenuItem("Start tray with Windows") { CheckOnClick = true };
        _autostartItem.Checked = IsAutostartEnabled();
        _autostartItem.CheckedChanged += (_, _) => SetAutostart(_autostartItem.Checked);
        var exitItem = new ToolStripMenuItem("Exit tray icon", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            openItem,
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

    private void OpenAdministration()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_adminUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not open administration", ex.Message, ToolTipIcon.Error);
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

    private void StartUpdate()
    {
        if (!File.Exists(_updateHelper))
        {
            ShowBalloon("Updater not found", _updateHelper, ToolTipIcon.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{_updateHelper}\" -FromTray",
                WorkingDirectory = _installRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            // The non-elevated updater helper owns the rest of the update. Exiting avoids locking
            // GameServerControl.Tray.exe while the elevated installer replaces the Tray directory.
            ExitThread();
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not start updater", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task CheckForUpdatesAsync(bool showNoUpdate)
    {
        try
        {
            _checkUpdateItem.Enabled = false;
            using var response = await _http.GetAsync(ReleasesApi);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tag)) return;

            var current = ParseVersion(CurrentVersionString());
            var latest = ParseVersion(tag);
            var updateAvailable = current is not null && latest is not null && latest > current;

            _updateItem.Text = updateAvailable ? $"Update to {tag}…" : "Update GameServerControl…";
            if (updateAvailable)
                ShowBalloon("GameServerControl update available", $"{CurrentVersionString()} → {tag}", ToolTipIcon.Info);
            else if (showNoUpdate)
                ShowBalloon("GameServerControl", $"No newer release found. Installed: {CurrentVersionString()}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            if (showNoUpdate) ShowBalloon("Update check failed", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
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
