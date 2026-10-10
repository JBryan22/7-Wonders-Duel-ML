using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Agents.Tests;

/// <summary>
/// The parts of the C++ cross-check (ticket 11) that need no C++ driver, so they run in CI: the object-id mapping
/// from the driver's names, the translation of C++ actions into our Move words, and the status-line parser.
/// The cross-check itself is opt-in (spec A11): `7wd crosscheck` after tools/crosscheck/build.sh.
/// </summary>
public class CrossCheckTests
{
    /// <summary>The driver's <c>names</c> reply for indjev99's engine @01ed7a5f (src/game/objects.cpp).</summary>
    const string Names = "names 0=Lumber_Yard;1=Logging_Camp;2=Clay_Pool;3=Clay_Pit;4=Quarry;5=Stone_Pit;6=Sawmill;7=Brickyard;"
        + "8=Shelf_Quarry;9=Glassworks;10=Press;11=Glassblower;12=Drying_Room;13=Theater;14=Altar;15=Baths;16=Statue;17=Temple;"
        + "18=Aqueduct;19=Rostrum;20=Courthouse;21=Gardens;22=Pantheon;23=Senate;24=Palace;25=Town_Hall;26=Obelisk;27=Stable;"
        + "28=Garrison;29=Palisade;30=Guard_Tower;31=Horse_Breeders;32=Barracks;33=Archery_Range;34=Parade_Ground;35=Walls;"
        + "36=Fortifications;37=Siege_Workshop;38=Circus;39=Arsenal;40=Pretorium;41=Scriptorium;42=Pharmacist;43=Workshop;"
        + "44=Apothecary;45=Library;46=Dispensary;47=School;48=Laboratory;49=University;50=Observatory;51=Academy;52=Study;"
        + "53=Tavern;54=Stone_Reserve;55=Clay_Reserve;56=Wood_Reserve;57=Brewery;58=Forum;59=Caravansery;60=Customs_House;"
        + "61=Lighthouse;62=Arena;63=Chamber_of_Commerce;64=Port;65=Armory;66=Merchants_Guild;67=Shipowners_Guild;"
        + "68=Builders_Guild;69=Magistrates_Guild;70=Scientists_Guild;71=Moneylenders_Guild;72=Tacticians_Guild;73=Agriculture;"
        + "74=Architecture;75=Economy;76=Law;77=Masonry;78=Mathematics;79=Philosophy;80=Strategy;81=Theology;82=Urbanism;"
        + "83=The_Appian_Way;84=Circus_Maximus;85=The_Colossus;86=The_Great_Library;87=The_Great_Lighthouse;"
        + "88=The_Hanging_Gardens;89=The_Mausoleum;90=Piraeus;91=The_Pyramids;92=The_Sphinx;93=The_Statue_of_Zeus;"
        + "94=The_Temple_of_Artemis;95=Looting_1;96=Looting_2";

    static readonly CppObjectMap Map = new(Names);

    [Fact]
    public void Every_base_card_wonder_and_token_maps_to_a_cpp_object()
    {
        Assert.Equal(11, Map.Card(GameData.CardIndex("glass-blower")));
        Assert.Equal(16, Map.Card(GameData.CardIndex("statue")));
        Assert.Equal(45, Map.Card(GameData.CardIndex("library")));
        Assert.Equal(86, Map.Wonder(GameData.WonderIndex("great-library")));
        Assert.Equal(93, Map.Wonder(GameData.WonderIndex("statue-of-zeus")));
        Assert.Equal(76, Map.Token(GameData.TokenIndex("law")));
    }

    [Fact]
    public void A_names_reply_missing_an_object_is_rejected()
    {
        Assert.Throws<FormatException>(() => new CppObjectMap(Names.Replace(";15=Baths", "")));
        Assert.Throws<FormatException>(() => new CppObjectMap(Names.Replace("Baths", "Hot_Springs")));
    }

    [Theory]
    [InlineData(0, 15, -1, 0, "build baths")]
    [InlineData(0, 15, -2, 0, "discard baths")]
    [InlineData(0, 15, 92, 0, "wonder baths sphinx")]
    [InlineData(1, 76, -100, 0, "token law")]
    [InlineData(2, 80, -100, 1, "library strategy")]
    [InlineData(3, 45, -100, 0, "mausoleum library")]
    [InlineData(4, 0, 0, 1, "destroy lumber-yard")]
    [InlineData(5, 86, -100, 0, "draft great-library")]
    [InlineData(6, 1, -100, 1, "start me")]
    [InlineData(6, 0, -100, 1, "start opponent")]
    public void Cpp_actions_read_as_our_move_words(int type, int arg1, int arg2, int actor, string expected)
    {
        Assert.Equal(expected, Map.Describe(new CppAction(type, arg1, arg2), actor));
    }

    [Fact]
    public void Status_lines_parse()
    {
        var next = CppStatus.Parse("next 0 -100 -100 1 0 7 5 0 2 0:15:-2 0:15:-1");
        Assert.False(next.End);
        Assert.Equal((0, 1, 0, 7, 5, 0, 2), (next.Expected.Type, next.Actor, next.Age, next.Coins0, next.Coins1, next.Military0, next.Military1));
        Assert.Equal([new CppAction(0, 15, -2), new CppAction(0, 15, -1)], next.Legal);

        var end = CppStatus.Parse("end -4 60 60 10 12 3 9 4 5");
        Assert.True(end.End);
        Assert.Equal(1, end.Result!.Winner);
        Assert.Equal(VictoryType.Civilian, end.Result.Victory);
        Assert.Equal(-1, CppStatus.Parse("end 0 60 60 10 10 3 9 4 5").Result!.Winner);
        Assert.Equal(VictoryType.Military, CppStatus.Parse("end 3 20 30 0 0 3 9 12 3").Result!.Victory);
        Assert.Throws<FormatException>(() => CppStatus.Parse("err Unexpected action."));
    }

    [Fact]
    public void A_missing_driver_names_the_build_script()
    {
        var e = Assert.Throws<FileNotFoundException>(() => new CppDriver(Path.Combine(Path.GetTempPath(), "no-such-7wd-driver")));
        Assert.Contains("tools/crosscheck/build.sh", e.Message);
    }
}
