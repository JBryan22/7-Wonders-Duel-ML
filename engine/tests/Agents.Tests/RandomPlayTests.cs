using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>Random-play invariants over full three-Age games (spec A11.3). Plain seeded loops: a failure names its seed.</summary>
public class RandomPlayTests
{
    [Fact]
    public void Random_full_games_always_end_with_a_result_after_all_60_cards_or_a_military_victory()
    {
        var military = 0;
        var science = 0;
        foreach (var rules in new[] { Ruleset.Default, new Ruleset(StartChoice.Rule2015) })
            for (ulong seed = 0; seed < 1000; seed++)
            {
                var game = GameRunner.PlayRandom(seed, rules);
                var s = game.State;
                Assert.True(s.IsOver, $"seed {seed}");
                Assert.NotNull(s.Result);
                var taken = game.Log.OfType<MoveEntry>().Count(m => m.Move.TakesCard);
                var choices = game.Log.OfType<MoveEntry>().Count(m => m.Move.Kind is MoveKind.StartMe or MoveKind.StartOpponent);
                Assert.Equal(taken, s.City(0).Count + s.City(1).Count + s.DiscardPile.Count);
                if (s.Result.Victory == VictoryType.Military)
                {
                    // The pawn stands on the loser's capital and the game stopped where it was.
                    military++;
                    Assert.Equal(GameState.CapitalDistance, Math.Abs(s.ConflictPawn));
                    Assert.Equal(s.ConflictPawn > 0 ? 0 : 1, s.Result.Winner);
                    Assert.Equal(60 - 20 * (3 - s.Age) - s.CardsLeftInAge, taken);
                    Assert.True(choices <= s.Age - 1, $"seed {seed}");
                    continue;
                }
                if (s.Result.Victory == VictoryType.Science)
                {
                    // The winner shows 6 different symbols; the loser could not have reached 6 first.
                    science++;
                    Assert.True(s.DistinctSymbols(s.Result.Winner) >= GameState.ScienceSupremacySymbols, $"seed {seed}");
                    Assert.True(s.DistinctSymbols(1 - s.Result.Winner) < GameState.ScienceSupremacySymbols, $"seed {seed}");
                    Assert.Equal(0, s.PendingCount);
                    continue;
                }
                Assert.Equal(VictoryType.Civilian, s.Result.Victory);
                Assert.All(new[] { 0, 1 }, p => Assert.True(s.DistinctSymbols(p) < GameState.ScienceSupremacySymbols));
                Assert.True(Math.Abs(s.ConflictPawn) < GameState.CapitalDistance);
                Assert.Equal(3, s.Age);
                Assert.Equal(0, s.CardsLeftInAge);
                Assert.Equal(60, taken);
                // Under the newer rule every Age start is a choice (weaker military, or the last taker on a tie);
                // under 2015 only an untied military gives one.
                if (rules.StartChoice == StartChoice.New) Assert.Equal(2, choices);
                else Assert.InRange(choices, 0, 2);
                Assert.Single(game.Log.OfType<GuildPatternReveal>());
                Assert.Equal(3, ((s.City(0) | s.City(1) | s.DiscardPile) & CardSets.Guilds).Count);
                // The winner has more VP, or equal VP and more blue VP; otherwise it is a draw.
                var (a, b) = (s.Result.P1, s.Result.P2);
                var expected = a.Total != b.Total ? (a.Total > b.Total ? 0 : 1)
                    : a[ScoreCategory.Blue] != b[ScoreCategory.Blue] ? (a[ScoreCategory.Blue] > b[ScoreCategory.Blue] ? 0 : 1) : -1;
                Assert.Equal(expected, s.Result.Winner);
            }
        Assert.True(military > 0, "no random game ended in a military victory");
        Assert.True(science > 0, "no random game ended in a science victory");
    }

    [Fact]
    public void Progress_tokens_are_conserved_and_each_pick_follows_a_science_pair_over_2k_random_games()
    {
        for (ulong seed = 0; seed < 2000; seed++)
        {
            var game = GameRunner.PlayRandom(seed);
            var s = game.State;
            // 5 tokens set out; every one is on the board or owned by exactly one player.
            Assert.Equal(ProgressTokens.OnBoard, System.Numerics.BitOperations.PopCount(s.TokensSetOut));
            Assert.Equal(0, s.Tokens(0) & s.Tokens(1));
            Assert.Equal(s.TokensSetOut, (ushort)(s.BoardTokens | s.Tokens(0) | s.Tokens(1)));
            // Every pick was a pair: no more picks than pairs, and fewer only when the board ran out.
            var picks = game.Log.OfType<MoveEntry>().Count(m => m.Move.Kind == MoveKind.Token);
            Assert.Equal(s.TokenCount(0) + s.TokenCount(1), picks);
            var pairs = Enumerable.Range(0, 2).Sum(p =>
                Enum.GetValues<SevenWondersDuel.Engine.Data.ScienceSymbol>().Count(sym => s.SymbolCount(p, sym) >= 2));
            if (s.BoardTokens != 0) Assert.Equal(pairs, picks);
            else Assert.True(picks <= pairs, $"seed {seed}");
            Assert.Equal(Scoring.ProgressPoints(s, 0), s.Result!.P1[ScoreCategory.Progress]);
        }
    }

    [Fact]
    public void The_pawn_stays_on_the_track_and_looted_tokens_never_come_back_over_2k_random_games()
    {
        for (ulong seed = 0; seed < 2000; seed++)
        {
            var game = GameRunner.PlayRandom(seed);
            var replay = GameState.New(game.Rules);
            var tokens = replay.MilitaryTokensLeft;
            foreach (var entry in game.Log)
            {
                if (entry is MoveEntry m) replay.Apply(m.Move); else replay.Apply((Reveal)entry);
                Assert.InRange(replay.ConflictPawn, -GameState.CapitalDistance, GameState.CapitalDistance);
                Assert.True(replay.MilitaryTokensLeft <= tokens, $"seed {seed}");
                tokens = replay.MilitaryTokensLeft;
                // A token is gone exactly when the pawn has at some point entered its zone; at least, the current lead implies it.
                for (var t = 0; t < GameState.MilitaryTokenCount; t++)
                    if (replay.MilitaryLead(1 - t / 2) >= GameState.MilitaryTokenZone(t))
                        Assert.False(replay.MilitaryTokenLeft(t), $"seed {seed}");
            }
        }
    }

    [Fact]
    public void Replaying_a_written_move_log_reproduces_an_identical_game_state()
    {
        for (ulong seed = 0; seed < 500; seed++)
        {
            var game = GameRunner.PlayRandom(seed);
            var text = game.WriteLog();
            var replayed = MoveLog.Parse(text);
            Assert.True(replayed.State.SameAs(game.State), $"seed {seed}");
            Assert.Equal(text, replayed.WriteLog());
        }
    }

    [Fact]
    public void Replaying_any_prefix_of_a_log_reproduces_the_state_at_that_point()
    {
        var game = GameRunner.PlayRandom(3);
        var replay = new Game(game.Rules);
        foreach (var entry in game.Log)
        {
            replay.Apply(entry);
            var lines = replay.WriteLog();
            Assert.True(MoveLog.Parse(lines).State.SameAs(replay.State));
        }
    }

    [Fact]
    public void Coins_never_go_negative_over_10k_random_games()
    {
        for (ulong seed = 0; seed < 10_000; seed++)
        {
            var seeds = new Rng(seed);
            var chance = new RandomChanceSource(seeds.NextUInt64());
            IAgent[] agents = [new RandomAgent(seeds.NextUInt64()), new RandomAgent(seeds.NextUInt64())];
            var s = GameState.New(Ruleset.Default);
            while (s.Step != StepKind.GameOver)
            {
                if (s.Step == StepKind.Reveal) s.Apply(chance.Next(s));
                else s.Apply(agents[s.PlayerToDecide].ChooseMove(s));
                if (s.Coins(0) < 0 || s.Coins(1) < 0) Assert.Fail($"negative coins, seed {seed}");
            }
        }
    }

    [Fact]
    public void The_same_seed_plays_the_same_game()
    {
        Assert.Equal(GameRunner.PlayRandom(77).WriteLog(), GameRunner.PlayRandom(77).WriteLog());
        Assert.NotEqual(GameRunner.PlayRandom(77).WriteLog(), GameRunner.PlayRandom(78).WriteLog());
    }

    [Fact]
    public void A_copied_state_never_affects_the_original()
    {
        var game = GameRunner.PlayRandom(11);
        var replay = new Game(game.Rules);
        var chance = new RandomChanceSource(99);
        var agent = new RandomAgent(99);
        foreach (var entry in game.Log)
        {
            var before = replay.State.Clone();
            Assert.True(before.SameAs(replay.State));

            // Play the copy to the end; the original must not move.
            var copy = replay.State.Clone();
            while (copy.Step != StepKind.GameOver)
                if (copy.Step == StepKind.Reveal) copy.Apply(chance.Next(copy));
                else copy.Apply(agent.ChooseMove(copy));
            Assert.True(before.SameAs(replay.State));

            replay.Apply(entry);
        }
    }

    [Fact]
    public void A_legal_move_exists_whenever_a_decision_is_pending()
    {
        for (ulong seed = 0; seed < 300; seed++)
        {
            var seeds = new Rng(seed);
            var chance = new RandomChanceSource(seeds.NextUInt64());
            var agent = new RandomAgent(seeds.NextUInt64());
            var s = GameState.New(Ruleset.Default);
            while (s.Step != StepKind.GameOver)
            {
                if (s.Step == StepKind.Reveal) { s.Apply(chance.Next(s)); continue; }
                Assert.NotEmpty(s.LegalMoves());
                Assert.All(s.LegalMoves(), m => Assert.True(s.IsLegal(m)));
                s.Apply(agent.ChooseMove(s));
            }
        }
    }
}
