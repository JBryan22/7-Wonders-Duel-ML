using System.Diagnostics;
using System.Numerics;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Agents;

/// <summary>What a <see cref="Soak"/> run found: games played, how they ended, and every failure with its seed.</summary>
public sealed record SoakReport(int Games, int Civilian, int Military, int Science, int Draws, IReadOnlyList<string> Failures, double Seconds)
{
    public bool Passed => Failures.Count == 0;

    public override string ToString() =>
        $"{Games:N0} games in {Seconds:N1} s: {Civilian:N0} civilian ({Draws:N0} draws), {Military:N0} military, {Science:N0} science; "
        + (Passed ? "all invariants hold" : $"{Failures.Count} FAILED");
}

/// <summary>
/// The random-play invariants (spec A11.3), checked after every log entry of seeded random games: coins never
/// negative; every game ends; a legal Move exists, and each one is legal, whenever a decision is pending; replaying the
/// written Move Log gives an identical Game State and the same text; a copied state played to the end never affects the
/// original; the Observation never holds the opponent's private Reveal (and otherwise equals the state). Plus the
/// bookkeeping the rules imply: the pawn stays on the track, looted military tokens never return, Progress tokens are
/// owned at most once, cached production matches the city, and every card taken is in a city, the discard pile or
/// under a Wonder. Seeds alternate between the two start-choice rules. A failure names its seed and rules.
/// </summary>
public static class Soak
{
    /// <summary>No game comes near this many log entries (about 100 is typical); reaching it means the game does not end.</summary>
    public const int MaxLogEntries = 1000;

    public static Ruleset RulesFor(ulong seed) => seed % 2 == 0 ? Ruleset.Default : new Ruleset(StartChoice.Rule2015);

    /// <summary>Soaks <paramref name="games"/> games from <paramref name="firstSeed"/> on, using every core.</summary>
    public static SoakReport Run(int games, ulong firstSeed = 0, int? threads = null)
    {
        var clock = Stopwatch.StartNew();
        var outcomes = new (VictoryType? Victory, bool Draw, string? Failure)[games];
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads ?? Environment.ProcessorCount };
        Parallel.For(0, games, options, i => outcomes[i] = CheckGame(firstSeed + (ulong)i));
        var failures = outcomes.Where(o => o.Failure is not null).Select(o => o.Failure!).ToList();
        int Count(VictoryType v) => outcomes.Count(o => o.Victory == v);
        return new SoakReport(games, Count(VictoryType.Civilian), Count(VictoryType.Military), Count(VictoryType.Science),
            outcomes.Count(o => o.Draw), failures, clock.Elapsed.TotalSeconds);
    }

    /// <summary>Plays one seeded random game, checking every invariant; returns how it ended, or the first failure.</summary>
    public static (VictoryType? Victory, bool Draw, string? Failure) CheckGame(ulong seed)
    {
        var rules = RulesFor(seed);
        try
        {
            var result = Play(seed, rules);
            return (result.Victory, result.IsDraw, null);
        }
        catch (Exception e)
        {
            return (null, false, $"seed {seed} ({rules}): {e.Message}");
        }
    }

    static Result Play(ulong seed, Ruleset rules)
    {
        // The same seed derivation as GameRunner.PlayRandom, plus one stream for the copy check.
        var seeds = new Rng(seed);
        var chance = new RandomChanceSource(seeds.NextUInt64());
        IAgent[] agents = [new RandomAgent(seeds.NextUInt64()), new RandomAgent(seeds.NextUInt64())];
        var extra = new Rng(seeds.NextUInt64());
        var copyAt = extra.Next(100);

        var game = new Game(rules);
        var s = game.State;
        Span<Move> moves = stackalloc Move[GameState.MaxMoves];
        Span<Move> observed = stackalloc Move[GameState.MaxMoves];
        var militaryTokens = s.MilitaryTokensLeft;
        while (s.Step != StepKind.GameOver)
        {
            Check(game.Log.Count < MaxLogEntries, $"the game did not end within {MaxLogEntries} log entries");
            if (game.Log.Count == copyAt) CheckCopyIsIndependent(s, extra.NextUInt64());

            if (s.Step == StepKind.Reveal)
            {
                game.Apply(chance.Next(s));
            }
            else
            {
                var player = s.PlayerToDecide;
                var n = s.LegalMoves(moves);
                Check(n > 0, "a decision is pending but there is no legal Move");
                for (var i = 0; i < n; i++)
                    Check(s.WhyIllegal(moves[i]) is null, $"generated Move '{moves[i]}' is illegal: {s.WhyIllegal(moves[i])}");
                for (var p = 0; p < 2; p++)
                {
                    var o = s.Observe(p);
                    if (s.LibraryDraw != 0 && p != player)
                        Check(o.State.LibraryDraw == 0 && o.State.LibraryDrawHidden, "an Observation holds the opponent's Great Library draw");
                    else
                        Check(o.State.SameAs(s), "an Observation differs from the state with nothing private to hide");
                    if (p == player)
                        Check(o.LegalMoves(observed) == n && observed[..n].SequenceEqual(moves[..n]), "the Observation's legal Moves differ");
                }
                game.Apply(agents[player].ChooseMove(s.Observe(player)));
            }

            for (var p = 0; p < 2; p++)
            {
                Check(s.Coins(p) >= 0, $"P{p + 1} has {s.Coins(p)} coins");
                foreach (var r in Enum.GetValues<Resource>())
                {
                    var produced = 0;
                    foreach (var c in s.City(p).Cards()) produced += GameData.Cards[c].Produce[(int)r];
                    Check(s.Production(p, r) == produced, $"P{p + 1}'s cached {r} production is {s.Production(p, r)}, the city makes {produced}");
                }
            }
            Check(Math.Abs(s.ConflictPawn) <= GameState.CapitalDistance, $"the pawn is off the track at {s.ConflictPawn}");
            Check(s.MilitaryTokensLeft <= militaryTokens, "a looted military token came back");
            militaryTokens = s.MilitaryTokensLeft;
            Check((s.Tokens(0) & s.Tokens(1)) == 0, "both players own the same Progress token");
            Check((s.BoardTokens & (s.Tokens(0) | s.Tokens(1))) == 0, "a Progress token is both owned and on the board");
            Check(s.TotalWondersBuilt <= GameState.MaxWondersBuilt, "more than 7 Wonders built");
        }

        Check(s.Result is not null, "the game is over without a Result");
        var taken = game.Log.Count(e => e is MoveEntry { Move.TakesCard: true });
        Check(taken == s.City(0).Count + s.City(1).Count + s.DiscardPile.Count + s.TotalWondersBuilt,
            "a taken card is not in a city, the discard pile or under a Wonder");
        Check(BitOperations.PopCount((uint)(s.Wonders(0) | s.Wonders(1))) == GameState.DraftSize, "8 Wonders were not drafted");

        // Replaying the written Move Log gives the identical state, and writes the identical text.
        var text = game.WriteLog();
        var replayed = MoveLog.Parse(text);
        Check(replayed.State.SameAs(s), "replaying the Move Log gives a different Game State");
        Check(replayed.WriteLog() == text, "the replayed Move Log writes different text");
        return s.Result!;
    }

    /// <summary>A copy played to the end with its own Reveals and Moves leaves the original byte-identical.</summary>
    static void CheckCopyIsIndependent(GameState s, ulong seed)
    {
        var before = s.ToBytes();
        var copy = s.Clone();
        Check(copy.SameAs(s), "a copy differs from its original");
        var rng = new Rng(seed);
        var chance = new RandomChanceSource(rng.NextUInt64());
        var agent = new RandomAgent(rng.NextUInt64());
        for (var i = 0; copy.Step != StepKind.GameOver && i < MaxLogEntries; i++)
            if (copy.Step == StepKind.Reveal) copy.Apply(chance.Next(copy));
            else copy.Apply(agent.ChooseMove(copy.Observe(copy.PlayerToDecide)));
        Check(s.ToBytes().AsSpan().SequenceEqual(before), "playing a copy changed the original");
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new SoakFailure(message);
    }

    sealed class SoakFailure(string message) : Exception(message);
}
