namespace SevenWondersDuel.Agents;

/// <summary>
/// Cancels <paramref name="stop"/> as soon as a file exists at a path (checked every <c>every</c>, default half a
/// second). This is how <c>7wd-train pause</c> stops a running Self-Play session or Match (spec A7, "Control"): the
/// training loop passes its pause-request file as <c>--stop-file</c>, works in progress are dropped and finished games
/// or seed pairs are kept, exactly as with Ctrl+C. Works the same on Windows and Linux (no signals needed).
/// </summary>
public sealed class StopFile : IDisposable
{
    readonly Timer _timer;
    readonly string _path;
    readonly CancellationTokenSource _stop;
    readonly Action? _onStop;
    int _fired;

    public StopFile(string path, CancellationTokenSource stop, TimeSpan? every = null, Action? onStop = null)
    {
        _path = path;
        _stop = stop;
        _onStop = onStop;
        _timer = new Timer(_ => Check(), null, TimeSpan.Zero, every ?? TimeSpan.FromMilliseconds(500));
    }

    void Check()
    {
        try
        {
            if (Volatile.Read(ref _fired) != 0 || !File.Exists(_path)) return;
            if (Interlocked.Exchange(ref _fired, 1) != 0) return;
            _onStop?.Invoke();
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}
