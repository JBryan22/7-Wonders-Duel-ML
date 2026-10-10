namespace SevenWondersDuel.Engine.Tests;

public class MoveLogTests
{
    [Fact]
    public void Rules_header_round_trips()
    {
        Assert.Equal("rules base start-choice=new", Ruleset.Default.ToString());
        Assert.Equal(StartChoice.Rule2015, Ruleset.Parse("rules base start-choice=2015").StartChoice);
        Assert.Equal(Ruleset.Default, Ruleset.Parse("rules base"));
    }

    [Fact]
    public void Expansions_are_rejected_as_not_supported()
    {
        var e = Assert.Throws<NotSupportedException>(() => Ruleset.Parse("rules base expansion=pantheon"));
        Assert.Contains("not supported", e.Message);
    }

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
        Assert.Equal([(1, Data.GameData.CardIndex("altar")), (19, Data.GameData.CardIndex("baths"))], entry.Cards);
        Assert.Equal("reveal age1 0.1 altar 4.5 baths", entry.ToString());
    }
}
