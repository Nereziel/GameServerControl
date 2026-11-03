from pathlib import Path

path = Path("src/GameServerControl.Agent/Program.cs")
text = path.read_text(encoding="utf-8")

old_first_run = '''if (!builder.Environment.IsEnvironment("Testing"))
{
    using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var startupLogger = startupLoggerFactory.CreateLogger("FirstRunTokenGenerator");
    if (FirstRunTokenGenerator.EnsureToken(builder.Configuration, AppContext.BaseDirectory, startupLogger, out var resolvedToken))
        builder.Configuration["Agent:ApiToken"] = resolvedToken;
}
'''
new_first_run = '''{
    using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var startupLogger = startupLoggerFactory.CreateLogger("FirstRunTokenGenerator");
    if (FirstRunTokenGenerator.EnsureToken(builder.Configuration, AppContext.BaseDirectory, startupLogger, out var resolvedToken))
        builder.Configuration["Agent:ApiToken"] = resolvedToken;
}
'''

old_loop_start = '''if (!builder.Environment.IsEnvironment("Testing"))
{
_ = Task.Run(async () =>
{'''
new_loop_start = '''_ = Task.Run(async () =>
{'''
old_loop_end = '''});
}

app.Run();'''
new_loop_end = '''});

app.Run();'''

for old, new, label in [
    (old_first_run, new_first_run, "first-run guard"),
    (old_loop_start, new_loop_start, "background-loop guard start"),
    (old_loop_end, new_loop_end, "background-loop guard end"),
]:
    if text.count(old) != 1:
        raise SystemExit(f"Expected exactly one {label}")
    text = text.replace(old, new, 1)

path.write_text(text, encoding="utf-8")
