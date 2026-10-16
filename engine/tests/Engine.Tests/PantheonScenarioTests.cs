using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Hand-traced Pantheon games (spec-pantheon P-A10, Pantheon ticket 06). There is no second Pantheon engine to
/// cross-check against, so each scripted game's result was worked out by hand from the rules, reading its Move Log
/// (<c>PantheonScenarios/*.log</c>, annotated with running coin totals), and only then compared with the Engine. The
/// scripts keep the arithmetic checkable: every turn not named in the log's header discards a card, worth 2 coins
/// because nobody builds a yellow card, and nobody produces a resource, so a missing resource costs 2 coins.
/// Activation costs are the player's cost for the space (P1 pays 3..8 along spaces 1..6, P2 8..3), doubled for the
/// Gate. Coins score 1 VP per 3 (rounded down).
/// </summary>
public class PantheonScenarioTests
{
    /// <summary>The scenario's state at the end, or just after the first line equal to <paramref name="until"/>.</summary>
    static GameState ReplayUntil(string file, string until)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PantheonScenarios", file));
        return MoveLog.Parse(string.Join('\n', lines.Take(Array.IndexOf(lines, until) + 1)) + "\n").State;
    }

    static GameState Replay(string file)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PantheonScenarios", file));
        var game = MoveLog.Parse(text);
        Assert.Equal(text.Split('\n').Count(l => l.Length > 0 && l[0] != '#') - 2, game.Log.Count);
        return game.State;
    }

    static void AssertSheet(ScoreSheet sheet, params (ScoreCategory Category, int Points)[] expected)
    {
        foreach (var category in Enum.GetValues<ScoreCategory>())
            Assert.True(expected.Where(e => e.Category == category).Sum(e => e.Points) == sheet[category],
                $"{category}: expected {expected.Where(e => e.Category == category).Sum(e => e.Points)}, got {sheet[category]}");
        Assert.Equal(expected.Sum(e => e.Points), sheet.Total);
    }

    [Fact]
    public void Divinity_points()
    {
        var s = Replay("divinity-points.log");
        // Age I: 10 discards each: 7 + 20 = 27 coins each. P1 takes the phoenician (3.0) and both greek tokens (3.4,
        // 1.2); P2 the other phoenician (3.2) and the egyptian (1.0).
        // Age II: P1 Aphrodite on space 5 (P1 pays 7): 20; P2 Tanit on space 1 (P2 pays 8, gains 12): 31; P1 Astarte on
        // space 2 (P1 pays 4): 16, plus 7 of Astarte's coins. Then 10 discards each: P1 36, P2 51.
        // Age III: P1 builds the Greek Grand Temple free (greek token, R9) and discards 9: 54. P2 builds the Egyptian
        // and Phoenician Grand Temples free and discards 8: 67.
        Assert.Equal((54, 67), (s.Coins(0), s.Coins(1)));
        Assert.Equal(7, s.Pantheon!.AstarteCoins(0));
        var r = s.Result!;
        // P1: coins 54 / 3 = 18; Divinities: Aphrodite 9 + Astarte's 7 coins left = 16; 1 Temple = 5. 39 VP.
        AssertSheet(r.P1, (ScoreCategory.Coins, 18), (ScoreCategory.Divinities, 9 + 7), (ScoreCategory.Temples, 5));
        // P2: coins 67 / 3 = 22; 2 Temples = 12. 34 VP.
        AssertSheet(r.P2, (ScoreCategory.Coins, 22), (ScoreCategory.Temples, 12));
        Assert.Equal((0, VictoryType.Civilian), (r.Winner, r.Victory));
    }

    [Fact]
    public void War()
    {
        var s = Replay("war-minerva-mars.log");
        var minerva = ReplayUntil("war-minerva-mars.log", "P2 minerva 2");
        Assert.Equal((1, 2), (minerva.ConflictPawn, minerva.Pantheon!.Minerva));
        var mars = ReplayUntil("war-minerva-mars.log", "P1 activate mars");
        Assert.Equal((1, PantheonState.NoMinerva), (mars.ConflictPawn, mars.Pantheon!.Minerva));
        Assert.Equal((20, 20), (mars.Coins(0), mars.Coins(1)));
        // Age I: P1 builds the Palisade ($2, pawn 1 toward P2) and discards 9: 7 - 2 + 18 = 23. P2 discards 10: 27.
        // Age II: P2 Minerva on space 2 (P2 pays 7): 20, her pawn 2 spaces toward P2. P1 Mars on space 1 (P1 pays 3):
        // 20; Mars's 2 shields would take the pawn into Minerva's space, so it stays on 1 and Minerva leaves.
        // P1 builds the Archery Range (S W P = 6 coins), pawn 3: P2 loses the 2-coin token; the Parade Ground
        // (C C G = 6), pawn 5; the Horse Breeders (C W = 4, no Stable to chain from), pawn 6: P2 loses the 5-coin token.
        // P1: 20 + 7 discards x 2 - 6 - 6 - 4 = 18. P2: 20 + 10 discards x 2 - 2 - 5 = 33.
        // Age III: P1 chains the Fortifications from the Palisade (pawn 8) and, after 4 discards (26 coins), the Siege
        // Workshop from the Archery Range: pawn 9, P2's capital. P2 discarded 6: 45 coins.
        Assert.Equal((26, 45), (s.Coins(0), s.Coins(1)));
        Assert.Equal(GameState.CapitalDistance, s.ConflictPawn);
        var r = s.Result!;
        Assert.Equal((0, VictoryType.Military), (r.Winner, r.Victory));
        // The sheets still count: P1 coins 26 / 3 = 8, military 10; P2 coins 45 / 3 = 15.
        AssertSheet(r.P1, (ScoreCategory.Coins, 8), (ScoreCategory.Military, 10));
        AssertSheet(r.P2, (ScoreCategory.Coins, 15));
    }

    [Fact]
    public void Gate_hades_enki_zeus()
    {
        var s = Replay("gate-hades-enki-zeus.log");
        // Age I: 10 discards each: 27 coins each. The Gate goes on the empty space 6.
        // Age II: P1 the Gate (space 6: 8, doubled: 16): 11, and takes Aphrodite (9 VP). P2 Hades (space 2: 7): 20,
        // builds the Theater from the discard pile (3 VP). P1 Enki (space 4: 6): 5, keeps Philosophy (7 VP). P2 Zeus
        // (space 3: 6): 14, the Brewery on 0.0 goes to the discard pile. Then P1 discards 10 (25), P2 9 (32).
        // Age III: P2 builds the Greek Grand Temple free (greek token) and discards 9: 50. P1 discards 10: 45.
        Assert.Equal((45, 50), (s.Coins(0), s.Coins(1)));
        Assert.True(s.HasToken(0, GameData.TokenIndex("philosophy")));
        Assert.False(s.HasToken(0, GameData.TokenIndex("urbanism")) || s.HasToken(1, GameData.TokenIndex("urbanism")));
        var r = s.Result!;
        // P1: coins 45 / 3 = 15; Aphrodite 9; Philosophy 7. 31 VP.
        AssertSheet(r.P1, (ScoreCategory.Coins, 15), (ScoreCategory.Divinities, 9), (ScoreCategory.Progress, 7));
        // P2: coins 50 / 3 = 16; Theater 3; 1 Temple 5. 24 VP.
        AssertSheet(r.P2, (ScoreCategory.Coins, 16), (ScoreCategory.Blue, 3), (ScoreCategory.Temples, 5));
        Assert.Equal((0, VictoryType.Civilian), (r.Winner, r.Victory));
    }

    [Fact]
    public void Offerings_astarte_neptune()
    {
        var s = Replay("offerings-astarte-neptune.log");
        // Age I: 27 coins each. Age II: P1 discards twice (31) and takes offering-4; Astarte on space 5 costs P1 7:
        // the 4 token and 3 coins (28), 7 coins on Astarte. P1 takes offering-3, P2 offering-2. P2 Neptune on space 1
        // costs P2 8: the 2 token and 6 coins; it discards the P1:2 token and applies P1:5, so P1 loses 5 coins from
        // the treasury, not from Astarte (R7). Age II ends with P1 39 (10 discards), P2 41 (10 discards).
        // Age III: 10 discards each: P1 59, P2 61.
        Assert.Equal((59, 61), (s.Coins(0), s.Coins(1)));
        Assert.Equal(7, s.Pantheon!.AstarteCoins(0));
        Assert.Equal(1 << PantheonData.OfferingIndex("offering-3"), s.Pantheon.OfferingTokens(0)); // offering-3 is still held: no VP without Mysticism
        Assert.Equal(GameState.MilitaryTokenCount - 2, s.MilitaryTokensLeft);
        var r = s.Result!;
        // P1: coins 59 / 3 = 19; Astarte's 7 coins = 7 VP. 26 VP. P2: coins 61 / 3 = 20.
        AssertSheet(r.P1, (ScoreCategory.Coins, 19), (ScoreCategory.Divinities, 7));
        AssertSheet(r.P2, (ScoreCategory.Coins, 20));
        Assert.Equal((0, VictoryType.Civilian), (r.Winner, r.Victory));
    }

    [Fact]
    public void Every_scenario_prints_and_replays_to_the_same_text()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "PantheonScenarios"), "*.log"))
        {
            var game = MoveLog.Parse(File.ReadAllText(file));
            Assert.Contains("Pantheon:", StatePrinter.Describe(game.State));
            Assert.Equal(game.WriteLog(), MoveLog.Parse(game.WriteLog()).WriteLog());
        }
    }

    [Fact]
    public void The_state_printer_describes_every_state_of_random_pantheon_games()
    {
        // `7wd replay` prints the final state; the printer must handle every Pantheon state on the way (a one-card
        // draw, the Gate and the Theater waiting for their Reveals, Zeus on a face-down card, Minerva, the Snake).
        var seen = new HashSet<string>();
        for (ulong seed = 0; seed < 150; seed++)
        {
            var game = new Game(new Ruleset((StartChoice)(seed % 2), (Promos)(seed / 2 % 4), Expansions.Pantheon));
            var (chance, rng, s) = (new RandomChanceSource(seed), new Rng(seed + 77), game.State);
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
                else
                {
                    var moves = s.LegalMoves();
                    game.Apply(moves[rng.Next(moves.Count)]);
                }
                foreach (var line in StatePrinter.Describe(s).Split('\n'))
                    if (line.IndexOf(':') is var colon and > 0) seen.Add(line[..colon]);
            }
            var replayed = MoveLog.Parse(game.WriteLog());
            Assert.Equal(StatePrinter.Describe(s), StatePrinter.Describe(replayed.State));
        }
        foreach (var expected in new[] { "Pantheon", "Mythology decks (top first)", "P1 holds", "P2 activated", "Drawn",
                     "Tokens on the structure", "Enki's tokens", "Minerva", "Nisaba's Snake", "The Gate", "The Divine Theater", "Zeus" })
            Assert.Contains(expected, seen);
    }
}
