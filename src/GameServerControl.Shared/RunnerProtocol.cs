using System.Security.Cryptography;
using System.Text;

namespace GameServerControl.Shared;

public static class RunnerProtocolLimits
{
    public const int MaxRequestBytes = 16 * 1024;
    public const int MaxResponseBytes = 512 * 1024;
    public const int MaxConsoleInputChars = 4096;
    public const int MaxConsoleLines = 400;
    public const int MaxConsoleChars = 200_000;
    public const int MaxConsoleLineChars = 16_384;
}

public sealed record RunnerIdentityInfo(
    string Key,
    string ServiceName,
    string PipeName,
    string StateDirectoryName);

public static class RunnerIdentity
{
    public static RunnerIdentityInfo ForServer(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            throw new ArgumentException("Server ID is required.", nameof(serverId));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(serverId));
        var key = Convert.ToHexString(digest).ToLowerInvariant()[..16];
        return new RunnerIdentityInfo(
            key,
            "GameServerControlRunner_" + key,
            "gsc-runner-" + key,
            key);
    }
}

public sealed record RunnerManifest(
    string ServerId,
    string ExecutablePath,
    string WorkingDirectory,
    string[] Arguments,
    bool InteractiveConsole,
    string? SteamAppId = null,
    string[]? StopProcessNames = null,
    bool AutoStart = false)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerId)) return "ServerId is required.";
        if (!IsAbsoluteWindowsPath(ExecutablePath)) return "ExecutablePath must be an absolute local Windows path.";
        if (!IsAbsoluteWindowsPath(WorkingDirectory)) return "WorkingDirectory must be an absolute local Windows path.";
        if (Arguments is null) return "Arguments are required.";
        if (StopProcessNames is { Length: > 64 }) return "Too many additional process names.";
        if (StopProcessNames is not null)
        {
            foreach (var raw in StopProcessNames)
            {
                if (string.IsNullOrWhiteSpace(raw)) return "Additional process names must not be blank.";
                var leaf = Path.GetFileName(raw);
                if (!string.Equals(leaf, raw, StringComparison.Ordinal))
                    return "Additional process names must be file names, not paths.";
            }
        }
        return null;
    }

    private static bool IsAbsoluteWindowsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3) return false;
        return char.IsLetter(path[0])
               && path[1] == ':'
               && (path[2] == '\\' || path[2] == '/');
    }
}

public enum RunnerCommand
{
    Ping,
    Start,
    Find,
    Stop,
    ConsoleState,
    SendConsoleInput,
    KillExtras
}

public sealed record RunnerRequest(
    RunnerCommand Command,
    bool Force = false,
    string? ConsoleInput = null)
{
    public string? Validate()
    {
        if (!Enum.IsDefined(Command)) return "Unknown runner command.";
        if (Command != RunnerCommand.SendConsoleInput)
            return ConsoleInput is null ? null : "ConsoleInput is only valid for SendConsoleInput.";

        if (string.IsNullOrWhiteSpace(ConsoleInput)) return "Console input is empty.";
        if (ConsoleInput.Length > RunnerProtocolLimits.MaxConsoleInputChars) return "Console input is too long.";
        if (ConsoleInput.Contains('\r') || ConsoleInput.Contains('\n')) return "Send one console command at a time.";
        return null;
    }
}

public sealed record RunnerConsoleLine(
    long Sequence,
    DateTimeOffset At,
    string Source,
    string Text);

public sealed record RunnerConsoleSnapshot(
    bool Supported,
    bool InputAvailable,
    string Message,
    long LastSequence,
    RunnerConsoleLine[] Lines);

public sealed record RunnerResponse(
    bool Ok,
    string? Error = null,
    int? Pid = null,
    bool InputAvailable = false,
    RunnerConsoleSnapshot? Console = null,
    string? LocalApplicationData = null);
