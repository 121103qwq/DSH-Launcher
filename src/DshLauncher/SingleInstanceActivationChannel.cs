using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DshLauncher;

internal enum SingleInstanceActivationResult
{
    Unavailable,
    Rejected,
    Accepted
}

internal sealed class SingleInstanceActivationChannel : IDisposable
{
    private const byte ActivateRequest = 1;
    private const byte CommandRequest = 2;
    private const byte ActivationAccepted = 1;
    private const int MaximumPayloadBytes = 64 * 1024;
    private readonly string _pipeName;
    private readonly Func<string?, bool> _activate;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listenerTask;

    public SingleInstanceActivationChannel(string pipeName, Func<bool> activate)
        : this(pipeName, _ => activate())
    {
    }

    public SingleInstanceActivationChannel(string pipeName, Func<string?, bool> activate)
    {
        _pipeName = pipeName;
        _activate = activate;
    }

    public void Start()
    {
        _listenerTask ??= Task.Run(() => ListenAsync(_cancellation.Token));
    }

    public static Task<SingleInstanceActivationResult> RequestActivationAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RequestActivationAsync(pipeName, timeout, null, cancellationToken);

    public static async Task<SingleInstanceActivationResult> RequestActivationAsync(
        string pipeName,
        TimeSpan timeout,
        string? payload,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(payload))
            {
                await client.WriteAsync(new[] { ActivateRequest }, timeoutCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                var payloadBytes = Encoding.UTF8.GetBytes(payload);
                if (payloadBytes.Length > MaximumPayloadBytes)
                {
                    return SingleInstanceActivationResult.Rejected;
                }

                await client.WriteAsync(new[] { CommandRequest }, timeoutCancellation.Token).ConfigureAwait(false);
                await client.WriteAsync(BitConverter.GetBytes(payloadBytes.Length), timeoutCancellation.Token).ConfigureAwait(false);
                await client.WriteAsync(payloadBytes, timeoutCancellation.Token).ConfigureAwait(false);
            }
            await client.FlushAsync(timeoutCancellation.Token).ConfigureAwait(false);

            var response = new byte[1];
            var bytesRead = await client.ReadAsync(response, timeoutCancellation.Token).ConfigureAwait(false);
            return bytesRead != 1
                ? SingleInstanceActivationResult.Unavailable
                : response[0] == ActivationAccepted
                    ? SingleInstanceActivationResult.Accepted
                    : SingleInstanceActivationResult.Rejected;
        }
        catch (Exception ex) when (ex is IOException
                                   or OperationCanceledException
                                   or UnauthorizedAccessException)
        {
            return SingleInstanceActivationResult.Unavailable;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);

                var request = new byte[1];
                var bytesRead = await server.ReadAsync(request, cancellationToken);
                string? payload = null;
                var validRequest = bytesRead == 1 && request[0] == ActivateRequest;
                if (bytesRead == 1 && request[0] == CommandRequest)
                {
                    var lengthBytes = new byte[sizeof(int)];
                    validRequest = await ReadExactlyAsync(server, lengthBytes, cancellationToken)
                        && BitConverter.ToInt32(lengthBytes) is > 0 and <= MaximumPayloadBytes;
                    if (validRequest)
                    {
                        var payloadBytes = new byte[BitConverter.ToInt32(lengthBytes)];
                        validRequest = await ReadExactlyAsync(server, payloadBytes, cancellationToken);
                        if (validRequest)
                        {
                            payload = Encoding.UTF8.GetString(payloadBytes);
                        }
                    }
                }

                var accepted = validRequest && TryActivate(payload);
                await server.WriteAsync(
                    new[] { accepted ? ActivationAccepted : (byte)0 },
                    cancellationToken);
                await server.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client may exit between connecting and reading the response.
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private bool TryActivate(string? payload)
    {
        try
        {
            return _activate(payload);
        }
        catch
        {
            return false;
        }
    }
}
