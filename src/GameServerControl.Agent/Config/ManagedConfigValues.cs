using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// INI and dynamic configuration readers preserve vendor key casing. JSON object
/// names are case-sensitive in JavaScript, so map discovered values back to the
/// canonical schema keys before returning the managed configuration to WebUI.
/// </summary>
public static class ManagedConfigValues
{
    public static Dictionary<string, string> Canonicalize(
        ConfigSchema schema, IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            result[key] = value;
            lookup[key] = value;
        }

        foreach (var field in schema.AllFields)
        {
            if (!lookup.TryGetValue(field.Key, out var value)) continue;
            foreach (var variant in result.Keys.Where(k =>
                string.Equals(k, field.Key, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(k, field.Key, StringComparison.Ordinal)).ToArray())
                result.Remove(variant);
            result[field.Key] = value;
        }

        return result;
    }
}
