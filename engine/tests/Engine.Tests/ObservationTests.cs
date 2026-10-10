using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Observe(state, player) (spec A4): the Game State with every Reveal whose audience excludes the player hidden.
/// In the base game that is only the opponent's pending Great Library draw.
/// </summary>
public class ObservationTests
{
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 pharmacist "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";

    static int T(string slug) => GameData.TokenIndex(slug);

    /// <summary>P1 has just built the Great Library and drawn <paramref name="draw"/> (3 of the box's tokens).</summary>
    static Game LibraryDrawn(string draw)
    {
        var game = MoveLog.Parse(string.Join('\n',
        [
            "7wd-log v1", "rules base", "reveal first P1", "reveal tokens agriculture economy law strategy urbanism",
            .. Setup.DraftFor("great-library"), Opening,
        ]));
        game.State.SetCoins(0, 50);
        game.Apply(MoveLog.ParseEntry("P1 wonder quarry great-library", game.State));
        game.Apply(MoveLog.ParseEntry($"reveal P1 library {draw}", game.State));
        return game;
    }

    [Fact]
    public void The_opponent_never_sees_the_great_library_draw()
    {
        var s = LibraryDrawn("mathematics philosophy theology").State;
        var theirs = s.Observe(1);
        Assert.Equal(1, theirs.Player);
        Assert.Equal(0, theirs.State.LibraryDraw);
        Assert.True(theirs.State.LibraryDrawHidden);
        // As far as P2 can tell, P1 may keep any of the 5 tokens in the box.
        Assert.Equal(ProgressTokens.In(s.TokensSetAside).Select(Move.Library), theirs.LegalMoves());
        Assert.Contains("hidden", theirs.ToString());
        Assert.DoesNotContain("mathematics", theirs.ToString());
    }

    [Fact]
    public void The_drawer_sees_its_own_draw_and_nothing_else_differs()
    {
        var s = LibraryDrawn("mathematics philosophy theology").State;
        var mine = s.Observe(0);
        Assert.True(mine.State.SameAs(s));
        Assert.Equal((ushort)(1 << T("mathematics") | 1 << T("philosophy") | 1 << T("theology")), mine.State.LibraryDraw);
        Assert.False(mine.State.LibraryDrawHidden);
        Assert.Equal(3, mine.LegalMoves().Count);
    }

    [Fact]
    public void Two_draws_look_the_same_to_the_opponent_and_different_to_the_drawer()
    {
        var a = LibraryDrawn("mathematics philosophy theology").State;
        var b = LibraryDrawn("architecture masonry theology").State;
        Assert.False(a.SameAs(b));
        Assert.True(a.Observe(1).SameAs(b.Observe(1)));
        Assert.False(a.Observe(0).SameAs(b.Observe(0)));
        Assert.False(a.Observe(0).SameAs(a.Observe(1)));
    }

    [Fact]
    public void Once_the_token_is_kept_both_observations_are_the_whole_state()
    {
        var game = LibraryDrawn("mathematics philosophy theology");
        game.Apply(MoveLog.ParseEntry("P1 library philosophy", game.State));
        var s = game.State;
        Assert.True(s.Observe(0).State.SameAs(s));
        Assert.True(s.Observe(1).State.SameAs(s));
        Assert.True(s.Observe(1).State.HasToken(0, ProgressTokens.Philosophy));
    }

    [Fact]
    public void Without_private_reveals_an_observation_is_the_whole_state_including_unseen_wonders()
    {
        var game = new Game(Ruleset.Default);
        game.Apply(new FirstPlayerReveal(0));
        game.Apply(MoveLog.ParseEntry("reveal tokens agriculture economy law strategy urbanism", game.State));
        game.Apply(MoveLog.ParseEntry(Setup.Draft(0)[0], game.State));
        var s = game.State;
        for (var p = 0; p < 2; p++)
        {
            var o = s.Observe(p);
            Assert.True(o.State.SameAs(s));
            Assert.Equal(s.UnseenWonders, o.State.UnseenWonders);
            Assert.Equal(s.DraftOffer, o.State.DraftOffer);
        }
    }

    [Fact]
    public void An_observation_is_a_copy_that_never_changes_the_game()
    {
        var game = LibraryDrawn("mathematics philosophy theology");
        var s = game.State;
        var before = s.Clone();
        var theirs = s.Observe(1);
        theirs.State.Apply(Move.Library(T("masonry"))); // legal as far as P2 can tell
        var mine = s.Observe(0);
        mine.State.Apply(Move.Library(T("theology")));
        Assert.True(before.SameAs(s));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Observe(2));
    }
}
