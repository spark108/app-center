using System.IO;
using System.IO.Pipes;
using Spark108.Core;

namespace Spark108.Desktop;

internal sealed class InstanceCommands : IDisposable
{
    private readonly CancellationTokenSource stop = new();

    public InstanceCommands(MainWindow window) => _ = Listen(window, stop.Token);

    public static bool Forward(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", LocalPackages.CommandPipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            using var output = new StreamWriter(pipe);
            output.WriteLine(command);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    private static async Task Listen(MainWindow window, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(LocalPackages.CommandPipe, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var buffer = new byte[80];
                var length = 0;
                while (length < buffer.Length)
                {
                    if (await pipe.ReadAsync(buffer.AsMemory(length, 1), deadline.Token).ConfigureAwait(false) == 0 ||
                        buffer[length] == '\n')
                        break;
                    length++;
                }
                var value = System.Text.Encoding.ASCII.GetString(buffer, 0, length).TrimEnd('\r');
                if (value == "show")
                    _ = window.Dispatcher.InvokeAsync(window.ShowFromTray);
                else if (value.StartsWith("uninstall ", StringComparison.Ordinal) && PackageRules.ValidId(value[10..]))
                    _ = window.Dispatcher.InvokeAsync(() => window.UninstallFromWindows(value[10..]));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { return; }
            catch (IOException)
            {
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}
