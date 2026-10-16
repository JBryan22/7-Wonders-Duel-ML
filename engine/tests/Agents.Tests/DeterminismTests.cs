using System.Security.Cryptography;
using System.Text;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// Cross-OS determinism (spec A11): the same seed gives byte-identical Move Logs and Game States on Linux and
/// Windows. The hashes below were taken on Windows; CI runs this on both, so any drift (RNG, iteration order,
/// formatting, line endings) fails on one of them. Change a pinned hash only for a deliberate rules or format change,
/// and say so in the commit.
/// </summary>
public class DeterminismTests
{
    const int Games = 200;

    [Fact]
    public void Random_games_give_pinned_move_logs_and_game_states()
    {
        using var logs = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var states = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (ulong seed = 0; seed < Games; seed++)
        {
            var game = GameRunner.PlayRandom(seed, Soak.RulesFor(seed));
            logs.AppendData(Encoding.UTF8.GetBytes(game.WriteLog()));
            // The state after every log entry, not only the final one.
            var replay = GameState.New(game.Rules);
            foreach (var entry in game.Log)
            {
                if (entry is MoveEntry m) replay.Apply(m.Move); else replay.Apply((Reveal)entry);
                states.AppendData(replay.ToBytes());
            }
            Assert.True(replay.SameAs(game.State));
        }
        Assert.Equal(PinnedLogs, Convert.ToHexString(logs.GetHashAndReset()));
        Assert.Equal(PinnedStates, Convert.ToHexString(states.GetHashAndReset()));
    }

    [Fact]
    public void One_pinned_game_reads_the_same_on_every_os()
    {
        // A short, human-readable anchor: if the hashes above drift, this shows where the first game went wrong.
        var game = GameRunner.PlayRandom(0);
        var log = game.WriteLog().Split('\n');
        Assert.Equal(PinnedFirstLines, log[..PinnedFirstLines.Length]);
        Assert.Equal(97, log.Length); // 96 lines and the final newline
        Assert.Equal("P1 wins (military) | P1 34 VP (blue 10, green 2, wonders 10, coins 2, military 10)"
            + " | P2 32 VP (blue 8, yellow 3, guilds 5, wonders 12, coins 4)", game.State.Result!.ToString());
    }

    /// <summary>
    /// The same for Pantheon games (spec-pantheon P-A10), with their own pinned hashes, taken on Linux (ticket 06);
    /// CI checks them on Windows too. Seeds cycle through the promo settings, both start rules each.
    /// </summary>
    [Fact]
    public void Random_pantheon_games_give_pinned_move_logs_and_game_states()
    {
        using var logs = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var states = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (ulong seed = 0; seed < Games; seed++)
        {
            var game = GameRunner.PlayRandom(seed, PantheonRules(seed));
            logs.AppendData(Encoding.UTF8.GetBytes(game.WriteLog()));
            var replay = GameState.New(game.Rules);
            foreach (var entry in game.Log)
            {
                if (entry is MoveEntry m) replay.Apply(m.Move); else replay.Apply((Reveal)entry);
                states.AppendData(replay.ToBytes());
            }
            Assert.True(replay.SameAs(game.State));
        }
        Assert.Equal(PinnedPantheonLogs, Convert.ToHexString(logs.GetHashAndReset()));
        Assert.Equal(PinnedPantheonStates, Convert.ToHexString(states.GetHashAndReset()));
    }

    [Fact]
    public void One_pinned_pantheon_game_reads_the_same_on_every_os()
    {
        var game = GameRunner.PlayRandom(0, PantheonRules(0));
        var log = game.WriteLog().Split('\n');
        Assert.Equal(PinnedPantheonFirstLines, log[..PinnedPantheonFirstLines.Length]);
        Assert.Equal(PinnedPantheonLength, log.Length);
        Assert.Equal(PinnedPantheonResult, game.State.Result!.ToString());
    }

    static Ruleset PantheonRules(ulong seed) => Soak.RulesFor(seed, (Promos)(seed / 2 % 4), Expansions.Pantheon);

    const string PinnedPantheonLogs = "D26F842123CC1CF92DD2F88A6C46D61E81FB040FB298EC177F097DB67D25A276";
    const string PinnedPantheonStates = "792337C3371558A559C10A9E3F552ED2F7B3E0568D74C199C4F89D22C888E11D";
    const int PinnedPantheonLength = 134; // 133 lines and the final newline
    const string PinnedPantheonResult = "P1 wins (civilian) | P1 28 VP (blue 11, wonders 6, coins 2, divinities 9)"
        + " | P2 25 VP (blue 11, green 1, wonders 7, progress 2, coins 2, military 2)";
    static readonly string[] PinnedPantheonFirstLines =
    [
        "7wd-log v1",
        "rules base start-choice=new expansion=pantheon",
        "reveal first P1",
        "reveal tokens economy law philosophy theology poliorcetics",
        "reveal draft circus-maximus colossus great-library divine-theater",
        "P1 draft circus-maximus",
        "P2 draft great-library",
        "P2 draft colossus",
        "P1 draft divine-theater",
        "reveal draft great-lighthouse piraeus temple-of-artemis sanctuary",
        "P2 draft piraeus",
        "P1 draft great-lighthouse",
        "P1 draft sanctuary",
        "P2 draft temple-of-artemis",
        "reveal age1 0.0 clay-pool 0.1 scriptorium 2.0 quarry 2.1 tavern 2.2 stable 2.3 press 4.0 theater 4.1 workshop 4.2 guard-tower 4.3 lumber-yard 4.4 logging-camp 4.5 altar",
        "reveal myth-tokens 1.0 greek 1.2 egyptian 3.0 greek 3.2 egyptian 3.4 roman",
        "P1 discard theater",
        "P2 build altar",
        "P1 wonder workshop sanctuary",
        "reveal 3.0 baths",
        "reveal P1 divinities greek hades aphrodite",
        "P1 divinity hades 6",
    ];

    const string PinnedLogs = "DC4A0858DBAD32AFC616BF38A7BFF4FC72F95903C546DC283EBE9638922FC907";
    const string PinnedStates = "69BAB1376774BB7611AC204C35EFC8E9B75DA7D832CEA1FB98BCCACEE8C8ED6A";
    static readonly string[] PinnedFirstLines =
    [
        "7wd-log v1",
        "rules base start-choice=new",
        "reveal first P1",
        "reveal tokens agriculture economy mathematics strategy theology",
        "reveal draft circus-maximus colossus great-library temple-of-artemis",
        "P1 draft circus-maximus",
        "P2 draft great-library",
        "P2 draft colossus",
        "P1 draft temple-of-artemis",
        "reveal draft great-lighthouse mausoleum sphinx statue-of-zeus",
        "P2 draft mausoleum",
        "P1 draft great-lighthouse",
        "P1 draft statue-of-zeus",
        "P2 draft sphinx",
        "reveal age1 0.0 clay-pool 0.1 scriptorium 2.0 quarry 2.1 tavern 2.2 stable 2.3 press 4.0 theater 4.1 workshop 4.2 guard-tower 4.3 lumber-yard 4.4 logging-camp 4.5 altar",
        "P1 discard theater",
    ];
}
