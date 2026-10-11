using System.Text.Json;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Advisor;

/// <summary>The new-game choices the Advisor remembers (spec A8: the promo boxes keep the last choice).</summary>
public sealed record NewGameSettings(bool Messe = false, bool Liberty = false, bool Rule2015 = false, int Seat = 0)
{
    public Ruleset ToRuleset() => new(
        Rule2015 ? StartChoice.Rule2015 : StartChoice.New,
        (Messe ? Promos.Messe : Promos.None) | (Liberty ? Promos.Liberty : Promos.None));
}

/// <summary>
/// What this computer remembers about advice: the auto switch, and a reduced budget when 800 simulations were too
/// slow here (null = the configured budget).
/// </summary>
public sealed record AdvicePrefs(bool Auto = false, int? Simulations = null);

/// <summary>
/// Where the Advisor keeps its files: <c>current.log</c> (the game in progress, rewritten after every entry so a
/// restart resumes), <c>settings.json</c> (the last new-game choices), <c>advice.json</c> (advice preferences),
/// <c>advice.log</c> (each advice search's wall-clock time) and <c>games/</c> (every earlier game's log, kept when a
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

    /// <summary>Writes to a temporary file first, so a crash mid-write never leaves a half-written log.</summary>
    void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
