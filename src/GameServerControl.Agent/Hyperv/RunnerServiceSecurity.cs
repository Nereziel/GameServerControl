namespace GameServerControl.Agent.Hyperv;

/// <summary>
/// Commands for assigning per-service access to the installed Runner payload.
/// The installer may protect existing EXE/DLL files from inherited ACLs, so granting
/// only on the directory does not make them executable by a newly created service.
/// </summary>
public static class RunnerServiceSecurity
{
    public static string[] PayloadDirectoryGrant(string runnerRoot, string serviceAccount) =>
        [runnerRoot, "/grant", serviceAccount + ":(OI)(CI)(RX)"];

    public static string[] ExistingPayloadGrant(string runnerRoot, string serviceAccount) =>
        [runnerRoot, "/grant", serviceAccount + ":(RX)", "/T"];

    public static string[] PayloadRevoke(string runnerRoot, string serviceAccount) =>
        [runnerRoot, "/remove:g", serviceAccount, "/T"];

    public static bool HasExpectedAccount(string serviceConfig, string expectedAccount)
    {
        if (string.IsNullOrWhiteSpace(serviceConfig) || string.IsNullOrWhiteSpace(expectedAccount))
            return false;

        foreach (var line in serviceConfig.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator < 0 ||
                !string.Equals(line[..separator].Trim(), "SERVICE_START_NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            return string.Equals(line[(separator + 1)..].Trim(), expectedAccount, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
