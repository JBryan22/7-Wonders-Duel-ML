using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// The uniform Determinizer (spec A4): an Observation in, one sampled Game State consistent with it out, then every
/// Reveal of that world drawn uniformly from what the observer has not seen.
/// </summary>
public class DeterminizerTests
{
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int T(string slug) => GameData.TokenIndex(slug);

    /// <summary>P1 has just built the Great Library and drawn <paramref name="draw"/> (3 of the box's tokens).</summary>
    static GameState LibraryDrawn(string draw)
    {
        var game = MoveLog.Parse(string.Join('\n',
        [
            "7wd-log v1", "rules base", "reveal first P1", "reveal tokens agriculture economy law strategy urbanism",
            .. Setup.DraftFor("great-library"), Opening,
        ]));
        game.State.SetCoins(0, 50);
        game.Apply(MoveLog.ParseEntry("P1 wonder quarry great-library", game.State));
        game.Apply(MoveLog.ParseEntry($"reveal P1 library {draw}", game.State));
        return game.State;
    }

    [Fact]
    public void A_public_position_samples_to_itself()
    {
        var rng = new Rng(5);
        for (var game = 0; game < 20; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(new Ruleset(Promos: Promos.Messe | Promos.Liberty));
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                var observation = s.Observe(s.PlayerToDecide);
                var world = new Determinizer(rng.NextUInt64()).Sample(observation);
                Assert.True(world.SameAs(s));
                Assert.NotSame(observation.State, world);
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
    }

    [Fact]
    public void The_opponents_hidden_great_library_draw_is_filled_uniformly_from_the_box()
    {
        var a = LibraryDrawn("mathematics philosophy theology");
        var b = LibraryDrawn("architecture masonry theology");
        var theirsA = a.Observe(1);
        var theirsB = b.Observe(1);
        Assert.True(theirsA.SameAs(theirsB));
        var box = a.TokensSetAside;
        Assert.Equal(5, System.Numerics.BitOperations.PopCount((uint)box));

        var counts = new Dictionary<ushort, int>();
        var target = GameState.New(Ruleset.Default);
        for (ulong seed = 0; seed < 2000; seed++)
        {
            var before = theirsA.State.Clone();
            var fromA = new Determinizer(seed).Sample(theirsA);
            new Determinizer(seed).Sample(theirsB, target);
            Assert.True(theirsA.State.SameAs(before), "sampling changed the Observation");
            Assert.True(fromA.SameAs(target), "the same seed must give the same world from the same Observation");

            Assert.False(fromA.LibraryDrawHidden);
            var draw = fromA.LibraryDraw;
            Assert.Equal(GameState.LibraryDrawSize, System.Numerics.BitOperations.PopCount((uint)draw));
            Assert.Equal(0, draw & ~box);
            // Apart from the draw, the world is the Observation.
            Assert.Equal(ProgressTokens.In(draw).Select(Move.Library), fromA.LegalMoves());
            counts[draw] = counts.GetValueOrDefault(draw) + 1;
        }
        // All C(5,3) = 10 draws, each about 200 times.
        Assert.Equal(10, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, 140, 260));
        // The true draw is not favoured.
        Assert.InRange(counts[(ushort)(1 << T("mathematics") | 1 << T("philosophy") | 1 << T("theology"))], 140, 260);
    }

    [Fact]
    public void A_redacted_draw_in_the_true_state_is_filled_too()
    {
        // The Advisor's own log: the opponent drew privately in Tabletop Simulator.
        var game = MoveLog.Parse(string.Join('\n',
        [
            "7wd-log v1", "rules base", "reveal first P1", "reveal tokens agriculture economy law strategy urbanism",
            .. Setup.DraftFor("great-library"), Opening,
        ]));
        game.State.SetCoins(0, 50);
        game.Apply(MoveLog.ParseEntry("P1 wonder quarry great-library", game.State));
        game.Apply(LibraryReveal.Hidden(0));
        Assert.True(game.State.LibraryDrawHidden);
        var world = new Determinizer(1).Sample(game.State.Observe(1));
        Assert.Equal(3, world.LegalMoves().Count);
    }

    /// <summary>
    /// From Observations all through random games (both promos), sampled worlds play out to the end with the
    /// Determinizer as their only Chance Source: every Reveal it draws is legal (Guild backs get Guilds, returned cards
    /// are unseen cards of the Age, draft offers are unseen Wonders), and a reseed replays the same world exactly.
    /// </summary>
    [Fact]
    public void Sampled_worlds_play_out_with_legal_reveals_and_replay_from_a_seed()
    {
        var rng = new Rng(11);
        var playouts = 0;
        for (var game = 0; game < 30; game++)
        {
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(new Ruleset(Promos: Promos.Messe | Promos.Liberty));
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                if (rng.Next(8) == 0)
                {
                    var observation = s.Observe(s.PlayerToDecide);
                    var seed = rng.NextUInt64();
                    Assert.Equal(PlayOut(observation, seed), PlayOut(observation, seed));
                    playouts++;
                }
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
        Assert.True(playouts > 100, $"only {playouts} playouts");
    }

    static string PlayOut(Observation observation, ulong seed)
    {
        var determinizer = new Determinizer(seed);
        var world = determinizer.Sample(observation);
        var policy = new Rng(seed + 1);
        while (!world.IsOver)
        {
            if (world.Step == StepKind.Reveal)
            {
                world.Apply(determinizer.Next(world));
                continue;
            }
            var moves = world.LegalMoves();
            world.Apply(moves[policy.Next(moves.Count)]);
        }
        return Convert.ToHexString(world.ToBytes());
    }
}
