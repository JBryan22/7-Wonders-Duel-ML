using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>Random-play invariants for Age I (spec A11.3). Plain seeded loops: a failure names its seed.</summary>
public class RandomPlayTests
{
    [Fact]
    public void Random_age_one_games_always_end_with_all_20_cards_taken()
    {
        for (ulong seed = 0; seed < 1000; seed++)
        {
            var game = GameRunner.PlayRandom(seed);
            var s = game.State;
            Assert.True(s.IsOver, $"seed {seed}");
            Assert.Equal(0, s.CardsLeftInAge);
            Assert.Equal(20, s.City(0).Count + s.City(1).Count + s.DiscardPile.Count);
            Assert.Equal(20, game.Log.OfType<MoveEntry>().Count());
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
    public void Coins_never_go_negative_over_10k_random_age_one_games()
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
