from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one occurrence, found {count}: {old[:80]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs",
    "public sealed class WindowsRunnerServiceManager\n",
    "public sealed class WindowsRunnerServiceManager : IWindowsRunnerServiceManager\n",
)
replace_once(
    "src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs",
    "    internal Task<RunnerResponse> PollConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>\n",
    "    public Task<RunnerResponse> PollConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>\n",
)

replace_once(
    "src/GameServerControl.Agent/Hyperv/LocalProcessService.cs",
    "    private readonly WindowsRunnerServiceManager _windowsRunner;\n",
    "    private readonly IWindowsRunnerServiceManager _windowsRunner;\n",
)
replace_once(
    "src/GameServerControl.Agent/Hyperv/LocalProcessService.cs",
    "        WindowsRunnerServiceManager windowsRunner)\n",
    "        IWindowsRunnerServiceManager windowsRunner)\n",
)

replace_once(
    "src/GameServerControl.Agent/Program.cs",
    "builder.Services.AddSingleton<WindowsRunnerServiceManager>();\nbuilder.Services.AddSingleton<LocalProcessService>();",
    "builder.Services.AddSingleton<WindowsRunnerServiceManager>();\nbuilder.Services.AddSingleton<IWindowsRunnerServiceManager>(sp => sp.GetRequiredService<WindowsRunnerServiceManager>());\nbuilder.Services.AddSingleton<LocalProcessService>();",
)
replace_once(
    "src/GameServerControl.Agent/Program.cs",
    "{\n    using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());\n    var startupLogger = startupLoggerFactory.CreateLogger(\"FirstRunTokenGenerator\");\n    if (FirstRunTokenGenerator.EnsureToken(builder.Configuration, AppContext.BaseDirectory, startupLogger, out var resolvedToken))\n        builder.Configuration[\"Agent:ApiToken\"] = resolvedToken;\n}\n\nbuilder.Services.AddSingleton<AuditLogger>();",
    "if (!builder.Environment.IsEnvironment(\"Testing\"))\n{\n    using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());\n    var startupLogger = startupLoggerFactory.CreateLogger(\"FirstRunTokenGenerator\");\n    if (FirstRunTokenGenerator.EnsureToken(builder.Configuration, AppContext.BaseDirectory, startupLogger, out var resolvedToken))\n        builder.Configuration[\"Agent:ApiToken\"] = resolvedToken;\n}\n\nbuilder.Services.AddSingleton<AuditLogger>();",
)

program = Path("src/GameServerControl.Agent/Program.cs")
text = program.read_text(encoding="utf-8")
count = text.count("WindowsRunnerServiceManager runners")
if count < 2:
    raise SystemExit(f"Program.cs: expected Runner endpoint injections, found {count}")
text = text.replace("WindowsRunnerServiceManager runners", "IWindowsRunnerServiceManager runners")
program.write_text(text, encoding="utf-8")

replace_once(
    "src/GameServerControl.Agent/Program.cs",
    "_ = Task.Run(async () =>\n{",
    "if (!builder.Environment.IsEnvironment(\"Testing\"))\n{\n_ = Task.Run(async () =>\n{",
)
replace_once(
    "src/GameServerControl.Agent/Program.cs",
    "});\n\napp.Run();\n\npublic record RconCommandRequest",
    "});\n}\n\napp.Run();\n\npublic partial class Program { }\n\npublic record RconCommandRequest",
)
