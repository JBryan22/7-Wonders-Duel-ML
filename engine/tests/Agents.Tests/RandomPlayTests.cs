using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>Random-play invariants over full three-Age games (spec A11.3). Plain seeded loops: a failure names its seed.</summary>
public class RandomPlayTests
{
    [Fact]
    public void Random_full_games_always_end_with_a_result_after_all_60_cards_are_taken()
    {
        foreach (var rules in new[] { Ruleset.Default, new Ruleset(StartChoice.Rule2015) })
            for (ulong seed = 0; seed < 1000; seed++)
            {
                var game = GameRunner.PlayRandom(seed, rules);
                var s = game.State;
                Assert.True(s.IsOver, $"seed {seed}");
                Assert.NotNull(s.Result);
                Assert.Equal(VictoryType.Civilian, s.Result.Victory);
                Assert.Equal(3, s.Age);
                Assert.Equal(0, s.CardsLeftInAge);
                Assert.Equal(60, s.City(0).Count + s.City(1).Count + s.DiscardPile.Count);
                Assert.Equal(60, game.Log.OfType<MoveEntry>().Count(m => m.Move.TakesCard));
                // Under the newer rule a tied military always gives the last taker a choice; under 2015 never.
                var choices = game.Log.OfType<MoveEntry>().Count(m => !m.Move.TakesCard);
                Assert.Equal(rules.StartChoice == StartChoice.New ? 2 : 0, choices);
                Assert.Single(game.Log.OfType<GuildPatternReveal>());
                Assert.Equal(3, ((s.City(0) | s.City(1) | s.DiscardPile) & CardSets.Guilds).Count);
                // The winner has more VP, or equal VP and more blue VP; otherwise it is a draw.
                var (a, b) = (s.Result.P1, s.Result.P2);
                var expected = a.Total != b.Total ? (a.Total > b.Total ? 0 : 1)
                    : a[ScoreCategory.Blue] != b[ScoreCategory.Blue] ? (a[ScoreCategory.Blue] > b[ScoreCategory.Blue] ? 0 : 1) : -1;
                Assert.Equal(expected, s.Result.Winner);
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
