using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

public class MoveLogTests
{
    const string Tokens = "reveal tokens agriculture economy law strategy urbanism";
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int C(string slug) => GameData.CardIndex(slug);

    /// <summary>A game at P1's first Age I turn; P2 owns the Great Library. Both players get 50 coins.</summary>
    static Game Start()
    {
        var draft = Setup.Draft(0, ["pyramids", "great-lighthouse", "temple-of-artemis", "statue-of-zeus"],
            ["great-library", "piraeus", "appian-way", "colossus"]);
        var game = MoveLog.Parse(string.Join('\n', ["7wd-log v1", "rules base", "reveal first P1", Tokens, .. draft, Opening]));
        game.State.SetCoins(0, 50);
        game.State.SetCoins(1, 50);
        return game;
    }

    static void Do(Game game, params string[] lines)
    {
        foreach (var line in lines) game.Apply(MoveLog.ParseEntry(line, game.State));
    }

    // ---------------------------------------------------------------- the rules header

    [Fact]
    public void Rules_header_round_trips()
    {
        Assert.Equal("rules base start-choice=new", Ruleset.Default.ToString());
        Assert.Equal(StartChoice.Rule2015, Ruleset.Parse("rules base start-choice=2015").StartChoice);
        Assert.Equal(Ruleset.Default, Ruleset.Parse("rules base"));
    }

    [Fact]
    public void Promo_switches_parse_in_any_order_and_are_written_in_a_fixed_one()
    {
        var rules = Ruleset.Parse("rules base promo=liberty,messe start-choice=2015");
        Assert.Equal(new Ruleset(StartChoice.Rule2015, Promos.Messe | Promos.Liberty), rules);
        Assert.Equal("rules base start-choice=2015 promo=messe,liberty", rules.ToString());
        Assert.Equal(rules, Ruleset.Parse(rules.ToString()));
        Assert.Equal(Promos.Liberty, Ruleset.Parse("rules base promo=liberty").Promos);
        Assert.True(Ruleset.Parse("rules base promo=messe").HasPromo(Promos.Messe));
        Assert.False(Ruleset.Parse("rules base promo=messe").HasPromo(Promos.Liberty));
    }

    [Theory]
    [InlineData("rules base promo=colossus", "Unknown promo")]
    [InlineData("rules base promo=", "at least one")]
    [InlineData("rules base promo=messe,messe", "twice")]
    [InlineData("rules base start-choice=new start-choice=2015", "twice")]
    [InlineData("rules base start-choice=2020", "Unknown start-choice")]
    [InlineData("rules base expansion=cities", "Unknown expansion")]
    [InlineData("rules base speed=fast", "Unknown rules option")]
    [InlineData("rules base messe", "key=value")]
    [InlineData("rules agora", "rules base")]
    public void Bad_rules_headers_are_format_errors(string line, string reason)
    {
        var e = Assert.Throws<FormatException>(() => Ruleset.Parse(line));
        Assert.Contains(reason, e.Message);
    }

    [Theory]
    [InlineData("rules base expansion=pantheon")]
    [InlineData("rules base expansion=agora")]
    [InlineData("rules base start-choice=2015 promo=liberty expansion=pantheon,agora")]
    public void Expansions_are_rejected_as_not_supported(string line)
    {
        var e = Assert.Throws<NotSupportedException>(() => Ruleset.Parse(line));
        Assert.Contains("not supported", e.Message);
    }

    [Fact]
    public void Logs_with_an_expansion_are_rejected_as_not_supported()
    {
        var e = Assert.Throws<NotSupportedException>(() => MoveLog.Parse("7wd-log v1\nrules base expansion=agora\nreveal first P1\n"));
        Assert.Contains("not supported", e.Message);
        Assert.Contains("line 2", e.Message);
    }

    [Fact]
    public void The_engine_rejects_promo_wonders_until_they_are_built()
    {
        // The grammar is ticket 09's; the Messe and the Statue of Liberty arrive with tickets 12-13.
        var e = Assert.Throws<NotSupportedException>(() => MoveLog.Parse("7wd-log v1\nrules base promo=messe\n"));
        Assert.Contains("not supported", e.Message);
    }

    // ---------------------------------------------------------------- general format

    [Fact]
    public void A_log_needs_the_version_line() =>
        Assert.Throws<FormatException>(() => MoveLog.Parse("rules base\nreveal first P1\n"));

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var game = MoveLog.Parse("# a game\n7wd-log v1\n\nrules base\r\n# who starts\nreveal first P2\n");
        Assert.Equal(1, game.State.FirstPlayer);
    }

    [Fact]
    public void Slot_names_count_from_zero_at_the_top_row()
    {
        var game = MoveLog.Parse("7wd-log v1\nrules base\nreveal first P1\n");
        var entry = (SlotReveal)MoveLog.ParseEntry("reveal age1 0.1 altar 4.5 baths", game.State);
        Assert.Equal([(1, C("altar")), (19, C("baths"))], entry.Cards);
        Assert.Equal("reveal age1 0.1 altar 4.5 baths", entry.ToString());
    }

    // ---------------------------------------------------------------- slot-named Moves

    [Fact]
    public void Slot_named_moves_resolve_to_the_face_up_card_in_the_slot()
    {
        var game = Start();
        var s = game.State;
        Assert.Equal(new MoveEntry(0, Move.Build(C("quarry"))), MoveLog.ParseEntry("P1 build 4.3", s));
        Assert.Equal(new MoveEntry(0, Move.Discard(C("apothecary"))), MoveLog.ParseEntry("P1 discard 4.0", s));
        Assert.Equal(new MoveEntry(0, Move.BuildWonder(C("baths"), GameData.WonderIndex("pyramids"))),
            MoveLog.ParseEntry("P1 wonder 4.1 pyramids", s));

        // Applied and logged by card name: the base game never writes a slot-named Move.
        Do(game, "P1 build 4.3", "P2 discard 4.5");
        Assert.True(s.City(0).Contains(C("quarry")));
        Assert.True(s.DiscardPile.Contains(C("stone-reserve")));
        Assert.Equal(["P1 build quarry", "P2 discard stone-reserve"], game.Log.TakeLast(2).Select(e => e.ToString()));
    }

    [Theory]
    [InlineData("P1 build 3.0", "face-down")]
    [InlineData("P1 build 9.0", "not a slot")]
    [InlineData("P1 build 4.6", "not a slot")]
    [InlineData("P1 wonder 3.0 pyramids", "face-down")]
    public void Slot_named_moves_need_a_face_up_card(string line, string reason)
    {
        var e = Assert.Throws<FormatException>(() => MoveLog.ParseEntry(line, Start().State));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void Slot_named_moves_on_an_empty_slot_or_before_an_age_are_errors()
    {
        var game = Start();
        Do(game, "P1 build 4.3");
        Assert.Contains("empty", Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P2 build 4.3", game.State)).Message);

        var early = MoveLog.Parse("7wd-log v1\nrules base\nreveal first P1\n");
        Assert.Contains("before any Age", Assert.Throws<FormatException>(() => MoveLog.ParseEntry("P1 build 4.3", early.State)).Message);
    }

    [Fact]
    public void A_slot_named_move_of_a_card_that_is_not_accessible_is_illegal()
    {
        var game = Start();
        // 0.0 holds the face-up Palisade, covered by row 1.
        Assert.Equal(new MoveEntry(0, Move.Build(C("palisade"))), MoveLog.ParseEntry("P1 build 0.0", game.State));
        Assert.Throws<InvalidOperationException>(() => Do(game, "P1 build 0.0"));
    }

    // ---------------------------------------------------------------- redacted private Reveals

    [Fact]
    public void A_redacted_library_draw_parses_and_hides_the_draw()
    {
        var game = Start();
        var s = game.State;
        Do(game, "P1 discard stone-reserve", "P2 wonder quarry great-library");
        Assert.True(s.AwaitingLibraryDraw);

        var entry = MoveLog.ParseEntry("reveal P2 library ? ? ?", s);
        Assert.Equal(1, Assert.IsType<LibraryReveal>(entry).Player);
        Assert.True(((LibraryReveal)entry).Redacted);
        Assert.Equal(1, ((Reveal)entry).Audience);
        game.Apply(entry);

        Assert.True(s.LibraryDrawHidden);
        Assert.Equal(0, s.LibraryDraw);
        Assert.Equal(StepKind.Decision, s.Step);
        Assert.Equal(1, s.PlayerToDecide);
        // Any token left in the box could have been drawn.
        Assert.Equal(ProgressTokens.In(s.TokensSetAside).Select(Move.Library), s.LegalMoves());
        Assert.True(s.Observe(0).State.LibraryDrawHidden);
        Assert.True(s.Observe(1).State.LibraryDrawHidden);

        Do(game, "P2 library philosophy");
        Assert.True(s.HasToken(1, ProgressTokens.Philosophy));
        Assert.False(s.LibraryDrawHidden);
        Assert.Equal(0, s.PlayerToDecide);
    }

    [Fact]
    public void A_log_with_a_redacted_draw_round_trips()
    {
        var game = Start();
        Do(game, "P1 discard stone-reserve", "P2 wonder quarry great-library", "reveal P2 library ? ? ?", "P2 library theology");
        var text = game.WriteLog();
        Assert.Contains("\nreveal P2 library ? ? ?\n", text);

        // Replay the written lines onto the same start (the 50 coins are test setup, outside the log).
        var replay = Start();
        Do(replay, text.TrimEnd('\n').Split('\n').TakeLast(4).ToArray());
        Assert.Equal(text, replay.WriteLog());
        Assert.True(replay.State.SameAs(game.State));
        Assert.True(replay.State.HasToken(1, ProgressTokens.Theology));
    }

    [Theory]
    [InlineData("reveal P2 library ? law ?")]
    [InlineData("reveal P2 library ? ?")]
    [InlineData("reveal P2 library ? ? ? ?")]
    public void A_partly_redacted_draw_is_an_error(string line)
    {
        var game = Start();
        Do(game, "P1 discard stone-reserve", "P2 wonder quarry great-library");
        Assert.ThrowsAny<Exception>(() => Do(game, line));
        Assert.True(game.State.AwaitingLibraryDraw);
    }

    [Fact]
    public void A_redacted_draw_names_the_drawer_and_comes_after_the_flips()
    {
        var game = Start();
        Do(game, "P1 discard stone-reserve", "P2 wonder quarry great-library");
        Assert.Throws<InvalidOperationException>(() => Do(game, "reveal P1 library ? ? ?"));

        game = Start();
        Do(game, "P1 discard apothecary", "P2 wonder baths great-library"); // uncovers 3.0
        Assert.Throws<InvalidOperationException>(() => Do(game, "reveal P2 library ? ? ?"));
        Do(game, "reveal 3.0 workshop", "reveal P2 library ? ? ?");
        Assert.True(game.State.LibraryDrawHidden);
    }
}
