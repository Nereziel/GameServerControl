using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed record MinecraftInstallRequest(
    string ProfileKey, string InstallPath, string ServerId, string Name,
    string? Version = null, int MemoryMb = 4096, int Port = 0,
    bool AcceptEula = false, bool AcceptBedrockTerms = false);

/// <summary>
/// Downloads Minecraft dedicated-server files from Mojang, PaperMC, FabricMC
/// or the official Bedrock release service. Never accepts arbitrary download
/// URLs from a caller. A fresh sibling staging directory is committed only
/// when the entire download/extraction and runtime preflight have succeeded.
/// </summary>
public sealed class MinecraftServerPackages
{
    private readonly HttpClient _http;
    private readonly ILogger<MinecraftServerPackages> _logger;

    public MinecraftServerPackages(IHttpClientFactory httpFactory, ILogger<MinecraftServerPackages> logger)
    {
        _http = httpFactory.CreateClient("minecraft-installer");
        _logger = logger;
    }

    public static string? Validate(MinecraftInstallRequest request)
    {
        if (request is null) return "Installation request is required.";
        var profile = GameProfiles.FindByKey(request.ProfileKey);
        if (profile is null || profile.GameType != GameType.Minecraft || !profile.Installable)
            return "Unsupported Minecraft server type.";
        if (string.IsNullOrWhiteSpace(request.ServerId) ||
            !Regex.IsMatch(request.ServerId, "^[a-z0-9][a-z0-9-]{0,63}$"))
            return "Server ID must contain lowercase letters, digits or hyphens.";
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
            return "Server name is required and must be at most 100 characters.";
        if (string.IsNullOrWhiteSpace(request.InstallPath) || !Path.IsPathFullyQualified(request.InstallPath))
            return "Installation path must be absolute.";
        if (request.InstallPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "Invalid installation path.";
        var root = Path.GetFullPath(request.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(Path.GetDirectoryName(root)) || root == Path.GetPathRoot(root))
            return "Installing at a filesystem root is not allowed.";
        if (Directory.Exists(root) || File.Exists(root))
            return "Install folder already exists. Choose an unused folder to protect existing data.";
        if (request.Version is { Length: > 0 } v && !Regex.IsMatch(v, "^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$"))
            return "Invalid Minecraft version identifier.";
        if (request.MemoryMb is < 512 or > 65536) return "Java RAM must be between 512 and 65536 MB.";
        var port = request.Port == 0 ? profile.DefaultPorts![0].Port : request.Port;
        if (port is < 1024 or > 65535) return "Game port must be between 1024 and 65535.";
        if (profile.Key == "minecraft-bedrock" && !request.AcceptBedrockTerms)
            return "Accept the Bedrock Dedicated Server license terms before downloading.";
        if (profile.Key != "minecraft-bedrock" && !request.AcceptEula)
            return "Accept the Minecraft Java EULA before installing.";
        if (profile.Key == "minecraft-bedrock" && !OperatingSystem.IsWindows() && !IsSupportedBedrockLinux())
            return "Official Bedrock Linux server requires Ubuntu 22.04 LTS or newer.";
        return null;
    }

    public async Task<(ServerDef Definition, string Version)> InstallAsync(
        MinecraftInstallRequest req, Action<string> progress, CancellationToken ct)
    {
        var problem = Validate(req);
        if (problem is not null) throw new ArgumentException(problem);
        var profile = GameProfiles.FindByKey(req.ProfileKey)!;
        var root = Path.GetFullPath(req.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        // Do not follow a pre-existing final directory or overwrite existing worlds.
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("Install directory already exists.");
        var stage = Path.Combine(parent, ".gsc-minecraft-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var committed = false;
        try
        {
            progress("Resolving official Minecraft server download…");
            var (download, version, sha1, sha256) = await ResolveAsync(profile.Key, req.Version, ct);
            var artifact = Path.Combine(stage, profile.Key == "minecraft-bedrock" ? "bedrock-package.zip" : "server.jar");
            progress("Downloading Minecraft " + profile.Label + " " + version);
            await DownloadAsync(download, artifact, sha1, sha256, ct);

            if (profile.Key == "minecraft-bedrock")
            {
                progress("Extracting Bedrock Dedicated Server…");
                ExtractSafeZip(artifact, stage);
                File.Delete(artifact);
                var exe = Path.Combine(stage, OperatingSystem.IsWindows() ? "bedrock_server.exe" : "bedrock_server");
                if (!File.Exists(exe)) throw new InvalidDataException("Bedrock server executable missing from official archive.");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
            else
            {
                // The user has explicitly accepted Mojang's EULA in the UI/API.
                File.WriteAllText(Path.Combine(stage, "eula.txt"), "eula=true\n");
            }

            // The generated server.properties exists on Bedrock installs; for
            // Java install it is safe to write the initial file before first start.
            var port = req.Port == 0 ? profile.DefaultPorts![0].Port : req.Port;
            UpdateServerProperty(Path.Combine(stage, "server.properties"), "server-port", port.ToString());
            if (profile.Key != "minecraft-bedrock")
            {
                UpdateServerProperty(Path.Combine(stage, "server.properties"), "online-mode", "true");
                UpdateServerProperty(Path.Combine(stage, "server.properties"), "enable-rcon", "false");
            }
            var exePath = profile.Key == "minecraft-bedrock"
                ? profile.BuildExecutablePath(root)
                : ResolveJavaExecutable();
            if (exePath is null)
                throw new InvalidOperationException("Java runtime was not found. Install a compatible Java version and configure JAVA_HOME/PATH, then retry.");
            var args = profile.Key == "minecraft-bedrock" ? Array.Empty<string>() : new[]
            {
                "-Xms512M", "-Xmx" + req.MemoryMb + "M", "-jar", "server.jar", "nogui"
            };
            var def = new ServerDef(
                req.ServerId, req.Name, "", GameType.Minecraft, exePath, root, args,
                profile.BuildSaveDirs(root), null, null, profile.BuildLogPath(root),
                HostingMode: HostingMode.BareMetal,
                NetworkPorts: new[] { new NetworkPort("Game",
                    profile.Key == "minecraft-bedrock" ? NetworkProtocol.Udp : NetworkProtocol.Tcp, port) },
                GameProfileKey: profile.Key);

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Destination appeared during installation.");
            Directory.Move(stage, root);
            committed = true;
            progress("Minecraft server installed. Registering with GameServerControl…");
            return (def, version);
        }
        finally
        {
            if (!committed && Directory.Exists(stage))
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not clean Minecraft install stage {Path}", stage); }
        }
    }

    private async Task<(Uri Url, string Version, string? Sha1, string? Sha256)> ResolveAsync(
        string profileKey, string? requested, CancellationToken ct)
    {
        if (profileKey == "minecraft-bedrock")
        {
            var root = await GetJsonAsync("https://net-secondary.web.minecraft-services.net/api/v1.0/download/links", ct);
            var type = OperatingSystem.IsWindows() ? "serverBedrockWindows" : "serverBedrockLinux";
            var link = root["result"]?["links"]?.AsArray()
                .FirstOrDefault(item => (string?)item?["downloadType"] == type);
            var uri = VerifiedUrl((string?)link?["downloadUrl"], "www.minecraft.net", "/bedrockdedicatedserver/");
            if (!uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                (OperatingSystem.IsWindows() ? !uri.AbsolutePath.Contains("/bin-win/") : !uri.AbsolutePath.Contains("/bin-linux/")))
                throw new InvalidDataException("Unexpected official Bedrock download URL.");
            return (uri, Path.GetFileNameWithoutExtension(uri.AbsolutePath).Replace("bedrock-server-", ""), null, null);
        }

        var version = string.IsNullOrWhiteSpace(requested) ? await LatestJavaReleaseAsync(ct) : requested;
        if (profileKey == "minecraft")
        {
            var manifest = await GetJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
            var selected = manifest["versions"]?.AsArray().FirstOrDefault(item =>
                (string?)item?["id"] == version && (string?)item?["type"] == "release");
            if (selected is null) throw new InvalidOperationException("Requested Minecraft Java release not found.");
            var versionUri = VerifiedUrl((string?)selected["url"], "piston-meta.mojang.com", "/v1/");
            var details = await GetJsonAsync(versionUri.ToString(), ct);
            var server = details["downloads"]?["server"];
            var url = VerifiedUrl((string?)server?["url"], "piston-data.mojang.com", "/v1/");
            return (url, version, (string?)server?["sha1"], null);
        }
        if (profileKey == "minecraft-fabric")
        {
            var loaders = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader", ct);
            var installers = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/installer", ct);
            var loader = loaders.AsArray().FirstOrDefault(x => (bool?)x?["stable"] == true)?["version"]?.ToString();
            var installer = installers.AsArray().FirstOrDefault(x => (bool?)x?["stable"] == true)?["version"]?.ToString();
            if (loader is null || installer is null) throw new InvalidOperationException("Fabric stable loader/installer unavailable.");
            return (new Uri("https://meta.fabricmc.net/v2/versions/loader/" +
                Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(loader) + "/" +
                Uri.EscapeDataString(installer) + "/server/jar"), version, null, null);
        }
        if (profileKey == "minecraft-paper")
        {
            var versions = requested is { Length: > 0 }
                ? new[] { requested }
                : (await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", ct))["versions"]!
                    .AsObject().SelectMany(group => group.Value!.AsArray().Select(x => x!.ToString()))
                    .Take(20).ToArray();
            foreach (var candidate in versions)
            {
                var builds = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper/versions/" +
                    Uri.EscapeDataString(candidate) + "/builds", ct);
                if (builds is not JsonArray array) continue;
                var stable = array.FirstOrDefault(b => (string?)b?["channel"] == "STABLE");
                var artifact = stable?["downloads"]?["server:default"];
                if (artifact is null) continue;
                var paperUrl = VerifiedUrl((string?)artifact["url"], "fill-data.papermc.io", "/v1/");
                var hash = (string?)artifact["checksums"]?["sha256"];
                return (paperUrl, candidate, null, hash);
            }
            throw new InvalidOperationException("No STABLE Paper build found for the selected Minecraft version.");
        }
        throw new InvalidOperationException("Unsupported Minecraft distribution.");
    }

    private async Task<string> LatestJavaReleaseAsync(CancellationToken ct)
    {
        var manifest = await GetJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);
        return (string?)manifest["latest"]?["release"] ??
            throw new InvalidDataException("Latest Minecraft Java release not published.");
    }

    private async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Official Minecraft metadata was empty.");
    }

    private async Task DownloadAsync(Uri url, string destination, string? sha1, string? sha256, CancellationToken ct)
    {
        // Explicit redirect handling: never follow an untrusted redirected host.
        var target = url;
        for (var redirects = 0; redirects < 5; redirects++)
        {
            using var response = await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                target = response.Headers.Location is { } location
                    ? new Uri(target, location)
                    : throw new InvalidDataException("Download redirect without Location.");
                if (!IsTrustedDownloadHost(target))
                    throw new InvalidDataException("Minecraft download redirected outside approved official hosts.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                await source.CopyToAsync(dest, ct);
            if (sha1 is { Length: > 0 })
            {
                await using var input = File.OpenRead(destination);
                var actual = Convert.ToHexString(await SHA1.HashDataAsync(input, ct));
                if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mojang SHA-1 checksum mismatch.");
            }
            if (sha256 is { Length: > 0 })
            {
                await using var input = File.OpenRead(destination);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Paper SHA-256 checksum mismatch.");
            }
            return;
        }
        throw new InvalidDataException("Too many Minecraft download redirects.");
    }

    public static Uri VerifiedUrl(string? url, string host, string prefix)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != host || uri.Port != 443 ||
            !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidDataException("Untrusted Minecraft download metadata URL.");
        return uri;
    }

    private static bool IsTrustedDownloadHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host is "piston-data.mojang.com" or "meta.fabricmc.net" or "www.minecraft.net" ||
         uri.Host == "fill-data.papermc.io" || uri.Host.EndsWith(".papermc.io", StringComparison.Ordinal));

    public static void ExtractSafeZip(string zip, string root)
    {
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var rel = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(rel) || rel.StartsWith('/') ||
                rel.Split('/').Any(part => part is "." or ".." or "") ||
                rel.Contains(':'))
                throw new InvalidDataException("Unsafe Bedrock archive path.");
            var output = Path.GetFullPath(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(folder + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Bedrock archive escapes installation root.");
            if (entry.FullName.EndsWith('/'))
                Directory.CreateDirectory(output);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                using var from = entry.Open();
                from.CopyTo(target);
            }
        }
    }

    public static void UpdateServerProperty(string file, string key, string value)
    {
        if (value.Contains('\n') || value.Contains('\r')) throw new ArgumentException("Invalid property.");
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        var index = lines.FindIndex(line => line.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (index < 0) lines.Add(key + "=" + value);
        else lines[index] = key + "=" + value;
        File.WriteAllLines(file, lines);
    }

    public static string? ResolveJavaExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var candidate = Path.Combine(home, "bin", name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static bool IsSupportedBedrockLinux()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var os = File.ReadAllLines("/etc/os-release");
            var ubuntu = os.Any(line => line.Trim().Equals("ID=ubuntu", StringComparison.OrdinalIgnoreCase));
            var version = os.FirstOrDefault(line => line.StartsWith("VERSION_ID=", StringComparison.Ordinal))?
                .Split('=', 2)[1].Trim('"');
            return ubuntu && Version.TryParse(version, out var parsed) && parsed.Major >= 22;
        }
        catch { return false; }
    }
}
