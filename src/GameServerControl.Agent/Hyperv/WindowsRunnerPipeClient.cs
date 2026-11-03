using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Hyperv;

internal readonly record struct RunnerPipeResult(RunnerResponse Response, bool TransportFailure);

public sealed class WindowsRunnerPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(12);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<RunnerResponse> SendAsync(
        RunnerIdentityInfo identity,
        RunnerRequest request,
        CancellationToken ct = default)
        => (await SendDetailedAsync(identity, request, ct)).Response;

    internal async Task<RunnerPipeResult> SendDetailedAsync(
        RunnerIdentityInfo identity,
        RunnerRequest request,
        CancellationToken ct = default)
    {
        var validation = request.Validate();
        if (validation is not null)
            return new RunnerPipeResult(new RunnerResponse(false, validation), TransportFailure: false);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, _json);
        if (payload.Length > RunnerProtocolLimits.MaxRequestBytes)
            return new RunnerPipeResult(
                new RunnerResponse(false, "Runner request exceeded the protocol limit."),
                TransportFailure: false);

        try
        {
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            operationCts.CancelAfter(OperationTimeout);

            await using var pipe = new NamedPipeClientStream(
                ".",
                identity.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(operationCts.Token);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token);

            await WriteFrameAsync(pipe, payload, operationCts.Token);
            var responseBytes = await ReadFrameAsync(pipe, RunnerProtocolLimits.MaxResponseBytes, operationCts.Token);
            var response = JsonSerializer.Deserialize<RunnerResponse>(responseBytes, _json)
                           ?? new RunnerResponse(false, "Runner returned an empty response.");
            return new RunnerPipeResult(response, TransportFailure: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RunnerPipeResult(
                new RunnerResponse(false, "Runner pipe connection timed out."),
                TransportFailure: true);
        }
        catch (Exception ex)
        {
            return new RunnerPipeResult(
                new RunnerResponse(false, "Runner IPC failed: " + ex.Message),
                TransportFailure: true);
        }
    }

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
