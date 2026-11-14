using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;
using System.Xml.Linq;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Creates / removes Windows Task Scheduler entries that request restart,
/// update and backup using the Agent's dedicated scheduled-maintenance command.
/// No bearer token is ever embedded in the task definition or process arguments.
/// </summary>
public sealed class MaintenanceScheduler
{
    private readonly LocalProcessService _local;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MaintenanceScheduler> _logger;

    public MaintenanceScheduler(LocalProcessService local, IConfiguration cfg, ILogger<MaintenanceScheduler> logger)
    {
        _local = local;
        _cfg = cfg;
        _logger = logger;
    }

    private string LocalApiBase
    {
        get
        {
            // Always use http://127.0.0.1:<port> for scheduled-task callbacks so they work without Tailscale.
            var bind = _cfg["Agent:Bind"] ?? "http://127.0.0.1:5099";
            try
            {
                var uri = new Uri(bind);
                return $"http://127.0.0.1:{uri.Port}";
            }
            catch { return "http://127.0.0.1:5099"; }
        }
    }

    public string TaskName(string serverId, string kind) => $"GSC-{serverId}-{kind}";

    public async Task ApplyAsync(string serverId, MaintenanceSchedule? schedule, CancellationToken ct = default)
    {
        if (!_local.HasScheduledTaskSupport)
        {
            // Linux: no Task Scheduler. Operators wire up scheduled restarts/updates/backups
            // via their own systemd timer units (or cron). The agent surfaces this as a clean
            // no-op rather than throwing — see SECURITY.md / README for the recipe.
            throw new PlatformNotSupportedException(
                "Windows Task Scheduler is unavailable on this platform. Configure systemd timers/cron explicitly.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(serverId, "^[a-z][a-z0-9-]{0,39}$"))
            throw new ArgumentException("Invalid server ID.");
        if (schedule is not null &&
            (schedule.DailyRestartHour is < 0 or > 23 ||
             schedule.WeeklyUpdateHour is < 0 or > 23 ||
             schedule.HourlyBackupMinute is < 0 or > 59))
            throw new ArgumentException("Schedule time is out of range.");

        // Snapshot existing definitions before changing any schedule. Restoring the
        // XML on failure prevents a bad task update from silently erasing an old plan.
        var taskNames = new[] { TaskName(serverId, "restart"),
            TaskName(serverId, "update"), TaskName(serverId, "backup") };
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in taskNames)
        {
            var (found, xml) = await RunSchtasksAsync(["/query", "/tn", task, "/xml"], ct);
            if (found)
            {
                // Reject invalid snapshots before performing any destructive action.
                _ = XDocument.Parse(xml);
                previous[task] = xml;
            }
        }

        try
        {
            foreach (var task in taskNames) await RemoveTaskAsync(task, ct);
            if (schedule is null) return;
            await ApplyNewTasksAsync(serverId, schedule, ct);
        }
        catch
        {
            await RestorePreviousTasksAsync(taskNames, previous);
            throw;
        }

    }

    private async Task ApplyNewTasksAsync(string serverId, MaintenanceSchedule schedule, CancellationToken ct)
    {
        if (schedule.DailyRestartEnabled)
        {
            await CreateAsync(
                TaskName(serverId, "restart"),
                $"Daily restart of {serverId}",
                trigger: $"/sc DAILY /st {schedule.DailyRestartHour:D2}:00",
                action: "restart",
                serverId: serverId,
                ct);
        }
        if (schedule.WeeklyUpdateEnabled)
        {
            var dayCode = schedule.WeeklyUpdateDay switch
            {
                DayOfWeek.Monday => "MON", DayOfWeek.Tuesday => "TUE", DayOfWeek.Wednesday => "WED",
                DayOfWeek.Thursday => "THU", DayOfWeek.Friday => "FRI", DayOfWeek.Saturday => "SAT", _ => "SUN"
            };
            await CreateAsync(
                TaskName(serverId, "update"),
                $"Weekly SteamCMD update of {serverId}",
                trigger: $"/sc WEEKLY /d {dayCode} /st {schedule.WeeklyUpdateHour:D2}:00",
                action: "update",
                serverId: serverId,
                ct);
        }
        if (schedule.HourlyBackupEnabled)
        {
            // /sc HOURLY /mo 1 /st HH:MM — first run starts at the next matching minute, then every hour.
            var firstHour = DateTime.Now.Hour;
            if (DateTime.Now.Minute >= schedule.HourlyBackupMinute) firstHour = (firstHour + 1) % 24;
            await CreateAsync(
                TaskName(serverId, "backup"),
                $"Hourly backup of {serverId}",
                trigger: $"/sc HOURLY /mo 1 /st {firstHour:D2}:{schedule.HourlyBackupMinute:D2}",
                action: "backup",
                serverId: serverId,
                ct);
        }
    }

    private async Task RestorePreviousTasksAsync(
        IEnumerable<string> taskNames, IReadOnlyDictionary<string, string> previous)
    {
        var errors = new List<string>();
        foreach (var task in taskNames)
        {
            if (!previous.TryGetValue(task, out var xml))
            {
                var (exists, _) = await RunSchtasksAsync(["/query", "/tn", task], CancellationToken.None);
                if (exists)
                {
                    var (deleted, _) = await RunSchtasksAsync(["/delete", "/f", "/tn", task], CancellationToken.None);
                    if (!deleted) errors.Add(task);
                }
                continue;
            }

            // Windows installer protects the Agent directory with SYSTEM and
            // Administrators ACLs. Older task XML may still contain legacy tokens,
            // so never write an XML snapshot into the user TEMP directory.
            var temporary = Path.Combine(AppContext.BaseDirectory,
                ".gsc-schedule-rollback-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                XDocument.Parse(xml).Save(temporary);
                var (ok, _) = await RunSchtasksAsync(
                    ["/create", "/f", "/tn", task, "/xml", temporary], CancellationToken.None);
                if (!ok) errors.Add(task);
            }
            catch { errors.Add(task); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
        if (errors.Count > 0)
            throw new InvalidOperationException("Schedule update failed and old definitions could not be restored: " +
                string.Join(", ", errors));
    }

    private async Task CreateAsync(string taskName, string description, string trigger, string action, string serverId, CancellationToken ct)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "GameServerControl.Agent.exe");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Installed Agent executable is required for scheduled maintenance.");

        var command = $"\"{executable}\" --scheduled-maintenance {action} {serverId}";
        var options = trigger.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = new List<string> { "/create", "/f", "/tn", taskName, "/tr", command };
        args.AddRange(options);
        args.AddRange(["/ru", "SYSTEM", "/rl", "HIGHEST"]);
        var (ok, output) = await RunSchtasksAsync(args, ct);
        if (!ok)
            throw new InvalidOperationException($"Could not create scheduled task {taskName}: {output}");
    }

    private async Task RemoveTaskAsync(string taskName, CancellationToken ct)
    {
        var (ok, output) = await RunSchtasksAsync(["/delete", "/f", "/tn", taskName], ct);
        if (!ok && !output.Contains("cannot find", StringComparison.OrdinalIgnoreCase) &&
            !output.Contains("does not exist", StringComparison.OrdinalIgnoreCase) &&
            !output.Contains("system cannot find", StringComparison.OrdinalIgnoreCase))
        {
            // /query does not mutate and distinguishes missing tasks from genuine
            // permission/service errors (independent of localized error text).
            var (exists, _) = await RunSchtasksAsync(["/query", "/tn", taskName], ct);
            if (exists) throw new InvalidOperationException($"Could not remove scheduled task {taskName}: {output}");
        }
    }

    public async Task<MaintenanceSchedule?> ReadAsync(string serverId, CancellationToken ct = default)
    {
        if (!_local.HasScheduledTaskSupport) return null; // Linux: not supported here
        // Query the three task names; reconstruct best-effort schedule.
        var schedule = new MaintenanceSchedule();
        bool found = false;

        var restart = await ReadTaskScheduleAsync(TaskName(serverId, "restart"), ct);
        if (restart.Start is DateTime r) { found = true; schedule = schedule with { DailyRestartEnabled = true, DailyRestartHour = r.Hour }; }

        var upd = await ReadTaskScheduleAsync(TaskName(serverId, "update"), ct);
        if (upd.Start is DateTime u) { found = true; schedule = schedule with {
            WeeklyUpdateEnabled = true, WeeklyUpdateHour = u.Hour,
            WeeklyUpdateDay = upd.Day ?? schedule.WeeklyUpdateDay }; }

        var bk = await ReadTaskScheduleAsync(TaskName(serverId, "backup"), ct);
        if (bk.Start is DateTime b) { found = true; schedule = schedule with { HourlyBackupEnabled = true, HourlyBackupMinute = b.Minute }; }

        return found ? schedule : null;
    }

    private async Task<(DateTime? Start, DayOfWeek? Day)> ReadTaskScheduleAsync(string taskName, CancellationToken ct)
    {
        var (ok, output) = await RunSchtasksAsync(["/query", "/tn", taskName, "/xml"], ct);
        return ok ? ParseTaskXml(output) : (null, null);
    }

    // Task Scheduler XML is language-independent; LIST /v text is not.
    public static (DateTime? Start, DayOfWeek? Day) ParseTaskXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var start = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "StartBoundary")?.Value;
            if (!DateTime.TryParse(start, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
                return (null, null);
            var days = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "DaysOfWeek");
            var name = days?.Elements().FirstOrDefault()?.Name.LocalName;
            var day = Enum.TryParse<DayOfWeek>(name, ignoreCase: true, out var parsed) ? parsed : (DayOfWeek?)null;
            return (date, day);
        }
        catch (System.Xml.XmlException) { return (null, null); }
    }

    private static async Task<(bool Ok, string Output)> RunSchtasksAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return (false, "schtasks.exe did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("Windows Task Scheduler timed out.");
            }
            return (process.ExitCode == 0, (await stdout) + (await stderr));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Task Scheduler executable unavailable: " + ex.Message, ex);
        }
    }
}
