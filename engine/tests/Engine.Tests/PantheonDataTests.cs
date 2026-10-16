using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Pantheon ticket 01: the data in data/pantheon/ (spec-pantheon P-A3, R1, R5, R6; expansions.md Part A §3) and the
/// expansion seam (P-A4 "Engine structure"): <c>expansion=pantheon</c> creates a game with a Pantheon state block,
/// a base game has none, Agora is still refused.
/// </summary>
public class PantheonDataTests
{
    static DivinityDef D(string id) => PantheonData.Divinities[PantheonData.DivinityIndex(id)];

    // ---------------------------------------------------------------- Divinities and Mythologies

    [Fact]
    public void There_are_15_divinities_in_5_mythologies_plus_the_gate()
    {
        Assert.Equal(16, PantheonData.Divinities.Length);
        Assert.Equal(15, PantheonData.Divinities.Count(d => !d.IsGate));
        Assert.Equal("gate", PantheonData.Divinities[PantheonData.Gate].Id);
        Assert.Null(PantheonData.Divinities[PantheonData.Gate].Mythology);
        Assert.Equal(Enum.GetValues<Mythology>(), PantheonData.Mythologies.Select(m => m.Mythology));
        Assert.Equal(["greek", "phoenician", "mesopotamian", "egyptian", "roman"], PantheonData.Mythologies.Select(m => m.Id));
    }

    [Fact]
    public void Every_divinity_is_in_exactly_one_mythology_deck_of_three()
    {
        Assert.All(PantheonData.Mythologies, m => Assert.Equal(PantheonData.DeckSize, m.Divinities.Length));
        var inDecks = PantheonData.Mythologies.SelectMany(m => m.Divinities).ToList();
        Assert.Equal(inDecks.Count, inDecks.Distinct().Count());
        Assert.Equal(PantheonData.Divinities.Where(d => !d.IsGate).Select(d => d.Index).Order(), inDecks.Order());
        Assert.All(PantheonData.Mythologies, m => Assert.All(m.Divinities,
            d => Assert.Equal(m.Mythology, PantheonData.Divinities[d].Mythology)));
    }

    [Theory]
    [InlineData("greek", "aphrodite", "hades", "zeus")]
    [InlineData("phoenician", "astarte", "baal", "tanit")]
    [InlineData("mesopotamian", "enki", "ishtar", "nisaba")]
    [InlineData("egyptian", "anubis", "isis", "ra")]
    [InlineData("roman", "mars", "minerva", "neptune")]
    public void Each_mythology_holds_the_rulebook_divinities(string mythology, params string[] divinities)
    {
        var m = PantheonData.MythologyDef(PantheonData.MythologyOf(mythology));
        Assert.Equal(divinities, m.Divinities.Select(d => PantheonData.Divinities[d].Id));
    }

    [Fact]
    public void Divinities_without_a_decision_carry_their_numbers()
    {
        Assert.Equal(9, D("aphrodite").VictoryPoints);
        Assert.Equal(12, D("tanit").Coins);
        Assert.Equal(7, D("astarte").ProtectedCoins);
        Assert.Equal(2, D("mars").Shields);
        Assert.Equal(ScienceSymbol.Law, D("ishtar").Science);
        // Everything else is implemented by id: no numbers on the other 10 Divinities or the Gate.
        var plain = PantheonData.Divinities.Where(d => d.Id is not ("aphrodite" or "tanit" or "astarte" or "mars" or "ishtar"));
        Assert.All(plain, d => Assert.True(
            d.VictoryPoints == 0 && d.Coins == 0 && d.ProtectedCoins == 0 && d.Shields == 0 && d.Science is null, d.Id));
    }

    // ---------------------------------------------------------------- tokens

    [Fact]
    public void There_are_10_mythology_tokens_and_offering_tokens_of_2_3_and_4()
    {
        Assert.All(PantheonData.Mythologies, m => Assert.Equal(2, m.Tokens));
        Assert.Equal(10, PantheonData.Mythologies.Sum(m => m.Tokens));
        Assert.Equal([2, 3, 4], PantheonData.Offerings.Select(o => o.Value));
        Assert.Equal(2, PantheonData.Offerings[PantheonData.OfferingIndex("offering-4")].Index);
    }

    [Fact]
    public void Token_slots_are_face_down_slots_of_the_rulebook_rows()
    {
        // R1: Age I row 1 both ends, row 3 first/middle/last; Age II row 1 first/middle/last.
        Assert.Equal([2, 4, 9, 11, 13], PantheonData.MythologySlots);
        Assert.Equal([6, 8, 10], PantheonData.OfferingSlots);
        var ageOne = GameData.Layout(1);
        var ageTwo = GameData.Layout(2);
        Assert.All(PantheonData.MythologySlots, s => Assert.False(ageOne.Slots[s].FaceUp, $"Age I slot {s}"));
        Assert.All(PantheonData.OfferingSlots, s => Assert.False(ageTwo.Slots[s].FaceUp, $"Age II slot {s}"));
        Assert.Equal(["1.0", "1.2", "3.0", "3.2", "3.4"], PantheonData.MythologySlots.Select(ageOne.SlotName));
        Assert.Equal(["1.0", "1.2", "1.4"], PantheonData.OfferingSlots.Select(ageTwo.SlotName));
    }

    // ---------------------------------------------------------------- the Pantheon board

    [Fact]
    public void Board_costs_run_3_to_8_cheapest_at_each_players_capital_end_and_sum_to_11()
    {
        Assert.Equal(PantheonData.SpaceCount, PantheonData.Spaces.Length);
        Assert.Equal([1, 2, 3, 4, 5, 6], PantheonData.Spaces.Select(s => s.Number));
        Assert.All(PantheonData.Spaces, s => Assert.Equal(11, s.Cost(0) + s.Cost(1)));
        Assert.All(PantheonData.Spaces, s => Assert.InRange(s.Cost(0), 3, 8));
        // Space 1 is at P1's capital end (the Conflict Pawn's negative end): cheapest for P1, dearest for P2.
        Assert.Equal([3, 4, 5, 6, 7, 8], PantheonData.Spaces.Select(s => s.Cost(0)));
        Assert.Equal([8, 7, 6, 5, 4, 3], PantheonData.Spaces.Select(s => s.Cost(1)));
    }

    // ---------------------------------------------------------------- Grand Temples

    [Theory]
    [InlineData("greek-grand-temple", Mythology.Greek, "S S S G P")]
    [InlineData("phoenician-grand-temple", Mythology.Phoenician, "W S G P P")]
    [InlineData("mesopotamian-grand-temple", Mythology.Mesopotamian, "W W W G P")]
    [InlineData("egyptian-grand-temple", Mythology.Egyptian, "C C C G P")]
    [InlineData("roman-grand-temple", Mythology.Roman, "C S G G P")]
    public void Grand_temples_have_their_mythology_and_cost(string id, Mythology mythology, string cost)
    {
        var temple = PantheonData.Temples[PantheonData.TempleIndex(id)];
        Assert.Equal(mythology, temple.Mythology);
        Assert.Equal(Cost.Parse(cost).Resources, temple.Cost.Resources);
        Assert.Equal(0, temple.Cost.Coins);
        Assert.Same(temple, PantheonData.TempleOf(mythology));
    }

    [Fact]
    public void There_is_one_grand_temple_per_mythology_scoring_5_12_21()
    {
        Assert.Equal(5, PantheonData.Temples.Length);
        Assert.Equal(Enum.GetValues<Mythology>(), PantheonData.Temples.Select(t => t.Mythology).Order());
        Assert.Equal([5, 12, 21], PantheonData.TemplePoints);
    }

    // ---------------------------------------------------------------- Wonders and Progress tokens

    [Fact]
    public void The_two_wonders_continue_the_base_wonder_indices()
    {
        var sanctuary = PantheonData.Wonders[0];
        var theater = PantheonData.Wonders[1];
        Assert.Equal(("sanctuary", 14), (sanctuary.Id, sanctuary.Index));
        Assert.Equal(("divine-theater", 15), (theater.Id, theater.Index));
        Assert.Equal(14, PantheonData.WonderIndex("sanctuary"));
        Assert.Equal(Cost.Parse("P G S S").Resources, sanctuary.Cost.Resources);
        Assert.True(sanctuary.PlayAgain);
        Assert.Equal(0, sanctuary.VictoryPoints);
        // R5: the Divine Theater costs PP G WW.
        Assert.Equal(Cost.Parse("P P G W W").Resources, theater.Cost.Resources);
        Assert.Equal(2, theater.VictoryPoints);
        Assert.Equal(WonderEffect.Theater, theater.Effect);
        Assert.False(theater.PlayAgain);
        // The base data is untouched: still 14 Wonders, and a 16-bit mask holds all 16.
        Assert.Equal(14, GameData.Wonders.Length);
        Assert.True(theater.Index < 16);
    }

    [Fact]
    public void The_three_progress_tokens_continue_the_base_token_indices()
    {
        Assert.Equal(["mysticism", "poliorcetics", "engineering"], PantheonData.Tokens.Select(t => t.Id));
        Assert.Equal([10, 11, 12], PantheonData.Tokens.Select(t => t.Index));
        Assert.Equal(10, GameData.Tokens.Length);
        Assert.All(PantheonData.Tokens, t => Assert.True(t.VictoryPoints == 0 && t.Coins == 0 && t.Science is null, t.Id));
    }

    [Fact]
    public void Pantheon_slugs_are_kebab_case_unique_and_never_clash_with_base_slugs()
    {
        var pantheon = PantheonData.Divinities.Select(d => d.Id)
            .Concat(PantheonData.Temples.Select(t => t.Id))
            .Concat(PantheonData.Wonders.Select(w => w.Id))
            .Concat(PantheonData.Tokens.Select(t => t.Id))
            .Concat(PantheonData.Offerings.Select(o => o.Id))
            .Concat(PantheonData.Mythologies.Select(m => m.Id))
            .ToList();
        Assert.Equal(pantheon.Count, pantheon.Distinct().Count());
        Assert.All(pantheon, id => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", id));
        var baseSlugs = GameData.Cards.Select(c => c.Id).Concat(GameData.Wonders.Select(w => w.Id)).Concat(GameData.Tokens.Select(t => t.Id));
        Assert.Empty(pantheon.Intersect(baseSlugs));
    }

    // ---------------------------------------------------------------- the Ruleset switch and the module seam

    [Theory]
    [InlineData("rules base expansion=pantheon", "rules base start-choice=new expansion=pantheon")]
    [InlineData("rules base expansion=pantheon promo=liberty,messe start-choice=2015", "rules base start-choice=2015 promo=messe,liberty expansion=pantheon")]
    public void Expansion_pantheon_parses_and_round_trips(string line, string written)
    {
        var rules = Ruleset.Parse(line);
        Assert.Equal(Expansions.Pantheon, rules.Expansions);
        Assert.True(rules.HasExpansion(Expansions.Pantheon));
        Assert.Equal(written, rules.ToString());
        Assert.Equal(rules, Ruleset.Parse(written));
        Assert.NotEqual(rules, rules with { Expansions = Expansions.None });
    }

    [Fact]
    public void A_pantheon_game_has_a_pantheon_block_and_a_base_game_none()
    {
        Assert.NotNull(GameState.New(Ruleset.Parse("rules base expansion=pantheon")).Pantheon);
        Assert.Null(GameState.New(Ruleset.Default).Expansion);
        Assert.Null(GameState.New(Ruleset.Parse("rules base promo=messe,liberty")).Expansion);
        Assert.Equal(Expansions.Pantheon, MoveLog.Parse("7wd-log v1\nrules base expansion=pantheon\n").Rules.Expansions);
    }

    [Fact]
    public void Agora_cannot_create_a_game_even_without_the_parser()
    {
        var e = Assert.Throws<NotSupportedException>(() => GameState.New(new Ruleset(Expansions: Expansions.Agora)));
        Assert.Contains("Pantheon", e.Message);
    }

    [Fact]
    public void A_pantheon_game_differs_from_the_base_game_only_by_its_trailing_expansion_bytes()
    {
        var base_ = GameState.New(Ruleset.Default).ToBytes();
        var pantheon = GameState.New(new Ruleset(Expansions: Expansions.Pantheon)).ToBytes();
        Assert.Equal(base_, pantheon[..base_.Length]);
        // Then the expansion byte, Pantheon's 2 Wonders (unbuilt) and the Pantheon block.
        Assert.Equal([(byte)Expansions.Pantheon, 0xFF, 0xFF], pantheon[base_.Length..(base_.Length + 3)]);
        Assert.False(GameState.New(Ruleset.Default).SameAs(GameState.New(new Ruleset(Expansions: Expansions.Pantheon))));
    }

    [Fact]
    public void Pantheon_games_play_to_the_end_copy_and_replay_from_their_log()
    {
        for (ulong seed = 0; seed < 50; seed++)
        {
            var game = new Game(Ruleset.Parse(seed % 2 == 0 ? "rules base expansion=pantheon" : "rules base promo=messe expansion=pantheon"));
            var s = game.State;
            var chance = new RandomChanceSource(seed);
            var rng = new Rng(seed + 1);
            var copy = GameState.New(Ruleset.Default);
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal) game.Apply(chance.Next(s));
                else
                {
                    var moves = s.LegalMoves();
                    game.Apply(new MoveEntry(s.PlayerToDecide, moves[rng.Next(moves.Count)]));
                }
                copy.CopyFrom(s);
                Assert.True(copy.SameAs(s));
                Assert.NotNull(copy.Pantheon);
                Assert.NotSame(s.Pantheon, copy.Pantheon);
            }
            Assert.NotNull(s.Result);
            var replayed = MoveLog.Parse(game.WriteLog());
            Assert.True(replayed.State.SameAs(s));
            Assert.Equal(s.ToBytes(), replayed.State.ToBytes());
            copy.CopyFrom(GameState.New(Ruleset.Default));
            Assert.Null(copy.Expansion);
        }
    }
}
