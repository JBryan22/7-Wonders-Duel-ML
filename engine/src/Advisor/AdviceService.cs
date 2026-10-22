using System.Diagnostics;
using System.Globalization;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Advisor;

// The JSON of the advice box (web/app.js).
// A row's Key is its log line (a Statue pair: both lines, newline-separated); Then is a pair's second line. Outcome is
// null without an outcome head; Expected starts with the row's own Moves (Own), and HasLine is false for "no line".
public sealed record AdviceRowView(string Label, string Line, int? Slot, int Visits, double Share, double Win, double StdErr,
    double Gap, double Noise, bool Same, bool Best, string Key, string? Then, IReadOnlyList<string> Reasons,
    IReadOnlyList<ExpectedStep> Expected, bool HasLine, double[]? Outcome);
public sealed record SlotBadgeView(int Slot, double Share, bool Top);
public sealed record AdviceView(
    string Status, string? Message, string Agent, int Simulations, bool Longer, double Seconds,
    AdviceRowView[] Rows, Dictionary<string, double> Shares, SlotBadgeView[] Badges,
    int DefaultSimulations, int ThinkLongerSimulations, bool Auto, string? Notice, bool HasOutcome, string[] OutcomeClasses);

/// <summary>
/// Advice on request (spec A8), computed in the background so the Advisor Window stays responsive.
/// <para>
/// A request searches the current position of an <see cref="AdvisorSession"/> from the owner's Observation. Whatever
/// feeds the session (manual entry now, the Tabletop Simulator Adapter later) calls <see cref="Sync"/> after every
/// change: a search for a position that is no longer current is cancelled, and with <see cref="Auto"/> on, a new one
/// starts by itself whenever the owner is to decide. Results are kept per position and budget, so undoing and
/// re-entering a Move shows its advice again at once. A position's key is its saved Move Log, and the search's seed
/// comes from it, so the same position always gets the same advice.
/// </para>
/// <para>
/// Every search's wall-clock time is logged (console and <c>advice.log</c>). If a normal request at 800 simulations
/// takes more than 3 s twice in a row, the local default drops to 400 and is remembered (spec A8, the laptop rule).
/// </para>
/// </summary>
public sealed class AdviceService : IDisposable
{
    const int CacheSize = 64;

    sealed class Job(string key, int simulations, bool longer, Observation observation, string agent)
    {
        public readonly string Agent = agent;
        public readonly string Key = key;
        public readonly int Simulations = simulations;
        public readonly bool Longer = longer;
        public readonly Observation Observation = observation;
        public readonly GameState State = observation.State;
        public readonly CancellationTokenSource Cancel = new();
        public Task Task = Task.CompletedTask;
        public string? Error;
    }

    sealed record Result(Advice Advice, bool Longer);

    readonly Lock _lock = new();
    readonly Lock _logLock = new();
    readonly AdviceSettings _settings;
    /// <summary>The search for Pantheon games (spec-pantheon P-A7), worked out once.</summary>
    readonly string _pantheonAgent;
    readonly string? _logPath;
    readonly Action<AdvicePrefs>? _savePrefs;
    readonly List<(string Key, int Simulations, Result Result)> _cache = [];
    readonly bool _fixedBudget;
    Job? _job;
    Job? _failed;
    int _slowInARow;
    string? _notice;

    /// <param name="settings">The search and the configured budget.</param>
    /// <param name="prefs">What this computer remembered: the auto switch and a reduced budget.</param>
    /// <param name="fixedBudget">True when the budget was given explicitly (<c>--sims</c>): it is never reduced.</param>
    /// <param name="logPath">Where each search's wall-clock time is appended (null: console only).</param>
    /// <param name="savePrefs">Saves changed preferences.</param>
    public AdviceService(AdviceSettings settings, AdvicePrefs? prefs = null, bool fixedBudget = false, string? logPath = null,
        Action<AdvicePrefs>? savePrefs = null)
    {
        settings.Validate();
        _settings = settings;
        _pantheonAgent = settings.AgentFor(new Ruleset(Expansions: Expansions.Pantheon));
        _fixedBudget = fixedBudget;
        _logPath = logPath;
        _savePrefs = savePrefs;
        Auto = prefs?.Auto ?? false;
        DefaultSimulations = !fixedBudget && prefs?.Simulations is { } s and > 0 ? s : settings.Simulations;
    }

    public string Agent => _settings.Agent;

    /// <summary>The search that advises a game of these rules: the base one, or the Pantheon one.</summary>
    public string AgentFor(Ruleset rules) => rules.HasExpansion(Expansions.Pantheon) ? _pantheonAgent : _settings.Agent;

    /// <summary>The budget of a normal request on this computer (800, or 400 after the slow-computer rule).</summary>
    public int DefaultSimulations { get; private set; }

    /// <summary>The Think longer button's budget: 4× the configured budget (3,200).</summary>
    public int ThinkLongerSimulations => _settings.Simulations * 4;

    /// <summary>More seconds than this at 800 simulations counts as slow (tests lower it).</summary>
    internal double SlowSeconds { get; init; } = AdviceSettings.SlowSeconds;

    /// <summary>When on, advice starts by itself whenever the owner is to decide.</summary>
    public bool Auto { get; private set; }

    /// <summary>Why there is no advice for this position (null when the owner is to decide).</summary>
    public static string? WhyNoAdvice(AdvisorSession? session)
    {
        if (session is null) return "No game in progress.";
        var s = session.State;
        if (s.IsOver) return "The game is over.";
        if (s.Step == StepKind.Reveal) return "Name the revealed cards first.";
        if (s.PlayerToDecide != session.Seat)
            return $"Advice is for your Moves (P{session.Seat + 1}); it is P{s.PlayerToDecide + 1}'s turn.";
        return null;
    }

    /// <summary>Starts advice for the session's position (a normal request, or Think longer) unless it is already known or running.</summary>
    public AdviceView Request(AdvisorSession session, bool longer)
    {
        lock (_lock)
        {
            if (WhyNoAdvice(session) is null) Start(session, longer);
            return ViewLocked(session);
        }
    }

    /// <summary>Call after every change to the session: cancels a search for an old position and, with <see cref="Auto"/>, starts a new one.</summary>
    public void Sync(AdvisorSession? session)
    {
        lock (_lock)
        {
            var key = session is null ? null : KeyOf(session);
            if (_job is not null && _job.Key != key) CancelLocked();
            if (Auto && session is not null && WhyNoAdvice(session) is null && _job is null) Start(session, longer: false);
        }
    }

    public void SetAuto(bool on, AdvisorSession? session)
    {
        lock (_lock)
        {
            Auto = on;
            SavePrefsLocked();
        }
        Sync(session);
    }

    public AdviceView View(AdvisorSession? session)
    {
        lock (_lock) return ViewLocked(session);
    }

    /// <summary>The running search, if any (tests wait on it).</summary>
    public Task Running
    {
        get { lock (_lock) return _job?.Task ?? Task.CompletedTask; }
    }

    /// <summary>The finished advice for the session's position at the largest budget searched so far, or null.</summary>
    public Advice? Latest(AdvisorSession session)
    {
        lock (_lock) return Cached(KeyOf(session))?.Advice;
    }

    public void Dispose()
    {
        lock (_lock) CancelLocked();
    }

    static string KeyOf(AdvisorSession session) => session.Write();

    void Start(AdvisorSession session, bool longer)
    {
        var key = KeyOf(session);
        var simulations = longer ? ThinkLongerSimulations : DefaultSimulations;
        if (_job is not null && _job.Key == key && (_job.Simulations >= simulations)) return;
        if (Cached(key) is { } done && done.Advice.Simulations >= simulations) return;
        CancelLocked();
        _failed = null;
        var job = new Job(key, simulations, longer, session.State.Observe(session.Seat), AgentFor(session.Rules));
        _job = job;
        var seed = Seed(key);
        job.Task = Task.Run(() => Run(job, seed));
    }

    void Run(Job job, ulong seed)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var outcome = AdviceSearch.Run(job.Observation, job.Agent, job.Simulations, seed, job.Cancel.Token);
            var seconds = watch.Elapsed.TotalSeconds;
            var advice = new Advice(job.Key, job.Agent, outcome.Simulations, seconds, AdviceTable.Rank(job.State, outcome), outcome.Line)
            {
                HasOutcome = outcome.HasOutcome,
            };
            Log(job, seconds, advice.Best.Label);
            lock (_lock)
            {
                if (_job != job) return;
                _cache.RemoveAll(c => c.Key == job.Key && c.Simulations == job.Simulations);
                _cache.Add((job.Key, job.Simulations, new Result(advice, job.Longer)));
                if (_cache.Count > CacheSize) _cache.RemoveAt(0);
                _job = null;
                CheckSpeedLocked(job, seconds);
            }
        }
        catch (OperationCanceledException)
        {
            // The position changed; nothing to keep.
        }
        catch (Exception e)
        {
            Log(job, watch.Elapsed.TotalSeconds, $"failed: {e.Message}");
            lock (_lock)
            {
                if (_job != job) return;
                job.Error = e.Message;
                _failed = job;
                _job = null;
            }
        }
    }

    /// <summary>The laptop rule: 800 simulations taking more than ~3 s twice in a row drops this computer's default to 400.</summary>
    void CheckSpeedLocked(Job job, double seconds)
    {
        if (job.Longer || _fixedBudget || job.Simulations != AdviceSettings.DefaultSimulations) return;
        _slowInARow = seconds > SlowSeconds ? _slowInARow + 1 : 0;
        if (_slowInARow < 2) return;
        DefaultSimulations = AdviceSettings.ReducedSimulations;
        _notice = $"This computer needed {seconds:0.0} s for {AdviceSettings.DefaultSimulations} simulations, so advice now uses " +
                  $"{AdviceSettings.ReducedSimulations} by default. Think longer still runs {ThinkLongerSimulations:N0}.";
        SavePrefsLocked();
    }

    void SavePrefsLocked() =>
        _savePrefs?.Invoke(new AdvicePrefs(Auto, DefaultSimulations != _settings.Simulations ? DefaultSimulations : null));

    void CancelLocked()
    {
        if (_job is null) return;
        _job.Cancel.Cancel();
        _job = null;
    }

    Result? Cached(string key)
    {
        Result? best = null;
        foreach (var c in _cache)
            if (c.Key == key && (best is null || c.Result.Advice.Simulations > best.Advice.Simulations)) best = c.Result;
        return best;
    }

    AdviceView ViewLocked(AdvisorSession? session)
    {
        var notice = _notice;
        _notice = null;
        AdviceView Make(string status, string? message, Result? result = null, Job? job = null)
        {
            var advice = result?.Advice;
            var rows = advice?.Rows ?? [];
            var badges = rows.Where(r => r.Slot is not null)
                .GroupBy(r => r.Slot!.Value)
                .Select(g => new SlotBadgeView(g.Key, g.Sum(r => r.Share), g.Any(r => r.Best)))
                .OrderBy(b => b.Slot)
                .ToArray();
            return new AdviceView(status, message, session is null ? _settings.Agent : AgentFor(session.Rules),
                advice?.Simulations ?? job?.Simulations ?? 0, result?.Longer ?? job?.Longer ?? false, advice?.Seconds ?? 0,
                [.. rows.Take(AdviceTable.MaxRows).Select(r => new AdviceRowView(r.Label, r.Line, r.Slot, r.Visits, r.Share, r.Win,
                    r.StdErr, r.Gap, r.Noise, r.Same, r.Best, r.Key, r.ThenLine, r.Reasons, r.Expected, r.HasLine, r.Outcome))],
                Shares(rows), badges,
                DefaultSimulations, ThinkLongerSimulations, Auto, notice, advice?.HasOutcome ?? false, NetworkEncoding.OutcomeClasses);
        }

        // Every option's share by its key; a Statue keep's line also gets the sum of its pairs.
        static Dictionary<string, double> Shares(IReadOnlyList<AdviceRow> rows)
        {
            var shares = new Dictionary<string, double>();
            foreach (var r in rows)
            {
                shares[r.Key] = r.Share;
                if (r.ThenLine is not null) shares[r.Line] = shares.GetValueOrDefault(r.Line) + r.Share;
            }
            return shares;
        }

        if (WhyNoAdvice(session) is { } why) return Make("unavailable", why);
        var key = KeyOf(session!);
        var cached = Cached(key);
        if (_job is not null && _job.Key == key)
            return Make("thinking", $"Thinking ({_job.Simulations:N0} simulations)…", cached, _job);
        if (_failed is not null && _failed.Key == key) return Make("failed", $"Advice failed: {_failed.Error}");
        return cached is null ? Make("idle", null) : Make("ready", null, cached);
    }

    void Log(Job job, double seconds, string what)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} advice agent={job.Agent} sims={job.Simulations} seconds={seconds:0.000} " +
            $"log-lines={job.Key.Count(c => c == '\n') - 3} best=\"{what}\"");
        Console.WriteLine(line);
        if (_logPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            lock (_logLock) File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Logging must never break advice.
        }
    }

    /// <summary>FNV-1a of the position key: the same position always gets the same search.</summary>
    static ulong Seed(string key)
    {
        var h = 14695981039346656037UL;
        foreach (var c in key)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }
}
