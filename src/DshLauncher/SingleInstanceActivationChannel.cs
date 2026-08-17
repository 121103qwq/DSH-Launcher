using System.IO;
using System.IO.Pipes;

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
    private const byte ActivationAccepted = 1;
    private readonly string _pipeName;
    private readonly Func<bool> _activate;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listenerTask;

    public SingleInstanceActivationChannel(string pipeName, Func<bool> activate)
    {
        _pipeName = pipeName;
        _activate = activate;
    }

    public void Start()
    {
        _listenerTask ??= Task.Run(() => ListenAsync(_cancellation.Token));
    }

    public static async Task<SingleInstanceActivationResult> RequestActivationAsync(
        string pipeName,
        TimeSpan timeout,
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
            await client.WriteAsync(new[] { ActivateRequest }, timeoutCancellation.Token).ConfigureAwait(false);
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
                var accepted = bytesRead == 1
                    && request[0] == ActivateRequest
                    && TryActivate();
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

    private bool TryActivate()
    {
        try
        {
            return _activate();
        }
        catch
        {
            return false;
        }
    }
}
