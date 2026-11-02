using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Hyperv;

public sealed class WindowsRunnerPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<RunnerResponse> SendAsync(
        RunnerIdentityInfo identity,
        RunnerRequest request,
        CancellationToken ct = default)
    {
        var validation = request.Validate();
        if (validation is not null) return new RunnerResponse(false, validation);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, _json);
        if (payload.Length > RunnerProtocolLimits.MaxRequestBytes)
            return new RunnerResponse(false, "Runner request exceeded the protocol limit.");

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                identity.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token);

            await WriteFrameAsync(pipe, payload, ct);
            var responseBytes = await ReadFrameAsync(pipe, RunnerProtocolLimits.MaxResponseBytes, ct);
            return JsonSerializer.Deserialize<RunnerResponse>(responseBytes, _json)
                   ?? new RunnerResponse(false, "Runner returned an empty response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RunnerResponse(false, "Runner pipe connection timed out.");
        }
        catch (Exception ex)
        {
            return new RunnerResponse(false, "Runner IPC failed: " + ex.Message);
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
