using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>
/// Encoding v2 and its manifest (spec-pantheon P-A7, ticket 09): Encoding v1 unchanged, then appended Pantheon blocks.
/// Encoding v1's own tests (<see cref="EncodingTests"/>) are untouched.
/// </summary>
public class EncodingV2Tests
{
    const Promos BothPromos = Promos.Messe | Promos.Liberty;
    static readonly int V1Input = NetworkEncoding.InputSize, V1Policy = NetworkEncoding.PolicySize;

    /// <summary>Seeded random games (promos every other game) calling <paramref name="visit"/> at every decision.</summary>
    static void RandomDecisions(int games, ulong seed, Expansions expansions, Action<GameState> visit)
    {
        var rng = new Rng(seed);
        for (var game = 0; game < games; game++)
        {
            var rules = new Ruleset(game % 4 >= 2 ? StartChoice.Rule2015 : StartChoice.New,
                game % 2 == 0 ? Promos.None : (Promos)(1 + game / 2 % 3), expansions);
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(rules);
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    s.Apply(chance.Next(s));
                    continue;
                }
                visit(s);
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
    }

    static float At(float[] input, string name)
    {
        var i = Array.IndexOf(NetworkEncodingV2.InputNames, name);
        Assert.True(i >= 0, $"no input named {name}");
        return input[i];
    }

    // ---------------------------------------------------------------- layout and policy

    [Fact]
    public void V2_starts_with_every_v1_block_and_name_unchanged_and_its_blocks_tile()
    {
        Assert.Equal(NetworkEncoding.InputBlocks, NetworkEncodingV2.InputBlocks[..NetworkEncoding.InputBlocks.Length]);
        Assert.Equal(NetworkEncoding.PolicyBlocks, NetworkEncodingV2.PolicyBlocks[..NetworkEncoding.PolicyBlocks.Length]);
        Assert.Equal(NetworkEncoding.InputNames, NetworkEncodingV2.InputNames[..V1Input]);
        Assert.Equal(NetworkEncoding.PolicyNames, NetworkEncodingV2.PolicyNames[..V1Policy]);
        Assert.Equal(3206, NetworkEncodingV2.InputSize);
        Assert.Equal(3420, NetworkEncodingV2.PolicySize);
        foreach (var (blocks, names, size) in new[]
        {
            (NetworkEncodingV2.InputBlocks, NetworkEncodingV2.InputNames, NetworkEncodingV2.InputSize),
            (NetworkEncodingV2.PolicyBlocks, NetworkEncodingV2.PolicyNames, NetworkEncodingV2.PolicySize),
        })
        {
            var at = 0;
            foreach (var b in blocks)
            {
                Assert.Equal(at, b.Offset);
                at += b.Size;
            }
            Assert.Equal(size, at);
            Assert.Equal(size, names.Length);
            Assert.Equal(size, names.Distinct().Count());
            Assert.Equal(blocks.Length, blocks.Select(b => b.Name).Distinct().Count());
        }
    }

    [Fact]
    public void Every_v2_policy_index_maps_to_a_Move_and_back_and_is_named_as_the_Move_Log_writes_it()
    {
        for (var i = 0; i < NetworkEncodingV2.PolicySize; i++)
        {
            var move = NetworkEncodingV2.MoveAt(i);
            Assert.Equal(i, NetworkEncodingV2.PolicyIndex(move));
            var name = move.Kind == MoveKind.Zeus
                ? $"zeus age{move.Wonder} {MoveLog.FormatMove(move)["zeus ".Length..]}" // slot names repeat between Ages
                : MoveLog.FormatMove(move);
            Assert.Equal(name, NetworkEncodingV2.PolicyNames[i]);
            if (i < V1Policy) Assert.Equal(NetworkEncoding.MoveAt(i), move);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkEncodingV2.MoveAt(NetworkEncodingV2.PolicySize));
        Assert.Throws<ArgumentException>(() => NetworkEncodingV2.PolicyIndex(Move.Destroy(GameData.CardIndex("baths"))));
        // v1 has no index for an expansion Move, rather than a wrong one.
        var temple = PantheonData.Temples[0].Card;
        Assert.Throws<ArgumentException>(() => NetworkEncoding.PolicyIndex(Move.Build(temple)));
        Assert.Throws<ArgumentException>(() => NetworkEncoding.PolicyIndex(Move.Draft(PantheonData.WonderIndex("sanctuary"))));
        Assert.Throws<ArgumentException>(() => NetworkEncoding.PolicyIndex(Move.Activate(PantheonData.Gate)));
        Assert.True(NetworkEncodingV2.PolicyIndex(Move.Build(temple)) >= V1Policy);
    }

    // ---------------------------------------------------------------- base games: exactly v1

    /// <summary>
    /// In every base-game position (promos included) the first v1-size inputs are exactly v1's input and every
    /// Pantheon input is 0, every legal Move has v1's index, and the mask has nothing past v1. (The soak checks the
    /// same at every decision of 100k games.)
    /// </summary>
    [Fact]
    public void In_a_base_game_v2_is_v1_followed_by_zeros_with_v1s_policy_indices()
    {
        var v2 = new float[NetworkEncodingV2.InputSize];
        var mask = new bool[NetworkEncodingV2.PolicySize];
        var positions = 0;
        RandomDecisions(200, 9, Expansions.None, s =>
        {
            foreach (var player in new[] { 0, 1 })
            {
                var observation = s.Observe(player);
                NetworkEncodingV2.Encode(observation, v2);
                Assert.Equal(NetworkEncoding.Encode(observation), v2[..V1Input]);
                Assert.All(v2[V1Input..], v => Assert.Equal(0, v));
            }
            var mine = s.Observe(s.PlayerToDecide);
            var moves = mine.LegalMoves();
            foreach (var m in moves) Assert.Equal(NetworkEncoding.PolicyIndex(m), NetworkEncodingV2.PolicyIndex(m));
            Assert.Equal(moves.Count, NetworkEncodingV2.LegalMask(mine, mask));
            Assert.DoesNotContain(true, mask[V1Policy..]);
            positions++;
        });
        Assert.True(positions > 10_000);
    }

    // ---------------------------------------------------------------- Pantheon games

    [Fact]
    public void Every_legal_Pantheon_Move_maps_to_a_unique_policy_index_and_back_and_the_mask_holds_exactly_the_legal_Moves()
    {
        var mask = new bool[NetworkEncodingV2.PolicySize];
        var kinds = new HashSet<MoveKind>();
        var appended = new HashSet<string>();
        RandomDecisions(600, 21, Expansions.Pantheon, s =>
        {
            var observation = s.Observe(s.PlayerToDecide);
            var moves = observation.LegalMoves();
            var indices = moves.Select(NetworkEncodingV2.PolicyIndex).ToList();
            Assert.Equal(indices.Count, indices.Distinct().Count());
            for (var k = 0; k < moves.Count; k++)
            {
                Assert.Equal(moves[k], NetworkEncodingV2.MoveAt(indices[k]));
                kinds.Add(moves[k].Kind);
                if (indices[k] >= V1Policy) appended.Add(NetworkEncodingV2.PolicyBlocks.Single(b => indices[k] >= b.Offset && indices[k] < b.Offset + b.Size).Name);
            }
            Assert.Equal(moves.Count, NetworkEncodingV2.LegalMask(observation, mask));
            Assert.Equal(moves.Count, mask.Count(m => m));
            foreach (var i in indices) Assert.True(mask[i]);
        });
        Assert.Equal(Enum.GetValues<MoveKind>().Length, kinds.Count); // every Move kind occurred, base and Pantheon
        foreach (var block in new[] { "build-temple", "discard-temple", "wonder-pantheon", "draft-pantheon", "token-pantheon", "divinity", "isis", "zeus" })
            Assert.Contains(block, appended);
    }

    [Fact]
    public void Pantheon_input_is_fully_written_and_roughly_zero_to_one_and_every_block_carries_information()
    {
        var input = new float[NetworkEncodingV2.InputSize + 3];
        var max = new float[NetworkEncodingV2.InputSize];
        RandomDecisions(300, 5, Expansions.Pantheon, s =>
        {
            foreach (var player in new[] { 0, 1 })
            {
                input.AsSpan().Fill(float.NaN);
                NetworkEncodingV2.Encode(s.Observe(player), input);
                for (var i = 0; i < NetworkEncodingV2.InputSize; i++)
                {
                    var v = input[i];
                    Assert.True(float.IsFinite(v) && v >= -1 && v <= 2.5f, $"{NetworkEncodingV2.InputNames[i]} = {v}");
                    max[i] = Math.Max(max[i], Math.Abs(v));
                }
                Assert.True(float.IsNaN(input[NetworkEncodingV2.InputSize]), "wrote past the input");
            }
        });
        foreach (var block in NetworkEncodingV2.InputBlocks)
            Assert.True(max.AsSpan(block.Offset, block.Size).ToArray().Any(v => v > 0), block.Name);
    }

    /// <summary>
    /// No peeking through Encoding v2 (spec-pantheon P-A5): two games that differ only in a Divinity the opponent drew,
    /// placed face down or put back on its deck give the viewer the same input, while the drawer's differs (the test
    /// bites).
    /// </summary>
    [Fact]
    public void Input_is_unchanged_when_only_hidden_Pantheon_cards_change()
    {
        foreach (var (drawA, placeA, drawB, placeB) in new[]
        {
            ("anubis isis", (string?)null, "ra isis", (string?)null), // P2 is placing their draw
            ("anubis isis", "isis", "ra anubis", "anubis"),          // placed face down, the other card put back
            ("anubis isis", "isis", "anubis isis", "anubis"),        // the same draw, the other card placed
        })
        {
            var a = PantheonPositions.EgyptianDraw(drawA, placeA).State;
            var b = PantheonPositions.EgyptianDraw(drawB, placeB).State;
            Assert.Equal(NetworkEncodingV2.Encode(a.Observe(0)), NetworkEncodingV2.Encode(b.Observe(0)));
            Assert.NotEqual(NetworkEncodingV2.Encode(a.Observe(1)), NetworkEncodingV2.Encode(b.Observe(1)));
        }
    }

    /// <summary>Any world the Determinizer samples from a Pantheon Observation encodes exactly like the Observation.</summary>
    [Fact]
    public void Sampled_Pantheon_worlds_encode_like_the_Observation_they_came_from()
    {
        var determinizer = new Determinizer(3);
        var hidden = 0;
        RandomDecisions(150, 11, Expansions.Pantheon, s =>
        {
            foreach (var player in new[] { 0, 1 })
            {
                var observation = s.Observe(player);
                var p = observation.State.Pantheon!;
                if (p.DrawHidden || Enumerable.Range(0, PantheonData.SpaceCount).Any(p.IsHidden)) hidden++;
                var expected = NetworkEncodingV2.Encode(observation);
                for (var k = 0; k < 2; k++)
                    Assert.Equal(expected, NetworkEncodingV2.Encode(determinizer.Sample(observation).Observe(player)));
            }
        });
        Assert.True(hidden > 100, $"only {hidden} Observations with a hidden Divinity");
    }

    [Fact]
    public void An_Age_I_placement_reads_as_expected_from_both_seats()
    {
        // P2 drew anubis and isis, placed isis face down on space 4 and put anubis back on top of the Egyptian deck.
        var s = PantheonPositions.EgyptianDraw("anubis isis", "isis").State;
        var p1 = NetworkEncodingV2.Encode(s.Observe(0));
        var p2 = NetworkEncodingV2.Encode(s.Observe(1));

        // Public: the space's Mythology, who placed it, the costs, who knows the deck top; the Mythology tokens left.
        foreach (var (input, placer) in new[] { (p1, "opp"), (p2, "me") })
        {
            Assert.Equal(1, At(input, "pantheon-spaces.space4.face-down"));
            Assert.Equal(1, At(input, "pantheon-spaces.space4.mythology.egyptian"));
            Assert.Equal(1, At(input, $"pantheon-spaces.space4.placed-by-{placer}"));
            Assert.Equal(1, At(input, "pantheon-spaces.space1.empty"));
            Assert.Equal(2 / 3f, At(input, "decks.egyptian.size"));
            Assert.Equal(1, At(input, "decks.greek.size"));
            // 1.0, 1.2, 3.2 and 3.4 (slots 2, 4, 11, 13) still hold their tokens; 3.0 (slot 9) was flipped and its token taken.
            Assert.Equal(1, At(input, "slot-tokens.slot02.greek"));
            Assert.Equal(1, At(input, "slot-tokens.slot13.greek"));
            Assert.Equal(1, At(input, "slot-tokens.slot11.phoenician"));
            Assert.Equal(0, At(input, "slot-tokens.slot09.egyptian"));
        }
        Assert.Equal(1, At(p1, "pantheon-held.opp-mythology.egyptian") * 2);
        Assert.Equal(1, At(p2, "pantheon-held.my-mythology.egyptian") * 2);
        Assert.Equal(1, At(p1, "decks.egyptian.pos1.known-to-opp"));
        Assert.Equal(1, At(p2, "decks.egyptian.pos1.known-to-me"));
        // P1's costs rise 3..8 along the board, P2's fall 8..3: space 4 is 6 for P1 and 5 for P2.
        Assert.Equal(6 / 8f, At(p1, "pantheon-spaces.space4.my-cost"));
        Assert.Equal(5 / 8f, At(p1, "pantheon-spaces.space4.opp-cost"));
        Assert.Equal(5 / 8f, At(p2, "pantheon-spaces.space4.my-cost"));

        // Private: P2 knows the cards, P1 only that the space is hidden.
        Assert.Equal(1, At(p1, "pantheon-spaces.space4.hidden"));
        Assert.Equal(0, At(p2, "pantheon-spaces.space4.hidden"));
        Assert.Equal(1, At(p2, "divinities.isis.space4"));
        Assert.Equal(1, At(p2, "divinities.isis.face-down"));
        Assert.Equal(1, At(p2, "divinities.anubis.deck1"));
        Assert.Equal(1, At(p2, "divinities.ra.unseen"));
        Assert.Equal(0, At(p2, "divinities.isis.unseen"));
        foreach (var god in new[] { "anubis", "isis", "ra" })
        {
            Assert.Equal(1, At(p1, $"divinities.{god}.unseen"));
            Assert.Equal(0, At(p1, $"divinities.{god}.space4") + At(p1, $"divinities.{god}.deck1"));
        }
    }

    [Fact]
    public void Placing_a_draw_shows_the_decision_and_the_drawers_own_cards_only()
    {
        var s = PantheonPositions.EgyptianDraw("anubis isis", null).State;
        var p1 = NetworkEncodingV2.Encode(s.Observe(0));
        var p2 = NetworkEncodingV2.Encode(s.Observe(1));
        foreach (var input in new[] { p1, p2 })
        {
            Assert.Equal(1, At(input, "pantheon-turn.decision.place-divinity"));
            Assert.Equal(1, At(input, "pantheon-turn.mythology.egyptian"));
            Assert.Equal(0, At(input, "turn.decision.take-card"));
        }
        Assert.Equal(1, At(p1, "pantheon-turn.draw-hidden"));
        Assert.Equal(0, At(p2, "pantheon-turn.draw-hidden"));
        Assert.Equal(1, At(p2, "divinities.anubis.my-draw"));
        Assert.Equal(1, At(p2, "divinities.isis.my-draw"));
        Assert.Equal(0, At(p1, "divinities.anubis.my-draw"));
    }

    // ---------------------------------------------------------------- versions

    [Fact]
    public void A_version_supports_the_base_game_always_and_Pantheon_from_v2()
    {
        var pantheon = new Ruleset(Expansions: Expansions.Pantheon);
        var withPromos = new Ruleset(Promos: BothPromos);
        Assert.Same(EncodingVersion.V1, EncodingVersion.For(withPromos));
        Assert.Same(EncodingVersion.V2, EncodingVersion.For(pantheon));
        Assert.Same(EncodingVersion.V2, EncodingVersion.Of(2));
        Assert.Null(EncodingVersion.TryOf(3));
        Assert.True(EncodingVersion.V1.Supports(withPromos) && EncodingVersion.V2.Supports(withPromos));
        Assert.True(EncodingVersion.V2.Supports(pantheon));
        Assert.False(EncodingVersion.V1.Supports(pantheon));
        var e = Assert.Throws<NotSupportedException>(() => EncodingVersion.V1.CheckSupports(pantheon));
        Assert.Contains("Encoding v1", e.Message);
        Assert.Contains("Encoding v2", e.Message);
        Assert.Equal(NetworkEncoding.InputSize, EncodingVersion.V1.InputSize);
        Assert.Equal(NetworkEncodingV2.PolicySize, EncodingVersion.V2.PolicySize);
    }

    // ---------------------------------------------------------------- manifest

    /// <summary>
    /// The committed v2 manifest equals the generated one. If this fails after a deliberate change: regenerate it with
    /// <c>7wd manifest --version 2 --out data/encoding/v2.json</c> (and v1's test must still pass untouched).
    /// </summary>
    [Fact]
    public void The_committed_v2_manifest_matches_the_generated_one()
    {
        var committed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "encoding", "v2.json")).Replace("\r\n", "\n");
        var generated = EncodingManifest.Generate(EncodingVersion.V2);
        Assert.True(committed == generated,
            "data/encoding/v2.json differs from Encoding v2 in code. Regenerate it with "
            + "`dotnet run --project engine/src/Cli -- manifest --version 2 --out data/encoding/v2.json`.");
        Assert.Equal(EncodingManifest.Generate(), EncodingManifest.Generate(EncodingVersion.V1));
    }

    [Fact]
    public void The_v2_manifest_is_valid_json_with_the_sizes_Python_checks()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(EncodingManifest.Generate(EncodingVersion.V2));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(NetworkEncodingV2.InputSize, root.GetProperty("input").GetProperty("size").GetInt32());
        Assert.Equal(NetworkEncodingV2.PolicySize, root.GetProperty("policy").GetProperty("names").GetArrayLength());
        Assert.Equal(NetworkEncodingV2.PolicySize, root.GetProperty("heads").GetProperty("policy").GetProperty("size").GetInt32());
        Assert.Equal("divinity zeus 3",
            root.GetProperty("policy").GetProperty("names")[NetworkEncodingV2.PolicyIndex(
                Move.PlaceDivinity(PantheonData.DivinityIndex("zeus"), 2))].GetString());
        Assert.Equal(7, root.GetProperty("heads").GetProperty("outcome").GetProperty("classes").GetArrayLength());
    }
}

/// <summary>Pantheon positions from text shared by the Encoding tests.</summary>
internal static class PantheonPositions
{
    const string Opening =
        "reveal age1 0.0 palisade 0.1 tavern 2.0 guard-tower 2.1 altar 2.2 lumber-yard 2.3 press "
        + "4.0 apothecary 4.1 baths 4.2 logging-camp 4.3 quarry 4.4 glassworks 4.5 stone-reserve";
    const string MythTokens = "reveal myth-tokens 1.0 greek 1.2 roman 3.0 egyptian 3.2 phoenician 3.4 greek";

    /// <summary>
    /// P2 takes 4.1, flipping 3.0 with the Egyptian token, draws <paramref name="draw"/> and (if
    /// <paramref name="place"/>) places it face down on space 4; the other card goes back on top, known to P2 only.
    /// </summary>
    internal static Game EgyptianDraw(string draw, string? place)
    {
        string[] lines = ["7wd-log v1", "rules base expansion=pantheon", "reveal first P1",
            "reveal tokens agriculture economy law strategy urbanism",
            .. Setup.Draft(0), Opening, MythTokens, "P1 discard apothecary", "P2 discard baths", "reveal 3.0 stable",
            $"reveal P2 divinities egyptian {draw}", .. place is null ? Array.Empty<string>() : [$"P2 divinity {place} 4"]];
        return MoveLog.Parse(string.Join('\n', lines));
    }
}
