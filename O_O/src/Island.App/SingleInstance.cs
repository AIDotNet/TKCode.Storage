using System.IO.Pipes;
using System.Text;

namespace Island.App;

/// <summary>
/// One O_O per user (docs/design/06-roadmap.md M1). The first instance holds an exclusive lock on a file in the data
/// directory (an OS lock: released when the process ends, even when it crashes) and listens on a per-user local pipe;
/// a later launch finds the lock taken, tells the running instance to come forward (it opens its console) and exits.
/// <c>O_O_INSTANCE</c> names a separate instance, for development (two builds side by side).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string ActivateMessage = "activate";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream @lock) => _lock = @lock;

    private static string Name =>
        $"O_O-{(Environment.GetEnvironmentVariable("O_O_INSTANCE") is { Length: > 0 } n ? n : "main")}-{Environment.UserName}";

    /// <summary>Takes the instance lock, or returns null when another O_O holds it.</summary>
    public static SingleInstance? TryAcquire(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // FileShare.None is an exclusive OS lock on every platform (flock on Unix).
            var file = new FileStream(Path.Combine(directory, Name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Asks the running instance to come forward. False when it did not answer.</summary>
    public static bool ActivateRunning()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeout);
            var bytes = Encoding.UTF8.GetBytes(ActivateMessage + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="activate"/> (on a background thread) whenever another launch asks.</summary>
    public void Listen(Action activate)
    {
        var thread = new Thread(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    server.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    if (reader.ReadLine() == ActivateMessage)
                    {
                        activate();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(200); // a client went away mid-message, or the name is briefly busy: listen again
                }
            }
        })
        {
            IsBackground = true,
            Name = "O_O single instance",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lock.Dispose();
    }
}
