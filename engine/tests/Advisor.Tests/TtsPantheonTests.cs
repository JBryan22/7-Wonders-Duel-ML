using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SevenWondersDuel.Advisor.Tts;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Pantheon ticket 13 (spec-pantheon P-A9): the Pantheon pieces of the "ULTIMATE ed." mod (names, slugs, places), the
/// snapshot filter for private zones, and reconciliation of the new Moves and Reveals from tables read through the real
/// snapshot.lua.
/// </summary>
public sealed class TtsPantheonTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Lazy<JsonNode> Mod = new(() => JsonNode.Parse(File.ReadAllText(FakeTable.RepoFile("tts/fixtures/pantheon-pieces.json")))!);

    static IEnumerable<JsonNode> ModObjects() => Mod.Value["objects"]!.AsArray().Select(o => o!);

    static IEnumerable<JsonNode> WithContents(JsonNode o) =>
        new[] { o }.Concat(o["contents"]?.AsArray().Select(c => c!) ?? []);

    static List<string> Tags(JsonNode o) => o["tags"]?.AsArray().Select(t => (string)t!).ToList() ?? [];

    /// <summary>
    /// Every Pantheon piece in the mod file (extracted in tts/fixtures/pantheon-pieces.json) maps to a slug, or is
    /// listed as never input; and every Pantheon slug has exactly one piece.
    /// </summary>
    [Fact]
    public void Every_Pantheon_piece_in_the_mod_maps_to_a_slug_or_is_listed_as_unknown()
    {
        var names = TtsData.Names.Pantheon;
        var divinities = new List<string>();
        var temples = new List<string>();
        var wonders = new List<string>();
        var tokens = new List<string>();
        var tokenImages = new List<string>();
        var pieces = new List<string>();
        var unmapped = new List<string>();
        foreach (var piece in ModObjects().SelectMany(WithContents))
        {
            var type = (string)piece["type"]!;
            var nickname = (string)piece["nickname"]!;
            var tags = Tags(piece);
            if (type is "Deck") continue; // a container: its cards are checked one by one
            if (type is "Card" or "CardCustom")
            {
                if (tags.Contains("wonder")) wonders.Add(names.Wonders.GetValueOrDefault(nickname) ?? $"? {nickname}");
                else if (names.Temples.TryGetValue(nickname, out var temple)) temples.Add(temple);
                else if (names.Divinities.TryGetValue(nickname, out var divinity)) divinities.Add(divinity);
                else unmapped.Add(nickname);
            }
            else if (tags.Contains("progress token")) tokens.Add(names.Tokens.GetValueOrDefault(nickname) ?? $"? {nickname}");
            else if (names.Pieces.TryGetValue(nickname, out var kind)) pieces.Add(kind);
            else if (nickname == "" && piece["image"] is { } image) tokenImages.Add(names.TokenImages.GetValueOrDefault((string)image!) ?? $"? {image}");
            else if (!names.Unknown.Contains(nickname)) unmapped.Add($"{type} '{nickname}'");
        }
        Assert.Empty(unmapped);
        Assert.Equal(PantheonData.Divinities.Select(d => d.Id).Order(), divinities.Order());
        Assert.Equal(PantheonData.Temples.Select(t => t.Id).Order(), temples.Order());
        Assert.Equal(PantheonData.Wonders.Select(w => w.Id).Order(), wonders.Order());
        Assert.Equal(PantheonData.Tokens.Select(t => t.Id).Order(), tokens.Order());
        Assert.Equal(["minerva", "snake"], pieces.Order());
        // 10 Mythology tokens, 2 of each; the 3 Offering tokens.
        Assert.Equal(PantheonData.Mythologies.SelectMany(m => new[] { m.Id, m.Id }).Concat(PantheonData.Offerings.Select(o => o.Id)).Order(),
            tokenImages.Order());
        Assert.All(names.TokenImages.Values, slug => Assert.True(ExpectedTable.TokenCode(slug) >= 0));
        // The mod's names never resolve through the base tables (a base game never reads a Pantheon piece).
        Assert.All(names.Divinities.Keys.Concat(names.Temples.Keys).Concat(names.Wonders.Keys),
            n => Assert.True(TtsData.Names.Card(n) is null && TtsData.Names.Wonder(n) is null));
    }

    /// <summary>
    /// The backs snapshot.lua reports for Pantheon cards are what the mod's cards show: one back per Mythology deck (its 3
    /// Divinities share it, no two decks do), the Gate's own, and the Grand Temples' own.
    /// </summary>
    [Fact]
    public void The_reported_backs_are_the_mod_s_card_backs()
    {
        var backs = TtsData.PantheonLua;
        var byBack = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var card in ModObjects().SelectMany(WithContents).Where(c => (string)c["type"]! is "Card" or "CardCustom" && c["back"] is not null))
        {
            var nickname = (string)card["nickname"]!;
            if (Tags(card).Contains("wonder")) continue;
            var reported = TtsData.Names.Pantheon.Temples.ContainsKey(nickname) ? "temple"
                : PantheonData.Divinities[PantheonData.DivinityIndex(TtsData.Names.Pantheon.Divinities[nickname])].Mythology is { } m
                    ? PantheonData.MythologyDef(m).Id : "gate";
            Assert.Contains($"[\"{nickname}\"] = \"{reported}\"", backs);
            if (!byBack.TryGetValue((string)card["back"]!, out var set)) byBack[(string)card["back"]!] = set = [];
            set.Add(reported);
        }
        // Each real back image stands for exactly one reported back, and the 7 reported backs are 7 different images.
        Assert.All(byBack.Values, set => Assert.Single(set));
        Assert.Equal(7, byBack.Count);
    }

    /// <summary>The Pantheon spaces in tts/places.json are the mod board's snap points, placed as the Global script spawns it.</summary>
    [Fact]
    public void The_Pantheon_spaces_are_the_mod_board_s_snap_points()
    {
        var mod = Mod.Value;
        var board = mod["spawned"]!["pantheonBoard"]!;
        var cx = (double)mod["battleTrack"]!["x"]! + (double)board["offset"]!["x"]!;
        var cz = (double)mod["battleTrack"]!["z"]! + (double)board["offset"]!["z"]!;
        var scale = (double)board["scale"]!;
        var snaps = board["snapPoints"]!.AsArray().Select(p => ((double)p![0]!, (double)p[1]!)).ToList();
        var places = TtsData.Places;
        Assert.Equal(6, places.PantheonSpaces.Count);
        for (var i = 0; i < 6; i++)
        {
            // Turned 180 degrees: local +x is the players' left, local -z away from them.
            Assert.Equal(cx - scale * snaps[i].Item1, places.PantheonSpaces[i].X, 0.02);
            Assert.Equal(cz - scale * snaps[i].Item2, places.PantheonSpaces[i].Z, 0.02);
            Assert.True(places.PantheonArea.Contains(places.PantheonSpaces[i].X, places.PantheonSpaces[i].Z));
        }
        var area = mod["spawned"]!["divinityArea"]!;
        Assert.Equal(cz + (double)area["offsetZ"]! - (double)area["scaleZ"]! / 2, places.PantheonArea.Z1, 0.02);
        Assert.Equal(cz + (double)area["offsetZ"]! + (double)area["scaleZ"]! / 2, places.PantheonArea.Z2, 0.02);
        // The spaces are far enough apart for their snap distance.
        for (var i = 0; i < 5; i++)
            Assert.True(Math.Abs(places.PantheonSpaces[i + 1].X - places.PantheonSpaces[i].X) > 2 * places.PantheonSpaceSnap);
        // The mod's own setup scripts put the tokens on the R1 slots: Age I (-4.05, 8.42) ... and Age II (-7.99, 8.55) ...
        var age1 = new[] { (-4.0466, 8.421774), (3.992211, 8.473701), (-8.013044, 3.464347), (-0.018326, 3.513106), (8.035233, 3.48864) };
        var age2 = new[] { (-7.991212, 8.549131), (0.011301, 8.531464), (8.087815, 8.506706) };
        foreach (var (age, spots, slots) in new[] { (1, age1, PantheonData.MythologySlots), (2, age2, PantheonData.OfferingSlots) })
        {
            var objects = spots.Select((p, i) => new FakeObject("Tile", $"t{i}", "", p.Item1, 2.1, p.Item2, Image: TablePainter.TokenImage(i % 5))).ToList();
            var view = TableView.From(TableSnapshot.Parse(new FakeTable(objects, [], $"Age{age}").Snapshot()), pantheon: true);
            Assert.Equal(slots.Order(), view.At(Place.SlotToken).Select(p => p.Slot).Order());
        }
    }

    /// <summary>
    /// Random Pantheon tables: Divinities, the Gate and Grand Temples face up or down, in either player's hand or anywhere,
    /// tokens face up or down. A Divinity's name leaves TTS only face up outside a hand, or in the host's own hand; never
    /// in the other hand, and a Grand Temple never from a hand. Tokens are named only face up, outside hands.
    /// </summary>
    [Fact]
    public void Random_Pantheon_tables_never_name_a_hidden_piece()
    {
        var names = TtsData.Names.Pantheon;
        var cardNames = names.Divinities.Keys.Concat(names.Temples.Keys).ToArray();
        var hands = TablePainter.Hands;
        var spots = new (double X, double Z)[] { (-23.9, 28), (4, 37.4), (20, -46), (-20, -46), (23.6, -60), (-23.6, -60), (60, 10), (0, 5), (-50.6, 2.3), (8, -40) };
        var rng = new Random(13);
        var checkedNames = 0;
        for (var game = 0; game < 40; game++)
        {
            var host = game % 2 == 0 ? "White" : "Red";
            var objects = new List<FakeObject>();
            var allowed = new List<string>();
            for (var i = 0; i < 24; i++)
            {
                var (x, z) = spots[rng.Next(spots.Length)];
                x += rng.NextDouble() - 0.5;
                z += rng.NextDouble() - 0.5;
                var down = rng.Next(2) == 0;
                var guid = $"g{game}x{i}";
                var hand = hands.FirstOrDefault(h => x >= h.X1 && x <= h.X2 && z >= h.Z1 && z <= h.Z2)?.Color;
                if (rng.Next(3) == 0)
                {
                    var slug = names.TokenImages.Values.ElementAt(rng.Next(names.TokenImages.Count));
                    objects.Add(new FakeObject("Tile", guid, "", x, 1.8, z, down, Image: TablePainter.TokenImage(ExpectedTable.TokenCode(slug))));
                    if (!down && hand is null) allowed.Add(slug);
                    continue;
                }
                var name = cardNames[rng.Next(cardNames.Length)];
                var temple = names.Temples.ContainsKey(name);
                objects.Add(new FakeObject("Card", guid, name, x, 1.6, z, down, temple ? ["purple"] : ["rotate flip"]));
                var discard = TtsData.Places.Discard.Contains(x, z);
                if (hand is null ? !down || discard : hand == host && !temple) allowed.Add(name);
            }
            var snapshot = TableSnapshot.Parse(new FakeTable(objects, [(host, true)], "Age2", hands).Snapshot());
            Assert.Equal(objects.Count, snapshot.Pieces.Count);
            var sent = snapshot.Pieces.SelectMany(p => (p.Names ?? []).Append(p.Name)).OfType<string>().Order().ToList();
            Assert.Equal(allowed.Order().ToList(), sent);
            Assert.All(snapshot.Pieces.Where(p => p.Own), p => Assert.True(p.InHand && p.Name is not null));
            Assert.All(snapshot.Pieces.Where(p => p.Kind == PieceKind.Card), p => Assert.NotNull(p.Back));
            checkedNames += sent.Count;
        }
        Assert.True(checkedNames > 200);
    }

    // ---------------------------------------------------------------- following Pantheon games from the table

    static readonly Ruleset[] PantheonRules =
    [
        new(Expansions: Expansions.Pantheon),
        new(Promos: Promos.Messe | Promos.Liberty, Expansions: Expansions.Pantheon),
        new(StartChoice.Rule2015, Promos.Liberty, Expansions.Pantheon),
    ];

    /// <summary>
    /// A true Pantheon game (dealt by a seeded Chance Source, random legal Moves with activations favoured) and the log
    /// the owner keeps of it: each line as they see it, the opponent's private Reveals logged redacted by the session.
    /// </summary>
    sealed class TrueGame(Ruleset rules, int seat, ulong seed)
    {
        public readonly Game Game = new(rules);
        public readonly AdvisorSession Owner = AdvisorSession.New(rules, seat);
        readonly RandomChanceSource _chance = new(seed + 500);
        readonly Rng _rng = new(seed * 7919 + 11);

        /// <summary>Plays <paramref name="moves"/> Moves and every Reveal after them.</summary>
        public void Advance(int moves)
        {
            while (!Game.State.IsOver)
            {
                var s = Game.State;
                LogEntry entry;
                if (s.Step == StepKind.Reveal) entry = _chance.Next(s);
                else
                {
                    if (moves-- == 0) return;
                    var legal = s.LegalMoves();
                    var activations = legal.Where(m => m.Kind == MoveKind.Activate).ToList();
                    var move = activations.Count > 0 && _rng.Next(2) == 0 ? activations[_rng.Next(activations.Count)] : legal[_rng.Next(legal.Count)];
                    entry = new MoveEntry(s.PlayerToDecide, move);
                }
                var index = Game.Log.Count;
                Game.Apply(entry);
                if (Owner.Log.Count <= index) Owner.Enter(MoveLog.Format(MoveLog.AsSeenBy(entry, Owner.Seat)));
            }
        }
    }

    sealed class Tally
    {
        public int Appended, Choices, Waits, ByHand, Lagging;
        public readonly HashSet<MoveKind> Followed = [];
        public readonly HashSet<string> Reveals = [];
        public override string ToString() =>
            $"appended {Appended}, choices {Choices}, waits {Waits}, by hand {ByHand}, readings behind {Lagging}; Moves: {string.Join(' ', Followed.Order())}; Reveals: {string.Join(' ', Reveals.Order())}";
    }

    /// <summary>
    /// Reconciles the follower with the table until it matches, the way the Advisor server does: a single chain is
    /// entered; with several chains or a wait, the owner's answer comes from their own log of the true game (a choice the
    /// table can't show yet waits for the next reading); a Reveal the table can't show is entered by hand.
    /// </summary>
    static void Follow(AdvisorSession follower, AdvisorSession owner, SeatSides sides, TableSnapshot table, Tally tally)
    {
        var (log, states) = Canonical(owner);
        var real = states.Select(Convert.ToBase64String).ToList();
        int Here() => real.LastIndexOf(Convert.ToBase64String(follower.State.ToBytes()));
        bool IsNext(TableChain chain)
        {
            var copy = AdvisorSession.Load(follower.Write());
            copy.Enter(chain.Lines);
            return real.Contains(Convert.ToBase64String(copy.State.ToBytes()));
        }
        void Enter(TableChain chain)
        {
            follower.Enter(chain.Lines);
            foreach (var e in chain.Entries)
                if (e is MoveEntry m) tally.Followed.Add(m.Move.Kind);
                else tally.Reveals.Add(e.GetType().Name);
        }
        string Describe(Reconciliation r) =>
            $"{r.Status}: {r.Waiting} / Chains: {string.Join(" | ", r.Chains.Select(c => c.Text))} / Not on table: {string.Join("; ", r.NotOnTable)} / "
            + $"Not in log: {string.Join("; ", r.NotInLog)}\nFollower:\n{follower.Write()}\nOwner's log:\n{owner.Write()}";
        // A Reveal the table can't show (or a card flipped and taken before it was read), entered as the owner would.
        bool ByHand(Reconciliation r)
        {
            var here = Here();
            if (r.Waiting is null || here < 0 || here >= log.Count) return false;
            var next = log.Skip(here).ToList();
            if (r.Waiting.StartsWith("Name the card that was at", StringComparison.Ordinal))
            {
                var k = next.FindIndex(e => e is SlotReveal);
                follower.Enter(next.Take(k + 1).Select(MoveLog.Format).ToList());
                return true;
            }
            if (next[0] is not Reveal) return false;
            follower.Enter(MoveLog.Format(next[0]));
            tally.Reveals.Add("by hand: " + next[0].GetType().Name);
            return true;
        }

        for (var guard = 0; guard < 24; guard++)
        {
            Assert.True(Here() >= 0, $"off the game\n{follower.Write()}\n{owner.Write()}");
            var r = Reconciler.Reconcile(follower.State, follower.Seat, sides, table);
            switch (r.Status)
            {
                case SyncStatus.Matches:
                    if (Here() < log.Count && ByHand(r))
                    {
                        tally.Reveals.Add("by hand");
                        break;
                    }
                    return;
                case SyncStatus.OneChain:
                    Assert.True(IsNext(r.Chains[0]), $"appended '{r.Chains[0].Text}', but the game went otherwise. {Describe(r)}");
                    Enter(r.Chains[0]);
                    tally.Appended++;
                    break;
                case SyncStatus.SeveralChains:
                    var picks = r.Chains.Where(IsNext).ToList();
                    Assert.True(picks.Count <= 1, Describe(r));
                    if (picks.Count == 0) return; // the owner hasn't made that choice yet (the cards are still in hand)
                    Enter(picks[0]);
                    tally.Choices++;
                    break;
                case SyncStatus.Waiting:
                    // Prefixes that differ only in how flips were grouped reach the same state: any of them will do.
                    var pick = r.Chains.Where(IsNext).GroupBy(c =>
                    {
                        var copy = AdvisorSession.Load(follower.Write());
                        copy.Enter(c.Lines);
                        return Convert.ToBase64String(copy.State.ToBytes());
                    }).Select(g => g.First()).ToList();
                    if (pick.Count == 1)
                    {
                        Enter(pick[0]);
                        tally.Waits++;
                        break;
                    }
                    Assert.True(ByHand(r), Describe(r));
                    tally.ByHand++;
                    break;
                default:
                    Assert.Fail($"Desync. {Describe(r)}");
                    break;
            }
        }
        Assert.Fail("reconciliation did not settle");
    }

    /// <summary>
    /// The owner's log with their own Divinity draws and Divine Theater Reveals in the order a hand shows them (cards the
    /// log already knows at their places, the others in index order: a hand doesn't show which unknown card was on top,
    /// and it makes no difference once the card is placed or the deck restacked), and the states it goes through.
    /// </summary>
    static (List<LogEntry> Log, List<byte[]> States) Canonical(AdvisorSession owner)
    {
        var game = new Game(owner.Rules);
        var states = new List<byte[]> { game.State.ToBytes() };
        var log = new List<LogEntry>();
        int[] Order(Mythology m, int[] cards)
        {
            var p = game.State.Pantheon!;
            var rest = new SortedSet<int>(cards);
            var order = Enumerable.Range(0, cards.Length).Select(i => p.DeckCard(m, i)).ToArray();
            foreach (var d in order.Where(d => d >= 0)) rest.Remove(d);
            for (var i = 0; i < order.Length; i++)
                if (order[i] < 0) { order[i] = rest.Min; rest.Remove(order[i]); }
            return order;
        }
        foreach (var e in owner.Log)
        {
            var entry = e switch
            {
                DivinityDrawReveal { Redacted: false } d => d with { Divinities = Order(d.Mythology, d.Divinities) },
                TheaterReveal { Redacted: false } t => t with { Divinities = Order(t.Mythology, t.Divinities) },
                _ => e,
            };
            game.Apply(entry);
            log.Add(entry);
            states.Add(game.State.ToBytes());
        }
        return (log, states);
    }

    static void FollowGames(ulong first, int count, int maxStep, Tally tally)
    {
        for (var seed = first; seed < first + (ulong)count; seed++)
        {
            var rules = PantheonRules[seed % 3];
            var seat = (int)(seed % 2);
            var sides = SeatSides.ForOwner(seat, seed % 4 < 2 ? Side.White : Side.Red);
            (string, bool)[] host = [(sides.Of(seat).ToString(), true)];
            var game = new TrueGame(rules, seat, seed);
            var follower = AdvisorSession.New(rules, seat);
            var rng = new Rng(seed * 31 + 5);
            Assert.Equal(SyncStatus.Matches, Reconciler.Reconcile(follower.State, seat, sides, TablePainter.Snapshot(game.Game.State, sides, host)).Status);
            while (!game.Game.State.IsOver)
            {
                game.Advance(1 + rng.Next(maxStep));
                Follow(follower, game.Owner, sides, TablePainter.Snapshot(game.Game.State, sides, host), tally);
                if (!follower.State.ToBytes().AsSpan().SequenceEqual(game.Owner.State.ToBytes())) tally.Lagging++;
            }
            Assert.True(follower.State.IsOver, $"{follower.Write()}\n{game.Owner.Write()}");
            Assert.Equal(game.Owner.State.ToBytes(), follower.State.ToBytes());
            // What the owner ends with is what the true game's Observation shows them.
            Assert.Equal(game.Game.State.Observe(seat).State.ToBytes(), follower.State.Observe(seat).State.ToBytes());
        }
    }

    /// <summary>
    /// The heart of ticket 13: whole random Pantheon games (all promo settings, both seats, both colours, activations
    /// favoured) played on a table that moves 1 to 3 Moves at a time and is read through the real snapshot.lua. The
    /// Advisor follows from the table alone (plus what the table can never show, entered by hand) and ends with the
    /// owner's log of the game. Every new Pantheon Move and Reveal is followed from the table at least once.
    /// </summary>
    [Fact]
    public void Pantheon_games_are_followed_from_the_table()
    {
        var tally = new Tally();
        FollowGames(0, 12, 3, tally);
        FollowGames(100, 8, 1, tally); // one Move per reading: the table shows every step (the Gate's deck tops while it waits)
        MoveKind[] pantheonMoves =
        [
            MoveKind.Divinity, MoveKind.Activate, MoveKind.Offer, MoveKind.Enki, MoveKind.Snake, MoveKind.Steal, MoveKind.Hades,
            MoveKind.Zeus, MoveKind.Anubis, MoveKind.Isis, MoveKind.Ra, MoveKind.Gate, MoveKind.TheaterDeck, MoveKind.Theater,
            MoveKind.TheaterOrder, MoveKind.Minerva, MoveKind.NeptuneDiscard, MoveKind.NeptuneApply,
        ];
        Assert.True(!pantheonMoves.Except(tally.Followed).Any(), $"{tally}");
        string[] pantheonReveals =
        [
            nameof(MythologyTokensReveal), nameof(DivinityDrawReveal), nameof(PantheonReveal), nameof(OfferingsReveal), nameof(EnkiReveal),
            nameof(ZeusDiscardReveal), nameof(GateReveal), nameof(TheaterReveal), nameof(GuildPatternReveal),
        ];
        Assert.True(!pantheonReveals.Except(tally.Reveals).Any(), $"{tally}");
        Assert.True(tally.Appended > 300, $"{tally}");
    }

    // ---------------------------------------------------------------- a recorded snapshot, and the server

    /// <summary>The Pantheon game the recorded snapshot comes from: seed 1, the owner P1 at White, 50 Moves in (Age II).</summary>
    static (TrueGame Game, SeatSides Sides, (string, bool)[] Host) RecordedGame()
    {
        var game = new TrueGame(PantheonRules[1], 0, 1);
        for (var i = 0; i < 50; i++) game.Advance(1);
        return (game, SeatSides.ForOwner(0, Side.White), [("White", true)]);
    }

    /// <summary>
    /// A recorded Pantheon snapshot (tts/fixtures/pantheon-age2.snapshot.json: what snapshot.lua gives for an Age II table
    /// with the Pantheon face up, Offering tokens on the structure and held, activated Divinities, the Snake or Minerva), read place by
    /// place, and reconciled from the owner's log 8 Moves earlier: the Advisor catches up to the owner's log exactly.
    /// Set UPDATE_TTS_FIXTURE=1 to rewrite it after a deliberate change.
    /// </summary>
    [Fact]
    public void A_recorded_Pantheon_snapshot_is_read_and_reconciled()
    {
        var (game, sides, host) = RecordedGame();
        var text = TablePainter.Snapshot(game.Game.State, sides, host).Text;
        var path = FakeTable.RepoFile("tts/fixtures/pantheon-age2.snapshot.json");
        if (Environment.GetEnvironmentVariable("UPDATE_TTS_FIXTURE") == "1")
        {
            File.WriteAllText(path, text);
            File.WriteAllText(FakeTable.RepoFile("tts/fixtures/pantheon-age2.log"), game.Owner.Write());
        }
        Assert.Equal(File.ReadAllText(path), text);
        var owner = AdvisorSession.Load(File.ReadAllText(FakeTable.RepoFile("tts/fixtures/pantheon-age2.log")));
        Assert.Equal(game.Owner.State.ToBytes(), owner.State.ToBytes());

        var snapshot = TableSnapshot.Parse(File.ReadAllText(path));
        Assert.True(snapshot.HasPantheonPieces);
        var view = TableView.From(snapshot, 2, pantheon: true);
        var p = owner.State.Pantheon!;
        Assert.Equal(2, owner.State.Age);
        Assert.Equal(ExpectedTable.Facts(owner.State, sides), ExpectedTable.Facts(view, owner.State, sides));
        Assert.NotEmpty(view.At(Place.PantheonSpace));
        Assert.NotEmpty(view.At(Place.Activated));
        Assert.Equal(Enumerable.Range(0, GameState.SlotCount).Count(slot => owner.State.IsOccupied(slot) && p.SlotToken(slot) != PantheonState.NoToken),
            view.At(Place.SlotToken).Count());
        // A base game's reading of the same table places no Pantheon piece.
        Assert.DoesNotContain(TableView.From(snapshot, 2).Pieces, x => x.Place >= Place.PantheonSpace);

        var lines = owner.Write().Split('\n').ToList();
        var moves = lines.Select((l, i) => (l, i)).Where(x => x.l.StartsWith('P')).Select(x => x.i).ToList();
        var follower = AdvisorSession.Load(string.Join('\n', lines.Take(moves[^8])));
        var tally = new Tally();
        Follow(follower, owner, sides, snapshot, tally);
        Assert.Equal(owner.State.ToBytes(), follower.State.ToBytes());
        Assert.True(tally.Appended + tally.Choices > 0);
    }

    /// <summary>A table that answers whatever the test last laid out.</summary>
    sealed class FakeSource : ITableSource
    {
        public string? Text { get; set; }
        public Task<string> ReadAsync(CancellationToken cancel) =>
            Text is null ? throw new TtsException("Could not reach Tabletop Simulator (fake)") : Task.FromResult(Text);
        public void Dispose() { }
    }

    /// <summary>
    /// The Advisor server follows a Pantheon game from the table (the "pantheon" status is gone): Age I with its
    /// Mythology tokens, the owner's own draws read from their hand, the opponent's draws logged redacted by the session,
    /// one-click choices where the table can be read several ways.
    /// </summary>
    [Fact]
    public async Task The_Advisor_server_follows_a_Pantheon_game_from_the_table()
    {
        var table = new FakeSource();
        var app = AdvisorServer.Build(new AdvisorOptions(_folder, Port: 0, TableSource: () => table, TtsPolling: false));
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/") };
        var server = app.Services.GetRequiredService<AdvisorServer>();
        try
        {
            await http.PostAsJsonAsync("api/new", new { messe = false, liberty = false, rule2015 = false, seat = 0, pantheon = true });
            await http.PostAsJsonAsync("api/tts", new { follow = true });
            var sides = SeatSides.ForOwner(0, Side.White);
            var game = new TrueGame(new Ruleset(Expansions: Expansions.Pantheon), 0, 3);
            JsonElement tts = default;
            for (var step = 0; step < 40 && !game.Game.State.IsOver; step++)
            {
                game.Advance(1);
                table.Text = TablePainter.Snapshot(game.Game.State, sides).Text;
                await server.Tts.PollOnceAsync();
                await server.Tts.PollOnceAsync();
                var canonical = Canonical(game.Owner).Log.Select(MoveLog.Format).ToHashSet();
                for (var guard = 0; guard < 8; guard++)
                {
                    tts = (await ServerTests.Json(await http.GetAsync("api/state"))).GetProperty("tts");
                    var status = tts.GetProperty("status").GetString();
                    Assert.True(status is "matches" or "choices" or "waiting", $"{status}: {tts}");
                    if (status == "matches") break;
                    // A choice or a wait: the owner clicks what happened, or enters what the table can't show.
                    var choice = tts.GetProperty("choices").EnumerateArray()
                        .FirstOrDefault(c => c.GetProperty("lines").EnumerateArray().All(l => canonical.Contains(l.GetString()!)));
                    if (choice.ValueKind == JsonValueKind.Object)
                        await http.PostAsJsonAsync("api/tts/choose", new { index = choice.GetProperty("index").GetInt32() });
                    else if (status == "waiting")
                    {
                        var log = (await ServerTests.Json(await http.GetAsync("api/state"))).GetProperty("game").GetProperty("log").GetArrayLength();
                        await http.PostAsJsonAsync("api/entry", new { line = MoveLog.Format(Canonical(game.Owner).Log[log]) });
                    }
                    else break; // a choice the owner hasn't made yet at the table
                }
            }
            var lines = (await ServerTests.Json(await http.GetAsync("api/state"))).GetProperty("game").GetProperty("log").EnumerateArray()
                .Select(l => l.GetProperty("text").GetString()!).ToList();
            Assert.Contains(lines, l => l.StartsWith("reveal myth-tokens ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("reveal P2 divinities ", StringComparison.Ordinal) && l.EndsWith(" ? ?", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("P2 divinity ? ", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("reveal P1 divinities ", StringComparison.Ordinal) && !l.Contains('?'));
            Assert.Contains(lines, l => l.StartsWith("P1 divinity ", StringComparison.Ordinal));
            Assert.Contains("divinity ?", File.ReadAllText(Path.Combine(_folder, "current.log")));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
