using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Runner;

public sealed class RunnerPipeServer
{
    private readonly RunnerIdentityInfo _identity;
    private readonly RunnerProcessHost _processHost;
    private readonly ILogger<RunnerPipeServer> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RunnerPipeServer(RunnerIdentityInfo identity, RunnerProcessHost processHost, ILogger<RunnerPipeServer> logger)
    {
        _identity = identity;
        _processHost = processHost;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                await HandleConnectionAsync(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runner pipe request failed");
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _identity.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            RunnerProtocolLimits.MaxRequestBytes + 4,
            RunnerProtocolLimits.MaxResponseBytes + 4,
            security,
            HandleInheritability.None);
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        RunnerResponse response;
        try
        {
            var requestBytes = await ReadFrameAsync(stream, RunnerProtocolLimits.MaxRequestBytes, ct);
            var request = JsonSerializer.Deserialize<RunnerRequest>(requestBytes, _json);
            if (request is null) response = new RunnerResponse(false, "Empty runner request.");
            else if (request.Validate() is { } validation) response = new RunnerResponse(false, validation);
            else response = await DispatchAsync(request, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            response = new RunnerResponse(false, "Malformed runner request.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(response, _json);
        if (payload.Length > RunnerProtocolLimits.MaxResponseBytes)
            payload = JsonSerializer.SerializeToUtf8Bytes(new RunnerResponse(false, "Runner response exceeded the protocol limit."), _json);
        await WriteFrameAsync(stream, payload, ct);
    }

    private Task<RunnerResponse> DispatchAsync(RunnerRequest request, CancellationToken ct) => request.Command switch
    {
        RunnerCommand.Ping => Task.FromResult(new RunnerResponse(
            true,
            LocalApplicationData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))),
        RunnerCommand.Start => _processHost.StartAsync(ct),
        RunnerCommand.Find => _processHost.FindAsync(ct),
        RunnerCommand.Stop => _processHost.StopAsync(request.Force, ct),
        RunnerCommand.ConsoleState => Task.FromResult(_processHost.ConsoleState(request.AfterSequence)),
        RunnerCommand.SendConsoleInput => _processHost.SendConsoleInputAsync(request.ConsoleInput, ct),
        RunnerCommand.KillExtras => _processHost.KillExtrasAsync(ct),
        _ => Task.FromResult(new RunnerResponse(false, "Unknown runner command."))
    };

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int limit, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > limit) throw new InvalidDataException("Runner frame length is outside the protocol limit.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }
}
