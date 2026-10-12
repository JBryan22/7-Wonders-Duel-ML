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
    ITableSource? _source;
    DateTime? _loadedAt;
    CancellationTokenSource? _loop;
    TtsConnection _state = new(false, false, null, false, null, null);

    public TtsConnection Connection
    {
        get { lock (_lock) return _state; }
    }

    public bool Following => Connection.Following;

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
        while (!stop.IsCancellationRequested)
        {
            var clock = Stopwatch.StartNew();
            await PollOnceAsync(stop);
            var wait = _interval - clock.Elapsed;
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Reads the table once; a settled snapshot goes to the reconciler. Never throws for TTS trouble.</summary>
    public async Task PollOnceAsync(CancellationToken cancel = default)
    {
        await _poll.WaitAsync(cancel);
        try
        {
            ITableSource source;
            lock (_lock)
            {
                if (!_state.Following) return;
                try
                {
                    source = _source ??= open();
                }
                catch (TtsException e)
                {
                    _state = _state with { Connected = false, Problem = e.Message };
                    return;
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
                return;
            }
            catch (FormatException e)
            {
                Update(s => s with { Connected = true, Problem = $"TTS sent something unexpected: {e.Message}", Settled = false });
                return;
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
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
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
