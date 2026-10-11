using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace SevenWondersDuel.Advisor;

/// <summary>
/// Settings of one Advisor server. <paramref name="Agent"/> is the advice search (<c>mcts</c> or
/// <c>neural:&lt;onnx&gt;</c>); <paramref name="Simulations"/> fixes the advice budget (null: 800, or this computer's
/// reduced default).
/// </summary>
public sealed record AdvisorOptions(string DataFolder, int Port = AdvisorServer.DefaultPort, string? WebFolder = null,
    string Agent = AdviceSettings.DefaultAgent, int? Simulations = null);

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
/// </code>
/// Refused entries answer 400 with {error}. Adapters (Tabletop Simulator, ticket 32+) feed the same session; every
/// change goes through <see cref="Respond"/>, which tells the <see cref="AdviceService"/> that the position changed.
/// </summary>
public sealed class AdvisorServer : IDisposable
{
    public const int DefaultPort = 7700;

    readonly AdvisorStore _store;
    readonly AdviceService _advice;
    readonly Lock _lock = new();
    AdvisorSession? _session;
    string? _notice;

    public AdvisorServer(AdvisorStore store, AdviceSettings? advice = null, bool fixedBudget = false)
    {
        _store = store;
        _advice = new AdviceService(advice ?? new AdviceSettings(), store.LoadAdvicePrefs(), fixedBudget, store.AdviceLogPath,
            store.SaveAdvicePrefs);
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
    }

    public AdvisorSession? Session
    {
        get { lock (_lock) return _session; }
    }

    public AdviceService Advice => _advice;

    public void Dispose() => _advice.Dispose();

    public static WebApplication Build(AdvisorOptions options, string[]? args = null)
    {
        var web = options.WebFolder ?? Path.Combine(AppContext.BaseDirectory, "web");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args ?? [], WebRootPath = web });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        var app = builder.Build();
        var advice = new AdviceSettings(options.Agent, options.Simulations ?? AdviceSettings.DefaultSimulations);
        var server = new AdvisorServer(new AdvisorStore(options.DataFolder), advice, fixedBudget: options.Simulations is not null);
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
        app.MapPost("/api/undo", () => Respond(() => Current().Undo()));
        app.MapPost("/api/rewind", (RewindRequest r) => Respond(() => Current().RewindTo(r.Count)));
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
    }

    AdvisorSession Current() => _session ?? throw new EntryException("No game in progress: start a new game first");

    void NewGame(NewGameRequest r)
    {
        var settings = new NewGameSettings(r.Messe, r.Liberty, r.Rule2015, r.Seat is 1 ? 1 : 0);
        _store.SaveSettings(settings);
        var moved = _store.ArchiveCurrent();
        _session = AdvisorSession.New(settings.ToRuleset(), settings.Seat);
        _notice = moved is null ? null : $"The previous game was saved to {moved}.";
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
            var view = _session is null ? null : BoardView.Build(_session, _store.CurrentPath);
            var response = new StateResponse(view, view is null ? _store.LoadSettings() : null, error, _notice, _advice.View(_session));
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

/// <summary>
/// Every answer: the game view (null on the new-game screen, which gets the remembered settings instead), any error,
/// a one-time notice, and the advice box for the current position.
/// </summary>
public sealed record StateResponse(GameView? Game, NewGameSettings? Settings, string? Error, string? Notice, AdviceView Advice);
