using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;

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
/// POST /api/tts     {follow?, color?}  follow the TTS table or stop; the owner's seat colour (Auto / White / Red)
/// POST /api/tts/choose {index} enter one of the chains offered when the table can be read several ways
/// POST /api/tts/newgame        start the new game detected on the table, with the remembered new-game settings
/// POST /api/tts/promo {promo}  enable a promo Wonder (messe / liberty) for this game: the header is rewritten, the log replayed
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
    // The game life cycle (ticket 34), from the last settled snapshot: the owner's colour (null: the host isn't seated at
    // White or Red), a fresh setup on the table, promo Wonders on the table that the game was started without, and the
    // catch-up mode: the first reading after a restart, after following starts, a new game or TTS loading a game may be
    // many Moves ahead, so the chain search goes deeper until the table and the log match.
    Side? _side;
    bool _fresh;
    Promos _missingPromos;
    bool _catchUp = true;

    public AdvisorServer(AdvisorStore store, AdviceSettings? advice = null, bool fixedBudget = false,
        Func<ITableSource>? tableSource = null, bool ttsPolling = true)
    {
        _store = store;
        _advice = new AdviceService(advice ?? new AdviceSettings(), store.LoadAdvicePrefs(), fixedBudget, store.AdviceLogPath,
            store.SaveAdvicePrefs);
        _tts = new TtsAdapter(tableSource ?? TtsLinkSource.Open, OnSettled, loaded: OnLoaded);
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
        app.MapPost("/api/new", (NewGameRequest r) => Respond(() => NewGame(new NewGameSettings(r.Messe, r.Liberty, r.Rule2015, r.Seat is 1 ? 1 : 0))));
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
        app.MapPost("/api/tts/newgame", () => Respond(() => NewGame(_store.LoadSettings())));
        app.MapPost("/api/tts/promo", (PromoRequest r) => Respond(() => EnablePromo(r.Promo)));
    }

    AdvisorSession Current() => _session ?? throw new EntryException("No game in progress: start a new game first");

    void NewGame(NewGameSettings settings)
    {
        _store.SaveSettings(settings);
        var moved = _store.ArchiveCurrent();
        _session = AdvisorSession.New(settings.ToRuleset(), settings.Seat);
        _hold.Clear();
        _lastAppended = null;
        _notice = moved is null ? null : $"The previous game was saved to {moved}.";
        // The table may already be a few draft picks in: catch up with it.
        _catchUp = true;
        Reconcile(force: true);
    }

    /// <summary>
    /// "Enable &lt;promo&gt; for this game" (spec A9): the promo Wonder is on the table, so the rules header gains it and the
    /// log is replayed. The new-game screen remembers it for the next game too.
    /// </summary>
    void EnablePromo(string? id)
    {
        var session = Current();
        var flag = id switch
        {
            "messe" => Promos.Messe,
            "liberty" => Promos.Liberty,
            _ => throw new EntryException($"Unknown promo '{id}' (messe or liberty)"),
        };
        if (session.Rules.HasPromo(flag)) return;
        _session = session.WithRules(session.Rules with { Promos = session.Rules.Promos | flag });
        var settings = _store.LoadSettings();
        try
        {
            _store.SaveSettings(flag == Promos.Messe ? settings with { Messe = true } : settings with { Liberty = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var name = TableLifecycle.Each(flag).Single().Name;
        _notice = $"{name} is now in this game's Wonder pool (the log was replayed with it).";
        _hold.Clear();
        Reconcile(force: true);
    }

    // ---------------------------------------------------------------- Tabletop Simulator

    void SetTts(TtsRequest r)
    {
        var prefs = _ttsPrefs;
        if (r.Color is { } color)
        {
            if (!TtsPrefs.IsColor(color)) throw new EntryException("Your TTS colour is Auto (your seat at the TTS table), White or Red");
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
            _catchUp = true;
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
    /// TTS loaded a game (a new table, or a save of this game further on): the next settled table is read in catch-up
    /// mode, and a hold after a manual Undo no longer applies.
    /// </summary>
    void OnLoaded()
    {
        lock (_lock)
        {
            _catchUp = true;
            _hold.Clear();
            _syncText = null; // read the next settled table again even if it looks the same
            _changes++;
        }
    }

    /// <summary>
    /// Reads the last settled snapshot (under the lock): the owner's colour, a fresh setup, then reconciliation with the
    /// session, appending a single explaining chain unless the table is held after a manual Undo. Skipped when neither
    /// the table nor the log changed. In catch-up mode the chain search goes deeper (<see cref="ReconcileOptions.CatchUp"/>)
    /// until the table and the log match.
    /// </summary>
    void Reconcile(bool force)
    {
        if (_settled is not { } snapshot || !_tts.Following)
        {
            if (_sync is not null || _syncText is not null) _changes++;
            _sync = null;
            _syncText = _syncLog = _syncProblem = null;
            _fresh = false;
            _side = null;
            _missingPromos = Promos.None;
            return;
        }
        var log = _session?.Write();
        if (!force && snapshot.Text == _syncText && log == _syncLog) return;

        _side = TableLifecycle.OwnerSide(_ttsPrefs.Color, snapshot);
        _fresh = TableLifecycle.LooksFresh(snapshot);
        _sync = null;
        _syncProblem = null;
        _held = false;
        _missingPromos = Promos.None;
        _syncText = snapshot.Text;
        _syncLog = log;
        _changes++;
        if (_session is null || _side is not { } side) return;

        var sides = SeatSides.ForOwner(_session.Seat, side);
        var options = _catchUp ? ReconcileOptions.CatchUp : null;
        var result = Reconciler.Reconcile(_session.State, _session.Seat, sides, snapshot, options);
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
                    result = Reconciler.Reconcile(_session.State, _session.Seat, sides, snapshot, options);
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
        if (result.Status == SyncStatus.Matches) _catchUp = false;
        if (result.Status is SyncStatus.Desync or SyncStatus.Waiting)
            _missingPromos = TableLifecycle.MissingPromos(snapshot, _session.Rules);
        _sync = result;
        _syncLog = log;
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

    /// <summary>What the one-click start of a detected new game uses: the remembered new-game settings.</summary>
    string NewGameSummary()
    {
        var s = _store.LoadSettings();
        var promos = s.Messe && s.Liberty ? "The Messe and the Statue of Liberty" : s.Messe ? "The Messe" : s.Liberty ? "the Statue of Liberty" : "no promo Wonders";
        return $"{promos} · you are P{s.Seat + 1} · {(s.Rule2015 ? "2015 start rule" : "current start rule")}";
    }

    TtsView TtsBox()
    {
        var c = _tts.Connection;
        var empty = Array.Empty<string>();
        var detected = _ttsPrefs.Color == TtsPrefs.Auto;
        TtsView View(string status, string? message, IReadOnlyList<TableChain>? chains = null, Reconciliation? r = null,
            Promos promos = Promos.None, string? newGame = null) => new(
            c.Following, _ttsPrefs.Color, c.Connected, c.Problem, c.Settled, c.RoundTripMs, status, message,
            (chains ?? []).Select((ch, i) => new TtsChoiceView(i, ch.Text, ch.Lines)).ToList(),
            r?.NotOnTable ?? empty, r?.NotInLog ?? empty, r?.Notes ?? empty, _lastAppended,
            _side?.ToString(), detected && _side is not null,
            TableLifecycle.Each(promos).Select(p => new TtsPromoView(p.Id, p.Name)).ToList(), newGame, _catchUp && _session is not null);

        if (!c.Following) return View("off", null);
        // The colour first: nothing is read from the table until the Advisor knows which side is the owner's.
        if (_syncText is not null && _side is null)
            return View("seat", "Which colour do you play? Your TTS seat isn't White or Red: sit at White or Red in TTS, or choose your colour under \"You sit\".");
        if (_session is null)
            return _syncText is not null && _fresh
                ? View("new-game", "New game detected on the table. Start it here?", newGame: NewGameSummary())
                : View("no-game", "Start a new game here first; the Advisor follows the table from setup (it can't join a game already under way).");
        if (_syncText is null || _syncLog != _session.Write())
            return View("reading", !c.Connected ? null
                : _catchUp ? "Catching up with the table (the game may have gone on while the Advisor was closed)…"
                : "Waiting for the table to settle (nothing held or moving, the same twice in a row).");
        var r = _sync!;
        if (_fresh && r.Status == SyncStatus.Desync)
            return View("new-game",
                "New game detected: the table shows a fresh setup, but the Move Log is a game in progress. Start a new game? (This one is saved.) Nothing is entered from the table until you do.",
                r: r, newGame: NewGameSummary());
        if (_missingPromos != Promos.None)
        {
            var each = TableLifecycle.Each(_missingPromos).ToList();
            var names = string.Join(" and ", each.Select(p => p.Name));
            var one = each.Count == 1;
            return View("promo",
                $"{names} {(one ? "is" : "are")} in the Wonder draft on the table, but this game was started without {(one ? "it" : "them")}. "
                + $"Enable {(one ? "it" : "them")} for this game? The Move Log is replayed with the bigger Wonder pool; nothing else changes.",
                r: r, promos: _missingPromos);
        }
        return r.Status switch
        {
            SyncStatus.Matches => View("matches", r.Waiting is null ? "The table matches the Move Log." : $"The table matches the Move Log. {r.Waiting}", r: r),
            SyncStatus.OneChain when _held => View("held",
                "You changed the log by hand, and the table still shows more. Enter it, or tidy the table: following resumes when the table changes.",
                r.Chains, r),
            SyncStatus.SeveralChains => View("choices", "The table can be read more than one way. Which happened?", r.Chains, r),
            SyncStatus.Waiting => View("waiting", r.Waiting, r.Chains, r),
            _ => View("desync", _syncProblem
                ?? (_catchUp
                    ? "Desync while catching up: the table is further ahead than the Advisor can work out (more than about 8 Moves), or something lies in the wrong place. Enter the Moves made while the Advisor was closed here by hand (or tidy the table); it follows again once they agree."
                    : "Desync: the table and the Move Log disagree. Tidy the table, or correct the log here (Undo, rewind or enter). Nothing is entered from the table until they agree."),
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
public sealed record PromoRequest(string? Promo);

/// <summary>
/// Every answer: the game view (null on the new-game screen, which gets the remembered settings instead), any error,
/// a one-time notice, the advice box for the current position, the Tabletop Simulator box, and the change counter.
/// </summary>
public sealed record StateResponse(GameView? Game, NewGameSettings? Settings, string? Error, string? Notice, AdviceView Advice,
    TtsView Tts, long Changes);

/// <summary>The Tabletop Simulator box alone, polled by the page while following the table.</summary>
public sealed record TtsResponse(TtsView Tts, long Changes);

/// <summary>
/// The Tabletop Simulator box. <see cref="Status"/>: off, no-game, new-game (a fresh setup on the table: start a new
/// game?), reading (not settled yet), seat (the host isn't seated at White or Red: which colour?), matches, held (the
/// table shows more than the log after a manual Undo), choices, waiting (a Reveal the table can't show), promo (a promo
/// Wonder on the table the game was started without: enable it?), desync.
/// <see cref="Color"/> is the owner's choice (Auto / White / Red), <see cref="Side"/> the colour in use and
/// <see cref="SideDetected"/> whether it came from the host's TTS seat. <see cref="Promos"/> are the promos to offer,
/// <see cref="NewGame"/> what the one-click new game would use, <see cref="CatchingUp"/> the deeper catch-up search.
/// <see cref="NotOnTable"/> / <see cref="NotInLog"/> list the mismatches; <see cref="Notes"/> are the soft checks
/// (coins, the Conflict Pawn, looting tokens); <see cref="LastAppended"/> is the last chain entered from the table.
/// </summary>
public sealed record TtsView(bool Following, string Color, bool Connected, string? Problem, bool Settled, long? RoundTripMs,
    string Status, string? Message, IReadOnlyList<TtsChoiceView> Choices, IReadOnlyList<string> NotOnTable,
    IReadOnlyList<string> NotInLog, IReadOnlyList<string> Notes, string? LastAppended, string? Side, bool SideDetected,
    IReadOnlyList<TtsPromoView> Promos, string? NewGame, bool CatchingUp);

public sealed record TtsPromoView(string Id, string Name);

public sealed record TtsChoiceView(int Index, string Text, IReadOnlyList<string> Lines);
