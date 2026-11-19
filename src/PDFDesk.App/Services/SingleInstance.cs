using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Windows.AppLifecycle;

namespace PDFDesk.Services;

/// <summary>Shares the instance across installed and portable copies in one Windows session.</summary>
internal sealed class SingleInstance : IDisposable
{
    private const int HandoffTimeoutMs = 4000;
    private const int MaxRequestBytes = 1024 * 1024;
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private readonly string _pipeName;
    private readonly AppInstance _owner;
    private readonly CancellationTokenSource _shutdown = new();

    public SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _owner = AppInstance.FindOrRegisterForKey($"DavidLabWorks.PDFDesk.{identity.User!.Value}");
        _pipeName = $"DavidLabWorks.PDFDesk.{identity.User.Value}.{_owner.ProcessId}";
    }

    public bool IsOwner => _owner.IsCurrent;

    public bool TryForward(LaunchRequest request)
    {
        try
        {
            return ForwardAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            ErrorLog.Default.Write(nameof(SingleInstance), ex);
            return false;
        }
    }

    private async Task<bool> ForwardAsync(LaunchRequest request)
    {
        using var deadline = new CancellationTokenSource(HandoffTimeoutMs);
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, Options);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        if (payload.Length > MaxRequestBytes)
        {
            return false;
        }

        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var header = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        AllowSetForegroundWindow(BitConverter.ToUInt32(header));
        await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
        var ack = new byte[1];
        await pipe.ReadExactlyAsync(ack, deadline.Token).ConfigureAwait(false);
        return ack[0] == 1;
    }

    public async Task ListenAsync(Func<LaunchRequest, bool> enqueue)
    {
        var shutdown = _shutdown.Token;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Options);
                await pipe.WaitForConnectionAsync(shutdown).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                deadline.CancelAfter(HandoffTimeoutMs);
                try
                {
                    await AcceptAsync(pipe, enqueue, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
                {
                    // A disconnected launcher must not take down the listener.
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(SingleInstance), ex);
        }
    }

    private static async Task AcceptAsync(Stream pipe, Func<LaunchRequest, bool> enqueue, CancellationToken token)
    {
        await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), token).ConfigureAwait(false);
        var header = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header);
        if (length is <= 0 or > MaxRequestBytes)
        {
            return;
        }

        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<LaunchRequest>(payload);
        byte[] ack = [request?.Files is not null && enqueue(request) ? (byte)1 : (byte)0];
        await pipe.WriteAsync(ack, token).ConfigureAwait(false);
        // Wait for the client to read the acknowledgement and close its end.
        await pipe.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
