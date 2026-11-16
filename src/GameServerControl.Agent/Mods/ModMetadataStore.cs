using System.Text.Json;

namespace GameServerControl.Agent.Mods;

/// <summary>Atomically persists mod sidecar JSON next to the existing metadata.</summary>
public static class ModMetadataStore
{
    // Never overwrite corrupt metadata for other mods with an empty sidecar.
    public static Dictionary<string, T> ReadForUpdate<T>(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Mod sidecar metadata is invalid.");
        return new(parsed, StringComparer.OrdinalIgnoreCase);
    }

    public static void WriteAtomic(string path, string json)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("Metadata path must have a parent.");
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, ".gsc-metadata-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(pending, json);
            File.Move(pending, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); }
            catch { /* original write failure is more relevant */ }
        }
    }
}
