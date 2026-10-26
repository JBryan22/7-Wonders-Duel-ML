using System.Text.Json;
using System.Text.Json.Serialization;
using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Advisor;

/// <summary>
/// The new-game choices the Advisor remembers (spec A8: the promo boxes keep the last choice), and those of "Play against
/// the AI" (ticket 36): the mode, the opponent's spec and name, its simulations and a random seat. <see cref="Pantheon"/>
/// is the Pantheon expansion box (spec-pantheon P-A9).
/// </summary>
public sealed record NewGameSettings(bool Messe = false, bool Liberty = false, bool Rule2015 = false, int Seat = 0,
    bool VsAi = false, string? Opponent = null, string? OpponentLabel = null, int AiSimulations = AiOpponent.DefaultSimulations,
    bool RandomSeat = false, bool Pantheon = false)
{
    public Ruleset ToRuleset() => new(
        Rule2015 ? StartChoice.Rule2015 : StartChoice.New,
        (Messe ? Promos.Messe : Promos.None) | (Liberty ? Promos.Liberty : Promos.None),
        Pantheon ? Expansions.Pantheon : Expansions.None);
}

/// <summary>
/// What this computer remembers about advice: the auto switch, and a reduced budget when 800 simulations were too
/// slow here (null = the configured budget).
/// </summary>
public sealed record AdvicePrefs(bool Auto = false, int? Simulations = null);

/// <summary>
/// The Tabletop Simulator Adapter's switch (follow the table or not) and the owner's TTS seat colour: "Auto" (the
/// host's seat at the TTS table, spec A9; the owner hosts and usually sits White, on the right), or "White" / "Red" to
/// override it. <see cref="Pace"/>: how often the table is read, "live" (about every second), "10s" or "click" (only on
/// "Read the table now"); each reading stalls TTS for a moment.
/// </summary>
public sealed record TtsPrefs(bool Follow = false, string Color = TtsPrefs.Auto, string Pace = TtsPrefs.Live)
{
    public const string Auto = "Auto";
    public const string Live = "live";
    public static bool IsColor(string? color) => color is Auto or "White" or "Red";
    public static bool IsPace(string? pace) => pace is Live or "10s" or "click";

    [JsonIgnore] public TtsPace TtsPace => Pace switch { "10s" => TtsPace.Every10, "click" => TtsPace.OnClick, _ => TtsPace.Live };
}

/// <summary>
/// Where the Advisor keeps its files: <c>current.log</c> (the game in progress, rewritten after every entry so a
/// restart resumes), <c>settings.json</c> (the last new-game choices), <c>advice.json</c> (advice preferences),
/// <c>advice.log</c> (each advice search's wall-clock time), <c>tts.json</c> (following the TTS table) and <c>games/</c> (every earlier game's log, kept when a
/// new one starts). The default folder is per user: <c>%LOCALAPPDATA%\7wd\advisor</c> on Windows,
/// <c>~/.local/share/7wd/advisor</c> on Linux.
/// </summary>
public sealed class AdvisorStore(string folder)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Folder { get; } = Path.GetFullPath(folder);
    public string CurrentPath => Path.Combine(Folder, "current.log");
    string SettingsPath => Path.Combine(Folder, "settings.json");
    public string GamesFolder => Path.Combine(Folder, "games");

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create), "7wd", "advisor");

    /// <summary>The saved game in progress, or null if there is none.</summary>
    public AdvisorSession? LoadCurrent()
    {
        if (!File.Exists(CurrentPath)) return null;
        return AdvisorSession.Load(File.ReadAllText(CurrentPath));
    }

    public void SaveCurrent(AdvisorSession session) => WriteAtomically(CurrentPath, session.Write());

    /// <summary>Moves the game in progress into games/ (named by the time it was archived) so a new one can start.</summary>
    public string? ArchiveCurrent()
    {
        if (!File.Exists(CurrentPath)) return null;
        Directory.CreateDirectory(GamesFolder);
        var target = Path.Combine(GamesFolder, $"{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(GamesFolder, $"{DateTime.Now:yyyy-MM-dd_HHmmss}-{i}.log");
        File.Move(CurrentPath, target);
        return target;
    }

    public NewGameSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<NewGameSettings>(File.ReadAllText(SettingsPath), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void SaveSettings(NewGameSettings settings) =>
        WriteAtomically(SettingsPath, JsonSerializer.Serialize(settings, Json));

    string AdvicePrefsPath => Path.Combine(Folder, "advice.json");

    /// <summary>Each advice search's wall-clock time is appended here.</summary>
    public string AdviceLogPath => Path.Combine(Folder, "advice.log");

    public AdvicePrefs LoadAdvicePrefs()
    {
        try
        {
            return File.Exists(AdvicePrefsPath)
                ? JsonSerializer.Deserialize<AdvicePrefs>(File.ReadAllText(AdvicePrefsPath), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void SaveAdvicePrefs(AdvicePrefs prefs) =>
        WriteAtomically(AdvicePrefsPath, JsonSerializer.Serialize(prefs, Json));

    string TtsPrefsPath => Path.Combine(Folder, "tts.json");

    public TtsPrefs LoadTtsPrefs()
    {
        try
        {
            var prefs = File.Exists(TtsPrefsPath)
                ? JsonSerializer.Deserialize<TtsPrefs>(File.ReadAllText(TtsPrefsPath), Json) ?? new()
                : new();
            if (!TtsPrefs.IsPace(prefs.Pace)) prefs = prefs with { Pace = TtsPrefs.Live };
            return TtsPrefs.IsColor(prefs.Color) ? prefs : prefs with { Color = TtsPrefs.Auto };
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void SaveTtsPrefs(TtsPrefs prefs) => WriteAtomically(TtsPrefsPath, JsonSerializer.Serialize(prefs, Json));

    /// <summary>Every finished game against the AI (ticket 36): <c>vs-ai.json</c> next to the saved games.</summary>
    public string AiResultsPath => Path.Combine(Folder, "vs-ai.json");

    public List<AiGameRecord> LoadAiResults()
    {
        try
        {
            return File.Exists(AiResultsPath)
                ? JsonSerializer.Deserialize<AiResults>(File.ReadAllText(AiResultsPath), Json)?.Games ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds a finished game's result unless a game with the same seed is already there; true if it was added.</summary>
    public bool AddAiResult(AiGameRecord record)
    {
        var games = LoadAiResults();
        if (games.Any(g => g.Seed == record.Seed)) return false;
        games.Add(record);
        WriteAtomically(AiResultsPath, JsonSerializer.Serialize(new AiResults(games), Json));
        return true;
    }

    /// <summary>"You 2 – 1 AI (pilot-1-small Gen 3)": the results against one opponent spec.</summary>
    public TallyView Tally(string spec, string label)
    {
        var games = LoadAiResults().Where(g => g.Spec == spec).ToList();
        int you = games.Count(g => g.Winner == "you"), ai = games.Count(g => g.Winner == "ai"), draws = games.Count(g => g.Winner == "draw");
        var text = $"You {you} – {ai} AI ({label})" + (draws > 0 ? $" · {draws} draw{(draws > 1 ? "s" : "")}" : "");
        return new TallyView(label, you, ai, draws, text);
    }

    sealed record AiResults(List<AiGameRecord> Games);

    /// <summary>
    /// Writes to a temporary file first, so a crash mid-write never leaves a half-written log. On Windows the replace
    /// can fail for a moment while a virus scanner or the search indexer has the file just written open (a sharing
    /// violation, seen as a rare failed entry), so it is retried for up to about two seconds.
    /// </summary>
    void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(temp, text);
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < SaveAttempts)
            {
                Thread.Sleep(Math.Min(10 * attempt, 100));
            }
        }
    }

    const int SaveAttempts = 25;
}
