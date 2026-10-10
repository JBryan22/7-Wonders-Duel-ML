namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// The soak checker and the bench, at CI size. The full 100k-game soak (<c>7wd soak</c>) and the timed bench
/// (<c>7wd bench</c>) are run by hand (spec A11: long soaks stay out of CI).
/// </summary>
public class SoakAndBenchTests
{
    [Fact]
    public void Every_invariant_holds_over_3k_soaked_games_under_both_start_rules()
    {
        var report = Soak.Run(3000);
        Assert.True(report.Passed, string.Join('\n', report.Failures.Take(10)));
        Assert.Equal(3000, report.Games);
        Assert.Equal(report.Games, report.Civilian + report.Military + report.Science);
        Assert.True(report.Military > 0 && report.Science > 0, report.ToString());
    }

    [Fact]
    public void Every_invariant_holds_over_3k_soaked_games_with_the_messe()
    {
        var report = Soak.Run(3000, firstSeed: 1_000_000, promos: Engine.Promos.Messe);
        Assert.True(report.Passed, string.Join('\n', report.Failures.Take(10)));
        Assert.Equal(3000, report.Civilian + report.Military + report.Science);
        Assert.Equal("rules base start-choice=2015 promo=messe", Soak.RulesFor(1, Engine.Promos.Messe).ToString());
    }

    [Theory]
    [InlineData(Engine.Promos.Liberty, "rules base start-choice=2015 promo=liberty")]
    [InlineData(Engine.Promos.Messe | Engine.Promos.Liberty, "rules base start-choice=2015 promo=messe,liberty")]
    public void Every_invariant_holds_over_3k_soaked_games_with_the_statue_of_liberty(Engine.Promos promos, string rules)
    {
        var report = Soak.Run(3000, firstSeed: 2_000_000, promos: promos);
        Assert.True(report.Passed, string.Join('\n', report.Failures.Take(10)));
        Assert.Equal(3000, report.Civilian + report.Military + report.Science);
        Assert.Equal(rules, Soak.RulesFor(1, promos).ToString());
    }

    [Fact]
    public void The_statue_of_liberty_is_drafted_built_and_resolved_in_random_promo_games()
    {
        var liberty = Engine.Data.GameData.WonderIndex("statue-of-liberty");
        int drafted = 0, built = 0, gives = 0, opponentPicks = 0;
        for (ulong seed = 0; seed < 1000; seed++)
        {
            var game = GameRunner.PlayRandom(seed, new Engine.Ruleset(Promos: Engine.Promos.Liberty));
            var s = game.State;
            if (((s.Wonders(0) | s.Wonders(1)) & 1 << liberty) != 0) drafted++;
            if (s.IsWonderBuilt(liberty)) built++;
            for (var i = 0; i < game.Log.Count; i++)
            {
                if (game.Log[i] is not Engine.MoveEntry { Move.Kind: Engine.MoveKind.LibertyGive } give) continue;
                gives++;
                // Wonder, (flips,) returned cards, keep, give: all the Statue's owner's.
                Assert.IsType<Engine.ReturnedReveal>(game.Log[i - 2]);
                var keep = Assert.IsType<Engine.MoveEntry>(game.Log[i - 1]);
                Assert.Equal((give.Player, Engine.MoveKind.LibertyKeep), (keep.Player, keep.Move.Kind));
                var wonder = game.Log.Take(i).OfType<Engine.MoveEntry>().Last(m => m.Move.Kind == Engine.MoveKind.Wonder);
                Assert.Equal((give.Player, liberty), (wonder.Player, wonder.Move.Wonder));
                // A token picked by the opponent during the owner's turn.
                opponentPicks += game.Log.Skip(i + 1).TakeWhile(e => e is Engine.MoveEntry { Move.Kind: Engine.MoveKind.Token })
                    .Count(e => ((Engine.MoveEntry)e).Player != give.Player);
            }
        }
        Assert.InRange(drafted, 500, 750); // 8 of 13 Wonders are drafted: ~615
        Assert.True(built > 0 && gives == built, $"built {built}, gives {gives}");
        Assert.True(opponentPicks > 0, "no opponent token pick from a given card in 1000 games");
    }

    [Fact]
    public void The_messe_is_drafted_built_and_resolved_in_random_promo_games()
    {
        var messe = Engine.Data.GameData.WonderIndex("messe");
        int drafted = 0, built = 0, choices = 0;
        for (ulong seed = 0; seed < 1000; seed++)
        {
            var game = GameRunner.PlayRandom(seed, new Engine.Ruleset(Promos: Engine.Promos.Messe));
            var s = game.State;
            if (((s.Wonders(0) | s.Wonders(1)) & 1 << messe) != 0) drafted++;
            if (s.IsWonderBuilt(messe)) built++;
            for (var i = 0; i < game.Log.Count; i++)
            {
                if (game.Log[i] is not Engine.MoveEntry { Move.Kind: Engine.MoveKind.Messe } m) continue;
                choices++;
                // The choice follows the Messe's own build by the same player (after any flips it caused).
                var wonder = game.Log.Take(i).OfType<Engine.MoveEntry>().Last();
                Assert.Equal((m.Player, Engine.MoveKind.Wonder, messe), (wonder.Player, wonder.Move.Kind, wonder.Move.Wonder));
            }
        }
        Assert.InRange(drafted, 500, 750); // 8 of 13 Wonders are drafted: ~615
        Assert.True(built > 0 && choices > 0 && choices <= built, $"built {built}, choices {choices}");
    }

    [Fact]
    public void A_soaked_game_is_the_same_game_on_any_thread_count()
    {
        var one = Soak.Run(200, firstSeed: 500, threads: 1);
        var many = Soak.Run(200, firstSeed: 500, threads: 4);
        Assert.Equal((one.Civilian, one.Military, one.Science, one.Draws), (many.Civilian, many.Military, many.Science, many.Draws));
    }

    [Fact]
    public void Soak_seeds_alternate_between_the_start_rules()
    {
        Assert.Equal(Engine.StartChoice.New, Soak.RulesFor(0).StartChoice);
        Assert.Equal(Engine.StartChoice.Rule2015, Soak.RulesFor(1).StartChoice);
    }

    [Fact]
    public void Bench_measures_every_variant()
    {
        var results = Bench.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.Games > 0 && r.Decisions > r.Games, r.ToString());
            Assert.True(r.GamesPerSecond > 0);
        });
    }
}
