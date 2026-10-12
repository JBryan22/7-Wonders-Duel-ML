using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SevenWondersDuel.Advisor.Tts;

namespace SevenWondersDuel.Advisor;

/// <summary>
/// Settings of one Advisor server. <paramref name="Agent"/> is the advice search (<c>mcts</c> or
/// <c>neural:&lt;onnx&gt;</c>); <paramref name="Simulations"/> fixes the advice budget (null: 800, or this computer's
/// reduced default). <paramref name="TableSource"/> opens the Tabletop Simulator table (default: the live table
/// through the External Editor API; tests pass a fake). <paramref name="TtsPolling"/> false leaves polling to the
/// caller (<see cref="AdvisorServer.Tts"/>.PollOnceAsync), for tests.
/// </summary>
public sealed record AdvisorOptions(string DataFolder, int Port = AdvisorServer.DefaultPort, string? WebFolder = null,
    string Agent = AdviceSettings.DefaultAgent, int? Simulations = null, Func<ITableSource>? TableSource = null,
    bool TtsPolling = true);

/// <summary>
/// The Advisor Window's local web server (spec A8): serves the page from web/ and a small JSON API. It listens on
/// localhost only. Every change is saved to disk before it is answered, so closing the window or the server loses
/// nothing.
/// <code>
/// GET  /api/state              the game view (or the new-game screen when there is no game) and the advice box
/// POST /api/new     {messe, liberty, rule2015, seat}   start a new game (the previous one is archived)
/// POST /api/entry   {line, then?}  enter one Move Log line ("P1 build baths", "reveal 3.1 baths", ...); with then, two
///                              lines as one change (a Statue keep/give pair), both or neither
/// POST /api/undo               remove the last Move and the Reveals after it
/// POST /api/rewind  {count}    keep only the first count log lines
/// POST /api/abandon            archive the game and go back to the new-game screen
/// GET  /api/advice             the advice box only (the page polls it while a search runs)
/// POST /api/advice  {longer}   ask for advice on the current position (longer: Think longer)
/// POST /api/advice/auto {on}   advise by itself whenever the owner is to decide
/// GET  /api/tts                the Tabletop Simulator box and the change counter (the page polls it while following)
/// POST /api/tts     {follow?, color?}  follow the TTS table or stop; the owner's seat colour (White / Red)
/// POST /api/tts/choose {index} enter one of the chains offered when the table can be read several ways
/// </code>
/// Refused entries answer 400 with {error}. The Tabletop Simulator Adapter feeds the same session: a settled Table
/// Snapshot is reconciled under the same lock, a single explaining chain is entered like a click, and every change
/// tells the <see cref="AdviceService"/> that the position changed.
/// </summary>
public sealed class AdvisorServer : IDisposable
{
    public const int DefaultPort = 7700;

    readonly AdvisorStore _store;
    readonly AdviceService _advice;
    readonly TtsAdapter _tts;
    readonly bool _ttsPolling;
    readonly Lock _lock = new();
    AdvisorSession? _session;
    string? _notice;
    TtsPrefs _ttsPrefs;
    /// <summary>Bumped on every change of the game or the TTS box, so the page knows when to fetch the state again.</summary>
    long _changes;

    // Reconciliation (under _lock): the last settled snapshot, what it said and for which log, and the hold.
    TableSnapshot? _settled;
    Reconciliation? _sync;
    string? _syncText, _syncLog, _syncProblem;
    /// <summary>The table's texts (last read, last settled) when the owner last undid or rewound by hand: no chain is appended while the table shows one of them.</summary>
    readonly HashSet<string> _hold = new(StringComparer.Ordinal);
    bool _held;
    string? _lastAppended;

    public AdvisorServer(AdvisorStore store, AdviceSettings? advice = null, bool fixedBudget = false,
        Func<ITableSource>? tableSource = null, bool ttsPolling = true)
    {
        _store = store;
        _advice = new AdviceService(advice ?? new AdviceSettings(), store.LoadAdvicePrefs(), fixedBudget, store.AdviceLogPath,
            store.SaveAdvicePrefs);
        _tts = new TtsAdapter(tableSource ?? TtsLinkSource.Open, OnSettled);
        _ttsPolling = ttsPolling;
        _ttsPrefs = store.LoadTtsPrefs();
        try
        {
            _session = store.LoadCurrent();
            if (_session is not null) _notice = $"Resumed the saved game ({_session.Log.Count} log lines).";
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or IOException)
        {
            var moved = store.ArchiveCurrent();
            _notice = $"The saved game could not be read and was moved to {moved}: {e.Message}";
        }
        _advice.Sync(_session);
        if (_ttsPrefs.Follow) _tts.Start(_ttsPolling);
    }

    public AdvisorSession? Session
    {
        get { lock (_lock) return _session; }
    }

    public AdviceService Advice => _advice;

    /// <summary>The Tabletop Simulator Adapter's polling half.</summary>
    public TtsAdapter Tts => _tts;

    public void Dispose()
    {
        _tts.Dispose();
        _advice.Dispose();
    }

    public static WebApplication Build(AdvisorOptions options, string[]? args = null)
    {
        var web = options.WebFolder ?? Path.Combine(AppContext.BaseDirectory, "web");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args ?? [], WebRootPath = web });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        var advice = new AdviceSettings(options.Agent, options.Simulations ?? AdviceSettings.DefaultSimulations);
        var server = new AdvisorServer(new AdvisorStore(options.DataFolder), advice, fixedBudget: options.Simulations is not null,
            options.TableSource, options.TtsPolling);
        builder.Services.AddSingleton(server);
        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(server.Dispose);
        server.Map(app, web);
        return app;
    }

    void Map(WebApplication app, string webFolder)
    {
        var files = new PhysicalFileProvider(webFolder);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            // A plain page with no build step: always fetch the latest copy.
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-store",
        });

        app.MapGet("/api/state", () => Respond(() => { }, save: false));
        app.MapPost("/api/new", (NewGameRequest r) => Respond(() => NewGame(r)));
        app.MapPost("/api/entry", (EntryRequest r) => Respond(() =>
        {
            if (r.Then is null) Current().Enter(r.Line ?? "");
            else Current().Enter([r.Line ?? "", r.Then]);
        }));
        app.MapPost("/api/undo", () => Respond(() =>
        {
            if (Current().Undo()) HoldTable();
        }));
        app.MapPost("/api/rewind", (RewindRequest r) => Respond(() =>
        {
            var before = Current().Log.Count;
            Current().RewindTo(r.Count);
            if (Current().Log.Count < before) HoldTable();
        }));
        app.MapPost("/api/abandon", () => Respond(() =>
        {
            if (_session is null) return;
            _store.SaveCurrent(_session);
            var moved = _store.ArchiveCurrent();
            _session = null;
            _notice = moved is null ? null : $"The game was saved to {moved}.";
        }, save: false));

        app.MapGet("/api/advice", () => { lock (_lock) return Results.Ok(_advice.View(_session)); });
        app.MapPost("/api/advice", (AdviceRequest r) =>
        {
            lock (_lock) return Results.Ok(_session is null ? _advice.View(null) : _advice.Request(_session, r.Longer));
        });
        app.MapPost("/api/advice/auto", (AutoRequest r) =>
        {
            lock (_lock)
            {
                _advice.SetAuto(r.On, _session);
                return Results.Ok(_advice.View(_session));
            }
        });

        app.MapGet("/api/tts", () => { lock (_lock) return Results.Ok(new TtsResponse(TtsBox(), _changes)); });
        app.MapPost("/api/tts", (TtsRequest r) => Respond(() => SetTts(r), save: false));
        app.MapPost("/api/tts/choose", (ChooseRequest r) => Respond(() => Choose(r.Index)));
    }

    AdvisorSession Current() => _session ?? throw new EntryException("No game in progress: start a new game first");

    void NewGame(NewGameRequest r)
    {
        var settings = new NewGameSettings(r.Messe, r.Liberty, r.Rule2015, r.Seat is 1 ? 1 : 0);
        _store.SaveSettings(settings);
        var moved = _store.ArchiveCurrent();
        _session = AdvisorSession.New(settings.ToRuleset(), settings.Seat);
        _hold.Clear();
        _lastAppended = null;
        _notice = moved is null ? null : $"The previous game was saved to {moved}.";
    }

    // ---------------------------------------------------------------- Tabletop Simulator

    void SetTts(TtsRequest r)
    {
        var prefs = _ttsPrefs;
        if (r.Color is { } color)
        {
            if (color is not ("White" or "Red")) throw new EntryException("Your TTS colour is White or Red");
            prefs = prefs with { Color = color };
        }
        if (r.Follow is { } follow) prefs = prefs with { Follow = follow };
        if (prefs != _ttsPrefs)
        {
            _ttsPrefs = prefs;
            try
            {
                _store.SaveTtsPrefs(prefs);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _notice = $"Could not save the TTS setting: {e.Message}";
            }
        }
        if (prefs.Follow && !_tts.Following)
        {
            _settled = null;
            _tts.Start(_ttsPolling);
        }
        else if (!prefs.Follow && _tts.Following) _tts.Stop();
        _hold.Clear();
        Reconcile(force: true);
    }

    /// <summary>Enters one of the offered chains (several readings of the table, or the table held after an Undo).</summary>
    void Choose(int index)
    {
        var session = Current();
        if (_sync is null || _syncLog != session.Write() || _sync.Status is not (SyncStatus.SeveralChains or SyncStatus.OneChain or SyncStatus.Waiting)
            || index < 0 || index >= _sync.Chains.Count)
            throw new EntryException("The table or the log changed since that choice was offered; look again.");
        var chain = _sync.Chains[index];
        session.Enter(chain.Lines);
        _lastAppended = chain.Text;
        _hold.Clear();
        Reconcile(force: true);
    }

    /// <summary>After a manual Undo or rewind the table still shows what was removed: don't put it straight back.</summary>
    void HoldTable()
    {
        if (_tts.Following)
        {
            _hold.Clear();
            if (_tts.LastSnapshot is { } last) _hold.Add(last.Text);
            if (_settled is { } settled) _hold.Add(settled.Text);
        }
        Reconcile(force: true);
    }

    /// <summary>A settled snapshot from the adapter's polling task.</summary>
    void OnSettled(TableSnapshot snapshot)
    {
        lock (_lock)
        {
            _settled = snapshot;
            Reconcile(force: false);
        }
    }

    /// <summary>
    /// Reconciles the last settled snapshot with the session (under the lock) and appends a single explaining chain,
    /// unless the table is held after a manual Undo. Skipped when neither the table nor the log changed.
    /// </summary>
    void Reconcile(bool force)
    {
        if (_settled is not { } snapshot || _session is null || !_tts.Following)
        {
            if (_sync is not null) _changes++;
            _sync = null;
            _syncText = _syncLog = _syncProblem = null;
            return;
        }
        var log = _session.Write();
        if (!force && snapshot.Text == _syncText && log == _syncLog) return;

        var sides = SeatSides.ForOwner(_session.Seat, Enum.Parse<Side>(_ttsPrefs.Color));
        var result = Reconciler.Reconcile(_session.State, _session.Seat, sides, snapshot);
        _held = false;
        _syncProblem = null;
        if (result.Status == SyncStatus.OneChain)
        {
            if (_hold.Contains(snapshot.Text))
                _held = true;
            else
            {
                try
                {
                    _session.Enter(result.Chains[0].Lines);
                    _lastAppended = result.Chains[0].Text;
                    SaveQuietly();
                    log = _session.Write();
                    result = Reconciler.Reconcile(_session.State, _session.Seat, sides, snapshot);
                    _advice.Sync(_session);
                }
                catch (EntryException e)
                {
                    _syncProblem = $"The table reads as \"{result.Chains[0].Text}\", but the Advisor refused it: {e.Message}";
                    result = result with { Status = SyncStatus.Desync };
                }
            }
        }
        if (!_hold.Contains(snapshot.Text)) _hold.Clear();
        _sync = result;
        _syncText = snapshot.Text;
        _syncLog = log;
        _changes++;
    }

    void SaveQuietly()
    {
        try
        {
            _store.SaveCurrent(_session!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _notice = $"Could not save the game to {_store.CurrentPath}: {e.Message} It is saved again after the next entry.";
        }
    }

    TtsView TtsBox()
    {
        var c = _tts.Connection;
        var empty = Array.Empty<string>();
        TtsView View(string status, string? message, IReadOnlyList<TableChain>? chains = null, Reconciliation? r = null) => new(
            c.Following, _ttsPrefs.Color, c.Connected, c.Problem, c.Settled, c.RoundTripMs, status, message,
            (chains ?? []).Select((ch, i) => new TtsChoiceView(i, ch.Text, ch.Lines)).ToList(),
            r?.NotOnTable ?? empty, r?.NotInLog ?? empty, r?.Notes ?? empty, _lastAppended);

        if (!c.Following) return View("off", null);
        if (_session is null) return View("no-game", "Start a new game here first; the Advisor follows the table from setup.");
        if (_sync is null || _syncLog != _session.Write())
            return View("reading", c.Connected ? "Waiting for the table to settle (nothing held or moving, the same twice in a row)." : null);
        var r = _sync;
        return r.Status switch
        {
            SyncStatus.Matches => View("matches", r.Waiting is null ? "The table matches the Move Log." : $"The table matches the Move Log. {r.Waiting}", r: r),
            SyncStatus.OneChain when _held => View("held",
                "You changed the log by hand, and the table still shows more. Enter it, or tidy the table: following resumes when the table changes.",
                r.Chains, r),
            SyncStatus.SeveralChains => View("choices", "The table can be read more than one way. Which happened?", r.Chains, r),
            SyncStatus.Waiting => View("waiting", r.Waiting, r.Chains, r),
            _ => View("desync", _syncProblem
                ?? "Desync: the table and the Move Log disagree. Tidy the table, or correct the log here (Undo, rewind or enter). Nothing is entered from the table until they agree.",
                r: r),
        };
    }

    /// <summary>
    /// Runs one change under the lock, saves the game, tells the advice service, and answers with the new view (or 400
    /// and the reason).
    /// </summary>
    IResult Respond(Action change, bool save = true)
    {
        lock (_lock)
        {
            string? error = null;
            try
            {
                change();
                if (save && _session is not null) _store.SaveCurrent(_session);
            }
            catch (EntryException e)
            {
                error = e.Message;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The change stands (the log in memory is the truth); the next change saves it again.
                _notice = $"Could not save the game to {_store.CurrentPath}: {e.Message} It is saved again after the next entry.";
            }
            _advice.Sync(_session);
            _changes++;
            var view = _session is null ? null : BoardView.Build(_session, _store.CurrentPath);
            var response = new StateResponse(view, view is null ? _store.LoadSettings() : null, error, _notice, _advice.View(_session),
                TtsBox(), _changes);
            _notice = null;
            return error is null ? Results.Ok(response) : Results.BadRequest(response);
        }
    }
}

public sealed record NewGameRequest(bool Messe, bool Liberty, bool Rule2015, int Seat);
public sealed record EntryRequest(string? Line, string? Then = null);
public sealed record RewindRequest(int Count);
public sealed record AdviceRequest(bool Longer);
public sealed record AutoRequest(bool On);
public sealed record TtsRequest(bool? Follow = null, string? Color = null);
public sealed record ChooseRequest(int Index);

/// <summary>
/// Every answer: the game view (null on the new-game screen, which gets the remembered settings instead), any error,
/// a one-time notice, the advice box for the current position, the Tabletop Simulator box, and the change counter.
/// </summary>
public sealed record StateResponse(GameView? Game, NewGameSettings? Settings, string? Error, string? Notice, AdviceView Advice,
    TtsView Tts, long Changes);

/// <summary>The Tabletop Simulator box alone, polled by the page while following the table.</summary>
public sealed record TtsResponse(TtsView Tts, long Changes);

/// <summary>
/// The Tabletop Simulator box. <see cref="Status"/>: off, no-game, reading (not settled yet), matches, held (the table
/// shows more than the log after a manual Undo), choices, waiting (a Reveal the table can't show), desync.
/// <see cref="NotOnTable"/> / <see cref="NotInLog"/> list the mismatches; <see cref="Notes"/> are the soft checks
/// (coins, the Conflict Pawn, looting tokens); <see cref="LastAppended"/> is the last chain entered from the table.
/// </summary>
public sealed record TtsView(bool Following, string Color, bool Connected, string? Problem, bool Settled, long? RoundTripMs,
    string Status, string? Message, IReadOnlyList<TtsChoiceView> Choices, IReadOnlyList<string> NotOnTable,
    IReadOnlyList<string> NotInLog, IReadOnlyList<string> Notes, string? LastAppended);

public sealed record TtsChoiceView(int Index, string Text, IReadOnlyList<string> Lines);
