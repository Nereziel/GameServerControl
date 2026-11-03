from pathlib import Path

manager_path = Path('src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs')
manager = manager_path.read_text()

old_fields = '''    private readonly ConcurrentDictionary<string, string> _provisionedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _provisionGates = new(StringComparer.OrdinalIgnoreCase);'''
new_fields = '''    private readonly ConcurrentDictionary<string, string> _provisionedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _provisionGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _deprovisioning = new(StringComparer.OrdinalIgnoreCase);'''
if old_fields not in manager:
    raise SystemExit('manager fields fragment not found')
manager = manager.replace(old_fields, new_fields, 1)

old_deprovision_start = '''    public async Task<(bool Ok, string Error)> DeprovisionAsync(ServerDef def, CancellationToken ct = default)
    {
        if (!Supports(def)) return (true, "");

        var identity = RunnerIdentity.ForServer(def.Id);
        var serviceAccount = "NT SERVICE\\\\" + identity.ServiceName;
        var provisionGate = _provisionGates.GetOrAdd(def.Id, _ => new SemaphoreSlim(1, 1));
        await provisionGate.WaitAsync(ct);
        try
        {'''
new_deprovision_start = '''    public async Task<(bool Ok, string Error)> DeprovisionAsync(ServerDef def, CancellationToken ct = default)
    {
        if (!Supports(def)) return (true, "");
        if (!_deprovisioning.TryAdd(def.Id, 0))
            return (false, "Runner deprovisioning is already in progress for this server.");

        var identity = RunnerIdentity.ForServer(def.Id);
        var serviceAccount = "NT SERVICE\\\\" + identity.ServiceName;
        var provisionGate = _provisionGates.GetOrAdd(def.Id, _ => new SemaphoreSlim(1, 1));
        var gateAcquired = false;
        var completed = false;
        try
        {
            await provisionGate.WaitAsync(ct);
            gateAcquired = true;'''
if old_deprovision_start not in manager:
    raise SystemExit('deprovision start fragment not found')
manager = manager.replace(old_deprovision_start, new_deprovision_start, 1)

old_deprovision_end = '''            _provisionedFingerprints.TryRemove(def.Id, out _);
            return (true, "");
        }
        finally
        {
            provisionGate.Release();
        }
    }

    private static IEnumerable<string> DeprovisionGrantPaths(ServerDef def)'''
new_deprovision_end = '''            _provisionedFingerprints.TryRemove(def.Id, out _);
            completed = true;
            return (true, "");
        }
        finally
        {
            if (gateAcquired) provisionGate.Release();
            if (!completed) _deprovisioning.TryRemove(def.Id, out _);
        }
    }

    public void CompleteDeprovision(string serverId)
        => _deprovisioning.TryRemove(serverId, out _);

    private static IEnumerable<string> DeprovisionGrantPaths(ServerDef def)'''
if old_deprovision_end not in manager:
    raise SystemExit('deprovision end fragment not found')
manager = manager.replace(old_deprovision_end, new_deprovision_end, 1)

old_ensure = '''    private async Task<ProvisionResult> EnsureProvisionedAsync(ServerDef def, CancellationToken ct)
    {
        if (!Supports(def)) return ProvisionResult.Fail("Windows Runner is not applicable to this server.");

        var identity = RunnerIdentity.ForServer(def.Id);'''
new_ensure = '''    private async Task<ProvisionResult> EnsureProvisionedAsync(ServerDef def, CancellationToken ct)
    {
        if (!Supports(def)) return ProvisionResult.Fail("Windows Runner is not applicable to this server.");
        if (_deprovisioning.ContainsKey(def.Id))
            return ProvisionResult.Fail("Runner deprovisioning is in progress for this server.");

        var identity = RunnerIdentity.ForServer(def.Id);'''
if old_ensure not in manager:
    raise SystemExit('ensure start fragment not found')
manager = manager.replace(old_ensure, new_ensure, 1)

old_gate = '''        await provisionGate.WaitAsync(ct);
        try
        {
            if (IsProvisionCached(def.Id, fingerprint, manifestPath))'''
new_gate = '''        await provisionGate.WaitAsync(ct);
        try
        {
            if (_deprovisioning.ContainsKey(def.Id))
                return ProvisionResult.Fail("Runner deprovisioning is in progress for this server.");
            if (IsProvisionCached(def.Id, fingerprint, manifestPath))'''
if old_gate not in manager:
    raise SystemExit('ensure gate fragment not found')
manager = manager.replace(old_gate, new_gate, 1)
manager_path.write_text(manager)

program_path = Path('src/GameServerControl.Agent/Program.cs')
program = program_path.read_text()
old_delete_tail = '''    if (!store.Delete(id)) return Results.NotFound();
    await relay.RemoveServerAsync(id, ct);
    return Results.NoContent();'''
new_delete_tail = '''    if (!store.Delete(id))
    {
        runners.CompleteDeprovision(id);
        return Results.NotFound();
    }
    runners.CompleteDeprovision(id);
    await relay.RemoveServerAsync(id, ct);
    return Results.NoContent();'''
if old_delete_tail not in program:
    raise SystemExit('delete endpoint tail not found')
program_path.write_text(program.replace(old_delete_tail, new_delete_tail, 1))
