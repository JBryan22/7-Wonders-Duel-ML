using System.Text.Json;
using System.Text.RegularExpressions;
using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Ticket 32: the read-only Lua snapshot script (run in MoonSharp against fake tables), hidden-card filtering, the
/// name table, and place mapping for all three Ages.
/// </summary>
public sealed class TtsSnapshotTests
{
    internal static readonly string SavePath = Path.Combine(AppContext.BaseDirectory, "Tts", "ml-practice.json");
    internal static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Tts", "fixtures", "ml-practice.snapshot.json");

    static readonly Lazy<FakeTable> Practice = new(() => FakeTable.FromSave(SavePath));

    /// <summary>
    /// Spec P10: the committed save gives a Table Snapshot matching the probe's findings place by place (13 Age II
    /// cards taken, 7 discards, the built Wonders, 2 Progress tokens taken), with no hidden card named.
    /// </summary>
    [Fact]
    public void The_practice_save_gives_a_snapshot_matching_the_probe_findings_place_by_place()
    {
        var snapshot = TableSnapshot.Parse(Practice.Value.Snapshot());
        Assert.Equal("Age2", snapshot.ModAge);
        Assert.Equal("White", snapshot.HostColor);
        Assert.True(snapshot.StillNow);
        var view = TableView.From(snapshot);
        Assert.Equal(2, view.Age);

        // The pyramid: 7 of 20 cards left, all face up, where research ticket 03 saw them.
        var layout = GameData.Layout(2);
        var pyramid = view.At(Place.Pyramid).ToDictionary(p => layout.SlotName(p.Slot), p => p.Slug);
        Assert.Equal(new Dictionary<string, string?>
        {
            ["0.0"] = "library", ["0.1"] = "walls", ["0.3"] = "barracks", ["0.4"] = "horse-breeders", ["0.5"] = "school",
            ["1.0"] = "drying-room", ["1.4"] = "glass-blower",
        }, pyramid);
        Assert.Equal(13, layout.SlotCount - pyramid.Count);

        // 7 discards, named although the mod stacks them face down.
        Assert.Equal(["guard-tower", "quarry", "scriptorium", "shelf-quarry", "stable", "theater", "workshop"],
            view.At(Place.Discard).Select(p => p.Slug!).Order());

        // Cities: 11 cards each, all named; 22 + 4 under Wonders + 7 discarded = 20 + 13 cards taken.
        Assert.Equal(["archery-range", "brewery", "clay-pool", "clay-reserve", "lumber-yard", "palisade", "parade-ground", "press",
            "rostrum", "temple", "wood-reserve"], view.At(Place.City, Side.Red).Select(p => p.Slug!).Order());
        Assert.Equal(["apothecary", "baths", "caravansery", "clay-pit", "customs-house", "dispensary", "garrison", "logging-camp",
            "pharmacist", "stone-pit", "stone-reserve"], view.At(Place.City, Side.White).Select(p => p.Slug!).Order());
        var under = view.At(Place.UnderWonder).ToList();
        Assert.Equal(4, under.Count);
        Assert.All(under, p => Assert.Null(p.Name)); // tucked face down: not named

        // Wonders by row (1 = next to the board), and which are built.
        string? Wonder(Side side, int row) => view.At(Place.Wonder, side).Single(p => p.Row == row).Slug;
        Assert.Equal(["piraeus", "circus-maximus", "sphinx", "mausoleum"], Enumerable.Range(1, 4).Select(r => Wonder(Side.Red, r)));
        Assert.Equal(["temple-of-artemis", "pyramids", "statue-of-zeus", "great-library"], Enumerable.Range(1, 4).Select(r => Wonder(Side.White, r)));
        Assert.Equal([true, false, false, false], Enumerable.Range(1, 4).Select(r => view.WonderBuilt(Side.Red, r)));
        Assert.Equal([true, false, true, true], Enumerable.Range(1, 4).Select(r => view.WonderBuilt(Side.White, r)));

        // Progress tokens: 4 on the board, 2 taken by White, 4 in the bag (the Great Library drew 3 and kept 1).
        Assert.Equal(["architecture", "economy", "masonry", "mathematics"], view.At(Place.ProgressBoard).Select(p => p.Slug!).Order());
        Assert.Equal(["agriculture", "theology"], view.At(Place.ProgressArea, Side.White).Select(p => p.Slug!).Order());
        Assert.Empty(view.At(Place.ProgressArea, Side.Red));
        Assert.Equal(4, view.ProgressBagCount);

        // Soft checks: coins, the pawn 3 spaces towards White, White's 2-coin looting token taken.
        Assert.Equal(3, view.Coins[Side.Red]);
        Assert.Equal(7, view.Coins[Side.White]);
        Assert.Equal(3, view.PawnSpace);
        Assert.Equal([new LootingToken(Side.Red, 5, true), new LootingToken(Side.Red, 2, true), new LootingToken(Side.White, 2, false),
            new LootingToken(Side.White, 5, true)], view.Looting);

        // Storage: removed cards (2 decks of 3), Age III (20), Guilds (7), unused Wonders (4), the promo Wonders, an Agora card.
        Assert.Equal(["deck of 20", "deck of 3", "deck of 3", "deck of 4", "deck of 7", "face-down age card", "messe", "statue-of-liberty"],
            view.At(Place.Storage).Select(TableView.Label).Order(StringComparer.Ordinal));
        Assert.Empty(view.At(Place.Loose));
        Assert.Empty(view.At(Place.WonderOffer));
        Assert.Empty(view.At(Place.Hand));
        Assert.Empty(view.At(Place.LibraryDraw));
    }

    /// <summary>
    /// The committed fixture is exactly what the script gives for the save: the owner's live check compares TTS with
    /// it, and ticket 33 can use it as a recorded snapshot. Set UPDATE_TTS_FIXTURE=1 to rewrite it after a deliberate change.
    /// </summary>
    [Fact]
    public void The_committed_practice_snapshot_is_what_the_script_gives_for_the_save()
    {
        var text = Practice.Value.Snapshot();
        if (Environment.GetEnvironmentVariable("UPDATE_TTS_FIXTURE") == "1")
            File.WriteAllText(FakeTable.RepoFile("tts/fixtures/ml-practice.snapshot.json"), text);
        Assert.True(File.Exists(FixturePath), "tts/fixtures/ml-practice.snapshot.json is missing (run with UPDATE_TTS_FIXTURE=1)");
        Assert.Equal(File.ReadAllText(FixturePath), text);
        Assert.Equal(text, Practice.Value.Snapshot("other-request")); // the request id travels outside the snapshot
    }

    /// <summary>
    /// Every name that leaves TTS for the practice save is a face-up piece or a discard; no hidden name (removed cards,
    /// the undealt Age III and Guild decks, unused Wonders, tokens in the bag, cards tucked under Wonders) and no GUID
    /// appears anywhere in the text.
    /// </summary>
    [Fact]
    public void No_hidden_card_name_leaves_TTS_from_the_practice_save()
    {
        var table = Practice.Value;
        var text = table.Snapshot();
        var discard = TtsData.Places.Discard;

        var publicNames = new HashSet<string>(StringComparer.Ordinal);
        var hiddenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in table.Objects)
        {
            var inDiscard = discard.Contains(o.X, o.Z);
            if (o.Contents is not null)
                foreach (var c in o.Contents) (inDiscard ? publicNames : hiddenNames).Add(c.Name);
            else if (o.Type == "Card" || (o.Tags ?? []).Contains("progress token"))
                (o.FaceDown && !inDiscard ? hiddenNames : publicNames).Add(o.Name);
        }
        hiddenNames.ExceptWith(publicNames);
        var names = TtsData.Names;
        Assert.Equal(6 + 20 + 7 + 4 + 4 + 4, // removed cards, Age III, Guilds, unused Wonders, tokens in the bag, tucked cards
            hiddenNames.Count(n => names.Card(n) is not null || names.Wonder(n) is not null || names.Token(n) is not null));
        Assert.Contains("Tu Quoque Fili", hiddenNames); // a face-down Agora card left in the box area

        var snapshot = TableSnapshot.Parse(text);
        var sentNames = snapshot.Pieces.SelectMany(p => (p.Names ?? []).Append(p.Name)).OfType<string>().ToList();
        Assert.All(sentNames, n => Assert.Contains(n, publicNames));
        foreach (var hidden in hiddenNames.Where(n => n.Length > 0))
            Assert.DoesNotContain(JsonSerializer.Serialize(hidden), text);
        foreach (var o in table.Objects)
            Assert.DoesNotContain($"\"{o.Guid}\"", text);
        foreach (var c in table.Objects.SelectMany(o => o.Contents ?? []))
            Assert.DoesNotContain($"\"{c.Guid}\"", text);
    }

    /// <summary>
    /// Random tables: cards, Wonders and tokens face up or down, anywhere (including the discard pile, a Great Library
    /// draw strip and the hands), decks anywhere. A name leaves TTS only where the rules make it public.
    /// </summary>
    [Fact]
    public void Random_tables_never_name_a_hidden_piece()
    {
        var names = TtsData.Names;
        var cardNames = names.Cards.Keys.ToArray();
        var wonderNames = names.Wonders.Keys.ToArray();
        var tokenNames = names.Tokens.Keys.ToArray();
        var places = TtsData.Places;
        var hands = new[] { new FakeHand("White", 16, 31, -63, -57), new FakeHand("Red", -31, -16, -63, -57) };
        var spots = new (double X, double Z)[]
        {
            (-50.6, 2.3), (-8, 24), (8, -24), (-10, -24), (0, 5), (-10, -13), (37, -21.2), (40, -27), (20, -60), (-24, -60), (-65, -25), (4.5, -7),
        };
        var rng = new Random(32);
        var checkedNames = 0;
        for (var game = 0; game < 40; game++)
        {
            var objects = new List<FakeObject>();
            for (var i = 0; i < 30; i++)
            {
                var (x, z) = spots[rng.Next(spots.Length)];
                x += rng.NextDouble() - 0.5;
                z += rng.NextDouble() - 0.5;
                var down = rng.Next(2) == 0;
                var guid = $"g{game}x{i}";
                objects.Add(rng.Next(4) switch
                {
                    0 => new FakeObject("Card", guid, cardNames[rng.Next(cardNames.Length)], x, 1.6, z, down, ["green"]),
                    1 => new FakeObject("Card", guid, wonderNames[rng.Next(wonderNames.Length)], x, 1.6, z, down, ["wonder"]),
                    2 => new FakeObject("Generic", guid, tokenNames[rng.Next(tokenNames.Length)], x, 1.7, z, down, ["progress token"]),
                    _ => new FakeObject("Deck", guid, "", x, 1.6, z, down, Contents: Enumerable.Range(0, rng.Next(1, 5))
                        .Select(k => new FakeContent($"{guid}c{k}", cardNames[rng.Next(cardNames.Length)], ["red"])).ToList()),
                });
            }
            var table = new FakeTable(objects, FakeTable.ProbePlayers, "Age1", hands);
            var snapshot = TableSnapshot.Parse(table.Snapshot());
            Assert.Equal(objects.Count, snapshot.Pieces.Count);

            var allowed = new List<string>();
            foreach (var o in objects)
            {
                var inHand = hands.Any(h => o.X >= h.X1 && o.X <= h.X2 && o.Z >= h.Z1 && o.Z <= h.Z2);
                var inDiscard = places.Discard.Contains(o.X, o.Z);
                var inLibrary = places.LibraryDraw.Values.Any(r => r.Contains(o.X, o.Z));
                if (inHand) continue;
                if (o.Contents is not null) { if (inDiscard) allowed.AddRange(o.Contents.Select(c => c.Name)); }
                else if ((o.Tags ?? []).Contains("progress token")) { if (!o.FaceDown && !inLibrary) allowed.Add(o.Name); }
                else if (!o.FaceDown || inDiscard) allowed.Add(o.Name);
            }
            var sent = snapshot.Pieces.SelectMany(p => (p.Names ?? []).Append(p.Name)).OfType<string>().Order().ToList();
            Assert.Equal(allowed.Order().ToList(), sent);
            Assert.All(snapshot.Pieces.Where(p => p.InHand), p => Assert.True(p.Name is null && p.Names is null));
            checkedNames += sent.Count;
        }
        Assert.True(checkedNames > 200);
    }

    /// <summary>
    /// The script only reads: the fake table raises an error on any call or assignment other than a getter, and on a new
    /// global (proved below by a script that tries), and the text has no mutating TTS call in it.
    /// </summary>
    [Fact]
    public void The_snapshot_script_is_read_only()
    {
        var table = Practice.Value;
        table.Snapshot(); // would throw on any non-getter use

        foreach (var bad in new[]
        {
            "getObjects()[1].setPosition({0, 0, 0})",
            "getObjects()[1].flip()",
            "getObjects()[1].locked = true",
            "leaked = 1",
            "Player.White.setHandTransform({})",
        })
            Assert.ThrowsAny<Exception>(() => table.Run(bad));

        var code = Regex.Replace(TtsData.SnapshotScript, "--[^\n]*", ""); // comments may name what the script never does
        foreach (var call in new[]
        {
            "set", "flip", "destruct", "destroy", "take", "deal", "shuffle", "spawn", "put", "lock", "unlock", "highlight",
            "broadcast", "print", "call", "reload", "randomize", "cut", "split", "translate", "rotate", "scale", "addForce",
            "addTorque", "jointTo", "drop", "attach", "reset", "UI", "WebRequest", "Wait", "startLuaCoroutine", "Turns",
        })
            Assert.DoesNotMatch($@"[.:]\s*{call}\w*\s*[\(\{{""]|\b{call}\s*\(|\b{call}\.", code);
        Assert.DoesNotMatch(@"[.:]\s*(locked|interactable|tooltip|use_\w+|sticky|drag_selectable)\s*=[^=]", code);
    }

    /// <summary>Every card, Wonder and Progress token has exactly one TTS name, and every name in the save is known.</summary>
    [Fact]
    public void The_name_table_covers_every_card_Wonder_and_token()
    {
        var names = TtsData.Names;
        Assert.Equal(GameData.Cards.Select(c => c.Id).Order(), names.Cards.Values.Order());
        Assert.Equal(GameData.Wonders.Select(w => w.Id).Order(), names.Wonders.Values.Order());
        Assert.Equal(GameData.Tokens.Select(t => t.Id).Order(), names.Tokens.Values.Order());
        Assert.Equal("pretorium", names.Card("Courthouse")); // the mod's Age III red card, not our Age II blue courthouse
        Assert.Equal("courthouse", names.Card("Tribunal"));

        foreach (var o in Practice.Value.Objects)
        {
            foreach (var c in o.Contents ?? [])
                if (c.Tags.Contains("wonder")) Assert.NotNull(names.Wonder(c.Name));
                else if (c.Tags.Contains("progress token")) Assert.NotNull(names.Token(c.Name));
                else if (c.Tags.Count > 0) Assert.NotNull(names.Card(c.Name));
            if (o.Type == "Card" && (o.Tags ?? []).Contains("wonder")) Assert.NotNull(names.Wonder(o.Name));
            else if (o.Type == "Card" && o.Tags is { Count: > 0 } tags && !tags.Contains("color card")) Assert.NotNull(names.Card(o.Name));
        }
    }

    /// <summary>
    /// Where the mod's Age I, II and III setup buttons deal each card (copied from the mod's scripts, base version),
    /// row by row, and whether they deal it face up ("flip = true"). Slot order is the Engine's: top row first, left to right.
    /// </summary>
    static readonly Dictionary<int, (double Z, double[] Xs, bool Up)[]> ModDeals = new()
    {
        [1] = [(10, [-2, 2], true), (7.5, [-4, 0, 4], false), (5, [-6, -2, 2, 6], true), (2.5, [-8, -4, 0, 4, 8], false),
            (0, [-10, -6, -2, 2, 6, 10], true)],
        [2] = [(10, [-10, -6, -2, 2, 6, 10], true), (7.5, [-8, -4, 0, 4, 8], false), (5, [-6, -2, 2, 6], true),
            (2.5, [-4, 0, 4], false), (0, [-2, 2], true)],
        [3] = [(10, [-2, 2], true), (7.5, [-4, 0, 4], false), (5, [-6, -2, 2, 6], true), (2.5, [-4, 4], false),
            (0, [-6, -2, 2, 6], true), (-2.5, [-4, 0, 4], false), (-5, [-2, 2], true)],
    };

    /// <summary>All three Age grids: every card dealt by the mod (nudged as a hand would) lands on its Engine slot, face as the layout says.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Each_Age_pyramid_maps_to_the_Engine_slots(int age)
    {
        var layout = GameData.Layout(age);
        var rng = new Random(age);
        var objects = new List<FakeObject>();
        var guilds = age == 3 ? new HashSet<int> { 1, 9, 17 } : [];
        foreach (var (z, xs, up) in ModDeals[age])
            foreach (var x in xs)
            {
                var i = objects.Count;
                var name = TtsData.Names.Cards.First(c => GameData.Cards[GameData.CardIndex(c.Value)].Age == age).Key;
                objects.Add(new FakeObject("Card", $"s{i}", name, x + (rng.NextDouble() - 0.5) * 1.2, 1.6 + 0.02 * i, z + (rng.NextDouble() - 0.5) * 1.2,
                    FaceDown: !up, Tags: guilds.Contains(i) ? ["purple"] : ["blue"]));
            }
        Assert.Equal(layout.SlotCount, objects.Count);

        var view = TableView.From(TableSnapshot.Parse(new FakeTable(objects, [], $"Age{age}").Snapshot()));
        Assert.Equal(age, view.Age);
        var placed = view.At(Place.Pyramid).OrderBy(p => p.Slot).ToList();
        Assert.Equal(Enumerable.Range(0, layout.SlotCount), placed.Select(p => p.Slot));
        foreach (var p in placed)
        {
            Assert.Equal(layout.Slots[p.Slot].FaceUp, !p.Piece.FaceDown);
            Assert.Equal(layout.Slots[p.Slot].FaceUp, p.Slug is not null);
        }
        if (age == 3)
            Assert.Equal(guilds.Order(), placed.Where(p => p.Piece.Back == "guild").Select(p => p.Slot));

        // The same cards read with another Age's grid don't pretend to be on it.
        var other = TableView.From(view.Snapshot, age == 3 ? 1 : 3);
        Assert.NotEqual(view.Facts(), other.Facts());
        // With no Age (before Age I is dealt) nothing is on a pyramid slot.
        Assert.Empty(TableView.From(TableSnapshot.Parse(new FakeTable(objects, [], "None").Snapshot())).At(Place.Pyramid));
    }

    /// <summary>The draft: offered Wonders where the Deal Wonders button lays them, and a Wonder taken into a column.</summary>
    [Fact]
    public void Wonder_offers_columns_and_the_Library_draw_are_placed()
    {
        var objects = new List<FakeObject>
        {
            new("Card", "w1", "The Sphinx", -4.3, 1.6, -7.2, Tags: ["wonder"]),
            new("Card", "w2", "The Colossus", 4.6, 1.6, -12.8, Tags: ["wonder"]),
            new("Card", "w3", "Piraeus", 39.9, 1.6, -21.3, Tags: ["wonder"]),
            new("Card", "w4", "The Great Library", -39.8, 1.6, -32.6, Tags: ["wonder"]),
            new("Card", "c1", "Altar", -36.9, 1.7, -32.7, FaceDown: true, Tags: ["blue"]),
            new("Generic", "t1", "Law", -12.1, 1.7, -24.0, Tags: ["progress token"]),
            new("Generic", "t2", "Urbanism", -8, 1.7, -24.0, Tags: ["progress token"]),
            new("Generic", "t3", "Strategy", -27, 1.7, -15, Tags: ["progress token"]),
            new("Card", "x1", "Baths", 0, 1.6, -20, Tags: ["blue"]),
        };
        var view = TableView.From(TableSnapshot.Parse(new FakeTable(objects, [], "Age1").Snapshot()));
        Assert.Equal(["colossus", "sphinx"], view.At(Place.WonderOffer).Select(p => p.Slug!).Order());
        Assert.Equal(2, view.At(Place.Wonder, Side.White).Single().Row);
        Assert.Equal("great-library", view.At(Place.Wonder, Side.Red).Single(p => p.Row == 4).Slug);
        Assert.True(view.WonderBuilt(Side.Red, 4));
        Assert.Equal(2, view.At(Place.LibraryDraw, Side.Red).Count(p => p.Name is null));
        Assert.Equal("strategy", view.At(Place.ProgressArea, Side.Red).Single().Slug);
        Assert.Contains("loose: baths near (0, -20)", view.Facts());
    }

    [Fact]
    public void Comparing_tables_lists_what_is_missing_and_extra()
    {
        Assert.Empty(TableView.Compare(["a", "b", "b"], ["b", "a", "b"]));
        Assert.Equal(["extra:   c", "missing: b"], TableView.Compare(["a", "b", "b"], ["a", "b", "c"]));
    }
}
