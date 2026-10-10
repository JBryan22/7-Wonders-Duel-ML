namespace SevenWondersDuel.Agents;

/// <summary>
/// Cancels <paramref name="stop"/> as soon as a file exists at a path (checked every <c>every</c>, default half a
/// second). This is how <c>7wd-train pause</c> stops a running Self-Play session or Match (spec A7, "Control"): the
/// training loop passes its pause-request file as <c>--stop-file</c>, work in progress is dropped and finished games
/// or seed pairs are kept, exactly as with Ctrl+C. Works the same on Windows and Linux (no signals needed). The check
/// runs on its own background thread, so a busy thread pool can't delay it.
/// </summary>
public sealed class StopFile : IDisposable
{
    readonly ManualResetEventSlim _disposed = new();
    readonly Thread _thread;

    public StopFile(string path, CancellationTokenSource stop, TimeSpan? every = null, Action? onStop = null)
    {
        var interval = every ?? TimeSpan.FromMilliseconds(500);
        _thread = new Thread(() =>
        {
            do
            {
                if (stop.IsCancellationRequested) return;
                if (File.Exists(path))
                {
                    onStop?.Invoke();
                    try
                    {
                        stop.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    return;
                }
            } while (!_disposed.Wait(interval));
        }) { IsBackground = true, Name = "7wd stop file" };
        _thread.Start();
    }

    public void Dispose()
    {
        _disposed.Set();
        _thread.Join();
        _disposed.Dispose();
    }
}
