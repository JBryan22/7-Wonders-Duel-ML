using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace SevenWondersDuel.Advisor;

/// <summary>Settings of one Advisor server.</summary>
public sealed record AdvisorOptions(string DataFolder, int Port = AdvisorServer.DefaultPort, string? WebFolder = null);

/// <summary>
/// The Advisor Window's local web server (spec A8): serves the page from web/ and a small JSON API. It listens on
/// localhost only. Every change is saved to disk before it is answered, so closing the window or the server loses
/// nothing.
/// <code>
/// GET  /api/state              the game view (or the new-game screen when there is no game)
/// POST /api/new     {messe, liberty, rule2015, seat}   start a new game (the previous one is archived)
/// POST /api/entry   {line}     enter one Move Log line ("P1 build baths", "reveal 3.1 baths", ...)
/// POST /api/undo               remove the last Move and the Reveals after it
/// POST /api/rewind  {count}    keep only the first count log lines
/// POST /api/abandon            archive the game and go back to the new-game screen
/// </code>
/// Refused entries answer 400 with {error}. Adapters (Tabletop Simulator, ticket 32+) feed the same session.
/// </summary>
public sealed class AdvisorServer
{
    public const int DefaultPort = 7700;

    readonly AdvisorStore _store;
    readonly Lock _lock = new();
    AdvisorSession? _session;
    string? _notice;

    public AdvisorServer(AdvisorStore store)
    {
        _store = store;
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
    }

    public AdvisorSession? Session
    {
        get { lock (_lock) return _session; }
    }

    public static WebApplication Build(AdvisorOptions options, string[]? args = null)
    {
        var web = options.WebFolder ?? Path.Combine(AppContext.BaseDirectory, "web");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args ?? [], WebRootPath = web });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        var app = builder.Build();
        var server = new AdvisorServer(new AdvisorStore(options.DataFolder));
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
        app.MapPost("/api/entry", (EntryRequest r) => Respond(() => Current().Enter(r.Line ?? "")));
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

    /// <summary>Runs one change under the lock, saves the game, and answers with the new view (or 400 and the reason).</summary>
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
            var view = _session is null ? null : BoardView.Build(_session, _store.CurrentPath);
            var response = new StateResponse(view, view is null ? _store.LoadSettings() : null, error, _notice);
            _notice = null;
            return error is null ? Results.Ok(response) : Results.BadRequest(response);
        }
    }
}

public sealed record NewGameRequest(bool Messe, bool Liberty, bool Rule2015, int Seat);
public sealed record EntryRequest(string? Line);
public sealed record RewindRequest(int Count);
/// <summary>Every answer: the game view (null on the new-game screen, which gets the remembered settings instead), any error and a one-time notice.</summary>
public sealed record StateResponse(GameView? Game, NewGameSettings? Settings, string? Error, string? Notice);
