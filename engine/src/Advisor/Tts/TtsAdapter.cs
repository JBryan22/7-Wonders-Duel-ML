using System.Diagnostics;

namespace SevenWondersDuel.Advisor.Tts;

/// <summary>
/// Settled-table detection (spec A9, ADR 0004): a snapshot is reconciled only when nobody holds an object, nothing
/// moves, and it is identical to the snapshot before it.
/// </summary>
public sealed class SettledTable
{
    string? _last;

    /// <summary>Records a snapshot; returns it when the table is settled, otherwise null.</summary>
    public TableSnapshot? Offer(TableSnapshot snapshot)
    {
        var settled = snapshot.StillNow && snapshot.Text == _last;
        _last = snapshot.Text;
        return settled ? snapshot : null;
    }

    public void Reset() => _last = null;
}

/// <summary>Where Table Snapshots come from: TTS through <see cref="TtsLink"/>, or a fake table in tests.</summary>
public interface ITableSource : IDisposable
{
    /// <summary>One snapshot's JSON text. Throws <see cref="TtsException"/> when TTS can't be reached or didn't answer.</summary>
    Task<string> ReadAsync(CancellationToken cancel);

    /// <summary>When TTS last said it loaded a game (a new or reloaded table), or null; see <see cref="TtsLink.GameLoadedAt"/>.</summary>
    DateTime? LoadedAt => null;
}

/// <summary>The live table: the read-only snapshot script through the External Editor API.</summary>
public sealed class TtsLinkSource(TtsLink link, TimeSpan timeout) : ITableSource
{
    /// <summary>Opens the link on TTS's default ports. Throws <see cref="TtsException"/> if the listen port is busy.</summary>
    public static TtsLinkSource Open() => new(new TtsLink(), TimeSpan.FromSeconds(5));

    public Task<string> ReadAsync(CancellationToken cancel) => link.RequestSnapshotTextAsync(timeout, cancel);
    public DateTime? LoadedAt => link.GameLoadedAt;
    public void Dispose() => link.Dispose();
}

/// <summary>
/// A table recorded in a file (what <c>tts-snapshot --save</c> writes), read again at every poll: for checking the
/// Advisor against a saved live table without TTS (<c>7wd-advisor --tts-file FILE</c>).
/// </summary>
public sealed class FileTableSource(string path) : ITableSource
{
    public async Task<string> ReadAsync(CancellationToken cancel)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancel);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TtsException($"Can't read the table file {path}: {e.Message}", e);
        }
    }

    public void Dispose() { }
}

/// <summary>How often the Advisor reads the table: about every second, every 10 seconds, or only on a click.</summary>
public enum TtsPace { Live, Every10, OnClick }

/// <summary>How the connection to the table is doing, for the Advisor Window.</summary>
public sealed record TtsConnection(bool Following, bool Connected, string? Problem, bool Settled, long? RoundTripMs, DateTime? LastRead);

/// <summary>
/// The Tabletop Simulator Adapter's polling half (spec A9): while following, it reads a Table Snapshot about once a
/// second, waits for the table to settle, and hands each settled snapshot to <paramref name="settled"/> (the Advisor
/// server, which reconciles it with the Move Log under its lock). Only the read-only snapshot script is ever sent.
/// Polling runs on a background task; <see cref="PollOnceAsync"/> is one step of it (tests call it directly). When TTS
/// says it loaded a game, the settled detection starts over and <paramref name="loaded"/> is told (ticket 34: the next
/// settled table may be a new game, or a save of this one further on).
/// </summary>
public sealed class TtsAdapter(Func<ITableSource> open, Action<TableSnapshot> settled, TimeSpan? interval = null, Action? loaded = null)
    : IDisposable
{
    readonly Lock _lock = new();
    readonly SettledTable _settle = new();
    readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(1);
    readonly SemaphoreSlim _poll = new(1, 1);
    // Released by ReadNow: wakes the loop from its wait (a pace's pause, or idling until a click).
    readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    // Reads left in the current click's burst (TtsPace.OnClick): read until the table settles, at most this many.
    int _burst;
    ITableSource? _source;
    DateTime? _loadedAt;
    CancellationTokenSource? _loop;
    TtsConnection _state = new(false, false, null, false, null, null);

    public TtsConnection Connection
    {
        get { lock (_lock) return _state; }
    }

    public bool Following => Connection.Following;

    /// <summary>
    /// How often the table is read (each reading stalls TTS for a moment, as its External Editor API answers on its
    /// main thread). While the table is changing it is read every interval (~1 s) until it settles, whatever the pace.
    /// </summary>
    public TtsPace Pace { get; set; } = TtsPace.Live;

    /// <summary>The wait between readings of a settled table at <see cref="Pace"/> <see cref="TtsPace.Every10"/>.</summary>
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(10);

    /// <summary>Reads a click brings at most: enough for the table to settle (the same twice in a row).</summary>
    public const int ClickReads = 6;

    /// <summary>Reads the table now (the "Read the table now" button): until it settles, then back to the pace.</summary>
    public void ReadNow()
    {
        Interlocked.Exchange(ref _burst, ClickReads);
        _wake.Release();
    }

    /// <summary>The last snapshot read, settled or not (for the hold after a manual Undo).</summary>
    public TableSnapshot? LastSnapshot { get; private set; }

    /// <summary>Starts following the table (polling in the background unless <paramref name="background"/> is false).</summary>
    public void Start(bool background = true)
    {
        lock (_lock)
        {
            if (_state.Following) return;
            _state = new TtsConnection(true, false, null, false, null, null);
            _settle.Reset();
            if (!background) return;
            _loop = new CancellationTokenSource();
            var token = _loop.Token;
            _ = Task.Run(() => Loop(token));
        }
    }

    /// <summary>Stops following and frees TTS's listen port.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _loop?.Cancel();
            _loop = null;
            _source?.Dispose();
            _source = null;
            _state = new TtsConnection(false, false, null, false, null, null);
        }
    }

    async Task Loop(CancellationToken stop)
    {
        // A click's burst starts with the first reading after following starts, so the box isn't empty until a click.
        Interlocked.Exchange(ref _burst, ClickReads);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // Waiting for a click: nothing is read, TTS isn't touched.
                if (Pace == TtsPace.OnClick && Volatile.Read(ref _burst) <= 0) await _wake.WaitAsync(stop);
                while (_wake.CurrentCount > 0) _wake.Wait(0);
                var clock = Stopwatch.StartNew();
                var settledNow = await PollOnceAsync(stop);
                // A click's burst ends when the table settles (or after ClickReads readings).
                if (settledNow) Interlocked.Exchange(ref _burst, 0);
                else Interlocked.Decrement(ref _burst);
                var pause = Pace switch
                {
                    TtsPace.Every10 when settledNow => SlowInterval,
                    TtsPace.OnClick when Volatile.Read(ref _burst) <= 0 => TimeSpan.Zero,
                    _ => _interval,
                };
                var wait = pause - clock.Elapsed;
                // ReadNow (or a pace change) cuts the wait short.
                if (wait > TimeSpan.Zero) await _wake.WaitAsync(wait, stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reads the table once; a settled snapshot goes to the reconciler. Returns whether the table was settled. Never
    /// throws for TTS trouble.
    /// </summary>
    public async Task<bool> PollOnceAsync(CancellationToken cancel = default)
    {
        await _poll.WaitAsync(cancel);
        try
        {
            ITableSource source;
            lock (_lock)
            {
                if (!_state.Following) return false;
                try
                {
                    source = _source ??= open();
                }
                catch (TtsException e)
                {
                    _state = _state with { Connected = false, Problem = e.Message };
                    return false;
                }
            }
            var clock = Stopwatch.StartNew();
            TableSnapshot snapshot;
            try
            {
                snapshot = TableSnapshot.Parse(await source.ReadAsync(cancel));
            }
            catch (TtsException e)
            {
                Update(s => s with { Connected = false, Problem = e.Message, Settled = false });
                return false;
            }
            catch (FormatException e)
            {
                Update(s => s with { Connected = true, Problem = $"TTS sent something unexpected: {e.Message}", Settled = false });
                return false;
            }
            if (source.LoadedAt is { } at && at != _loadedAt)
            {
                _loadedAt = at;
                _settle.Reset();
                loaded?.Invoke();
            }
            LastSnapshot = snapshot;
            var ready = _settle.Offer(snapshot);
            Update(s => s with
            {
                Connected = true,
                Problem = null,
                Settled = ready is not null,
                RoundTripMs = clock.ElapsedMilliseconds,
                LastRead = DateTime.UtcNow,
            });
            if (ready is not null && Following) settled(ready);
            return ready is not null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _poll.Release();
        }
    }

    void Update(Func<TtsConnection, TtsConnection> change)
    {
        lock (_lock)
            if (_state.Following) _state = change(_state);
    }

    public void Dispose() => Stop();
}
