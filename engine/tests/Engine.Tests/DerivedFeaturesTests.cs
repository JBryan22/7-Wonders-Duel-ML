using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>The shared derived-features module (spec A4, Derived features).</summary>
public class DerivedFeaturesTests
{
    static GameState Position(string[] p1, string[] p2, int pawn = 0)
    {
        var s = GameState.New(Ruleset.Default);
        foreach (var c in p1) s.AddToCity(0, GameData.CardIndex(c));
        foreach (var c in p2) s.AddToCity(1, GameData.CardIndex(c));
        s.SetCoins(0, GameState.StartingCoins);
        s.SetCoins(1, GameState.StartingCoins);
        s.SetConflictPawn(pawn);
        return s;
    }

    [Fact]
    public void An_empty_board_reads_as_the_start_of_a_game()
    {
        var s = Position([], []);
        foreach (var p in new[] { 0, 1 })
        {
            foreach (var r in Enum.GetValues<Resource>())
            {
                Assert.Equal(0, DerivedFeatures.Production(s, p, r));
                Assert.Equal(2, DerivedFeatures.TradingPrice(s, p, r));
            }
            Assert.Equal(0, DerivedFeatures.ChoiceProducers(s, p));
            Assert.Equal(0, DerivedFeatures.ScienceSymbols(s, p));
            Assert.Equal(0, DerivedFeatures.SciencePairs(s, p));
            Assert.Equal(2, DerivedFeatures.Vp(s, p)); // 7 coins
            Assert.Equal(9, DerivedFeatures.DistanceToCapital(s, p));
            Assert.Equal(0, DerivedFeatures.OpenChains(s, p));
        }
    }

    [Fact]
    public void Production_raises_the_opponents_price_and_a_fixed_price_card_caps_it()
    {
        var s = Position(["lumber-yard", "logging-camp"], ["stone-reserve"]);
        Assert.Equal(2, DerivedFeatures.Production(s, 0, Resource.Wood));
        Assert.Equal(4, DerivedFeatures.TradingPrice(s, 1, Resource.Wood));
        Assert.Equal(2, DerivedFeatures.TradingPrice(s, 0, Resource.Wood));
        Assert.Equal(1, DerivedFeatures.TradingPrice(s, 1, Resource.Stone));
    }

    [Fact]
    public void Science_counts_distinct_symbols_and_pairs()
    {
        // Pharmacist and Dispensary both show the mortar; Apothecary shows the wheel.
        var s = Position(["pharmacist", "dispensary", "apothecary"], []);
        Assert.Equal(2, DerivedFeatures.ScienceSymbols(s, 0));
        Assert.Equal(1, DerivedFeatures.SciencePairs(s, 0));
        Assert.False(DerivedFeatures.IsNewSymbol(s, 0, ScienceSymbol.Mortar));
        Assert.True(DerivedFeatures.IsNewSymbol(s, 0, ScienceSymbol.Globe));
    }

    [Fact]
    public void The_capital_distance_follows_the_Conflict_Pawn()
    {
        var s = Position([], [], pawn: 4); // 4 spaces toward P2's capital
        Assert.Equal(13, DerivedFeatures.DistanceToCapital(s, 0));
        Assert.Equal(5, DerivedFeatures.DistanceToCapital(s, 1));
    }

    [Fact]
    public void Open_chains_count_targets_still_in_play()
    {
        // Lumber Yard does not chain; Stable chains into Horse Breeders; Scriptorium into Library.
        var s = Position(["stable", "scriptorium"], []);
        Assert.Equal(2, DerivedFeatures.OpenChains(s, 0));
        s.AddToCity(1, GameData.CardIndex("horse-breeders"));
        Assert.Equal(1, DerivedFeatures.OpenChains(s, 0));
        s.AddToDiscard(GameData.CardIndex("library"));
        Assert.Equal(0, DerivedFeatures.OpenChains(s, 0));
    }

    [Fact]
    public void The_vector_matches_its_names_and_the_single_features_over_random_games()
    {
        Assert.Equal(DerivedFeatures.Count, DerivedFeatures.Names.Length);
        Assert.Equal(DerivedFeatures.Count, DerivedFeatures.Names.Distinct().Count());
        Span<int> values = stackalloc int[DerivedFeatures.Count];
        var rng = new Rng(15);
        for (var game = 0; game < 50; game++)
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
                var me = s.PlayerToDecide;
                DerivedFeatures.Write(s, me, values);
                Assert.Equal(Scoring.Score(s, me).Total, values[Array.IndexOf(DerivedFeatures.Names, "me.vp")]);
                Assert.Equal(Scoring.Score(s, 1 - me).Total, values[Array.IndexOf(DerivedFeatures.Names, "opp.vp")]);
                Assert.Equal(s.DistinctSymbols(1 - me), values[Array.IndexOf(DerivedFeatures.Names, "opp.science-symbols")]);
                Assert.Equal(Payment.TradingPrice(s, me, Resource.Glass), values[Array.IndexOf(DerivedFeatures.Names, "me.trading-price.glass")]);
                Assert.Equal(2 * GameState.CapitalDistance, values[^1] + values[^2]);
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
            Assert.Equal(s.Result!.P1.Total, DerivedFeatures.Vp(s, 0));
        }
    }
}
