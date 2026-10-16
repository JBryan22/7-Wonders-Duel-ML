using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine.Tests;

/// <summary>Encoding v1 and its manifest (spec A6, P6 validation).</summary>
public class EncodingTests
{
    const Promos BothPromos = Promos.Messe | Promos.Liberty;

    /// <summary>
    /// Plays seeded random games (alternating base / both promos / each start rule) and calls <paramref name="visit"/>
    /// with the true state at every decision.
    /// </summary>
    static void RandomDecisions(int games, ulong seed, Action<GameState> visit, Action<GameState>? beforeReveal = null)
    {
        var rng = new Rng(seed);
        for (var game = 0; game < games; game++)
        {
            var rules = new Ruleset(game % 4 >= 2 ? StartChoice.Rule2015 : StartChoice.New, game % 2 == 0 ? Promos.None : BothPromos);
            var chance = new RandomChanceSource(rng.NextUInt64());
            var s = GameState.New(rules);
            while (!s.IsOver)
            {
                if (s.Step == StepKind.Reveal)
                {
                    beforeReveal?.Invoke(s);
                    s.Apply(chance.Next(s));
                    continue;
                }
                visit(s);
                var moves = s.LegalMoves();
                s.Apply(moves[rng.Next(moves.Count)]);
            }
        }
    }

    // ---------------------------------------------------------------- policy

    [Fact]
    public void Every_policy_index_maps_to_a_Move_and_back_and_is_named_as_the_Move_Log_writes_it()
    {
        Assert.Equal(1509, NetworkEncoding.PolicySize);
        for (var i = 0; i < NetworkEncoding.PolicySize; i++)
        {
            var move = NetworkEncoding.MoveAt(i);
            Assert.Equal(i, NetworkEncoding.PolicyIndex(move));
            Assert.Equal(MoveLog.FormatMove(move), NetworkEncoding.PolicyNames[i]);
        }
        Assert.Equal(NetworkEncoding.PolicySize, NetworkEncoding.PolicyNames.Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkEncoding.MoveAt(NetworkEncoding.PolicySize));
        Assert.Throws<ArgumentException>(() => NetworkEncoding.PolicyIndex(Move.Destroy(GameData.CardIndex("baths"))));
    }

    [Fact]
    public void Every_legal_Move_maps_to_a_unique_policy_index_and_back_and_the_mask_holds_exactly_the_legal_Moves()
    {
        var mask = new bool[NetworkEncoding.PolicySize];
        var kinds = new HashSet<MoveKind>();
        var positions = 0;
        RandomDecisions(300, 18, s =>
        {
            var observation = s.Observe(s.PlayerToDecide);
            var moves = observation.LegalMoves();
            var indices = moves.Select(NetworkEncoding.PolicyIndex).ToList();
            Assert.Equal(indices.Count, indices.Distinct().Count());
            for (var k = 0; k < moves.Count; k++)
            {
                Assert.Equal(moves[k], NetworkEncoding.MoveAt(indices[k]));
                kinds.Add(moves[k].Kind);
            }
            Assert.Equal(moves.Count, NetworkEncoding.LegalMask(observation, mask));
            Assert.Equal(moves.Count, mask.Count(m => m));
            foreach (var i in indices) Assert.True(mask[i]);
            positions++;
        });
        Assert.True(positions > 10_000);
        Assert.Equal((int)MoveKind.LibertyGive + 1, kinds.Count); // every base Move kind occurred, promos included
    }

    // ---------------------------------------------------------------- input

    [Fact]
    public void Input_is_fully_written_and_roughly_zero_to_one_at_every_decision()
    {
        var input = new float[NetworkEncoding.InputSize + 3];
        var max = new float[NetworkEncoding.InputSize];
        RandomDecisions(200, 5, s =>
        {
            input.AsSpan().Fill(float.NaN);
            NetworkEncoding.Encode(s.Observe(s.PlayerToDecide), input);
            for (var i = 0; i < NetworkEncoding.InputSize; i++)
            {
                var v = input[i];
                Assert.True(float.IsFinite(v) && v >= -1 && v <= 2.5f, $"{NetworkEncoding.InputNames[i]} = {v}");
                max[i] = Math.Max(max[i], Math.Abs(v));
            }
            Assert.True(float.IsNaN(input[NetworkEncoding.InputSize]), "wrote past the input");
        });
        // Every block carries information somewhere (no block stuck at zero).
        foreach (var block in NetworkEncoding.InputBlocks)
            Assert.True(max.AsSpan(block.Offset, block.Size).ToArray().Any(v => v > 0), block.Name);
    }

    /// <summary>
    /// No peeking through the Encoding (spec P6): the input never changes when only hidden cards change. The Game
    /// State holds no hidden identity except the Great Library's private draw (ADR 0002), so random games are stopped
    /// at each Great Library draw, the draw is made two different ways, and the opponent's input must be identical
    /// while the drawer's differs (the test bites).
    /// </summary>
    [Fact]
    public void Input_is_unchanged_when_only_hidden_cards_change()
    {
        var draws = 0;
        RandomDecisions(400, 7, _ => { }, s =>
        {
            if (s.SlotsToReveal != 0 || !s.AwaitingLibraryDraw) return;
            var drawer = s.PlayerToDecide;
            var box = ProgressTokens.In(s.TokensSetAside).ToArray();
            var a = s.Clone();
            a.Apply(new LibraryReveal(drawer, box[..3]));
            var b = s.Clone();
            b.Apply(new LibraryReveal(drawer, box[^3..]));
            Assert.Equal(NetworkEncoding.Encode(a.Observe(1 - drawer)), NetworkEncoding.Encode(b.Observe(1 - drawer)));
            Assert.NotEqual(NetworkEncoding.Encode(a.Observe(drawer)), NetworkEncoding.Encode(b.Observe(drawer)));
            draws++;
        });
        Assert.True(draws >= 8, $"only {draws} Great Library draws");
    }

    /// <summary>Any world the Determinizer samples from an Observation encodes exactly like the Observation.</summary>
    [Fact]
    public void Sampled_worlds_encode_like_the_Observation_they_came_from()
    {
        var determinizer = new Determinizer(3);
        var hiddenDraws = 0;
        RandomDecisions(150, 11, s =>
        {
            foreach (var player in new[] { 0, 1 })
            {
                var observation = s.Observe(player);
                if (observation.State.LibraryDrawHidden) hiddenDraws++;
                var expected = NetworkEncoding.Encode(observation);
                for (var k = 0; k < 2; k++)
                    Assert.Equal(expected, NetworkEncoding.Encode(determinizer.Sample(observation).Observe(player)));
            }
        });
        Assert.True(hiddenDraws > 0);
    }

    [Fact]
    public void The_opening_of_Age_I_reads_as_expected_from_both_seats()
    {
        GameState? opening = null;
        RandomDecisions(1, 2, s => opening ??= s.Age == 1 && s.AgeUntouched ? s.Clone() : null);
        var s = opening!;
        var me = s.PlayerToDecide;
        var input = NetworkEncoding.Encode(s.Observe(me));
        float At(string name) => input[Array.IndexOf(NetworkEncoding.InputNames, name)];

        Assert.Equal(1, At("age.1"));
        Assert.Equal(0, At("age.2"));
        // Age I: 20 cards, the bottom row of 6 accessible, rows 0, 2 and 4 face up (2 + 4 + 6 = 12), the rest face down.
        var flags = Enumerable.Range(0, GameState.SlotCount).Select(slot => (
            Up: At($"layout.slot{slot:00}.face-up"), Back: At($"layout.slot{slot:00}.back-age"),
            Open: At($"layout.slot{slot:00}.accessible"))).ToList();
        Assert.Equal(12, flags.Count(f => f.Up == 1));
        Assert.Equal(8, flags.Count(f => f.Back == 1));
        Assert.Equal(6, flags.Count(f => f.Open == 1));
        for (var slot = 0; slot < GameState.SlotCount; slot++)
        {
            var card = s.SlotCard(slot);
            var oneHot = Enumerable.Range(0, GameData.Cards.Length).Count(c => At($"layout.slot{slot:00}.card.{GameData.Cards[c].Id}") == 1);
            Assert.Equal(card >= 0 ? 1 : 0, oneHot);
            if (card >= 0) Assert.Equal(1, At($"layout.slot{slot:00}.card.{GameData.Cards[card].Id}"));
        }
        Assert.Equal(7 / 20f, At("scalars.my-coins"));
        Assert.Equal(1, At("scalars.cards-left-in-age"));
        Assert.Equal(1, At("turn.decision.take-card"));
        Assert.Equal(1, At("turn.me-to-decide"));
        Assert.Equal(1, At("turn.my-turn"));
        Assert.Equal(1, At("derived.me.capital-distance") * 2); // 9 / 18

        // Each player's own Wonders are "mine" in their own input and "opp" in the other's.
        var mine = NetworkEncoding.Encode(s.Observe(me));
        var theirs = NetworkEncoding.Encode(s.Observe(1 - me));
        foreach (var w in GameState.WondersIn(s.Wonders(me)))
        {
            var id = GameData.Wonders[w].Id;
            Assert.Equal(1, mine[Array.IndexOf(NetworkEncoding.InputNames, $"wonders.my-unbuilt.{id}")]);
            Assert.Equal(1, theirs[Array.IndexOf(NetworkEncoding.InputNames, $"wonders.opp-unbuilt.{id}")]);
        }
        Assert.Equal(0, theirs[Array.IndexOf(NetworkEncoding.InputNames, "turn.me-to-decide")]);
    }

    [Fact]
    public void Unseen_cards_shrink_as_cards_are_revealed()
    {
        var s = GameState.New(Ruleset.Default);
        var input = NetworkEncoding.Encode(s.Observe(0));
        var unseen = NetworkEncoding.InputBlocks.Single(b => b.Name == "unseen-cards");
        Assert.Equal(GameData.Cards.Length, input.AsSpan(unseen.Offset, unseen.Size).ToArray().Count(v => v == 1));
        var unseenWonders = NetworkEncoding.InputBlocks.Single(b => b.Name == "unseen-wonders");
        Assert.Equal(12, input.AsSpan(unseenWonders.Offset, unseenWonders.Size).ToArray().Count(v => v == 1));

        RandomDecisions(1, 4, st =>
        {
            if (st.Age != 2) return;
            var x = NetworkEncoding.Encode(st.Observe(st.PlayerToDecide));
            var names = Enumerable.Range(unseen.Offset, unseen.Size).Where(i => x[i] == 1)
                .Select(i => NetworkEncoding.InputNames[i]["unseen-cards.".Length..]).ToList();
            Assert.DoesNotContain(names, n => GameData.Cards[GameData.CardIndex(n)].Age == 1 && !GameData.Cards[GameData.CardIndex(n)].IsGuild);
            Assert.Contains(names, n => GameData.Cards[GameData.CardIndex(n)].IsGuild);
        });
    }

    [Fact]
    public void Outcome_classes_and_value_targets_are_from_the_players_point_of_view()
    {
        var sheet = new ScoreSheet(new int[ScoreSheet.CategoryCount]);
        var science = new Result(1, VictoryType.Science, sheet, sheet);
        Assert.Equal("loss-science", NetworkEncoding.OutcomeClasses[NetworkEncoding.OutcomeClass(science, 0)]);
        Assert.Equal("win-science", NetworkEncoding.OutcomeClasses[NetworkEncoding.OutcomeClass(science, 1)]);
        Assert.Equal(-1, NetworkEncoding.ValueTarget(science, 0));
        Assert.Equal(1, NetworkEncoding.ValueTarget(science, 1));
        var draw = new Result(-1, VictoryType.Civilian, sheet, sheet);
        Assert.Equal("draw", NetworkEncoding.OutcomeClasses[NetworkEncoding.OutcomeClass(draw, 0)]);
        Assert.Equal(0, NetworkEncoding.ValueTarget(draw, 1));
    }

    // ---------------------------------------------------------------- manifest

    [Fact]
    public void Blocks_tile_the_input_and_policy_and_every_index_has_a_unique_name()
    {
        foreach (var (blocks, names, size) in new[]
        {
            (NetworkEncoding.InputBlocks, NetworkEncoding.InputNames, NetworkEncoding.InputSize),
            (NetworkEncoding.PolicyBlocks, NetworkEncoding.PolicyNames, NetworkEncoding.PolicySize),
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
        }
    }

    /// <summary>
    /// The committed manifest equals the generated one, so any Encoding change is a visible diff (spec A6). If this
    /// fails after a deliberate change: regenerate with <c>7wd manifest --out data/encoding/v1.json</c>, and bump
    /// <see cref="NetworkEncoding.Version"/> if any index changed meaning (stored training data and models become stale).
    /// </summary>
    [Fact]
    public void The_committed_manifest_matches_the_generated_one()
    {
        var committed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "encoding", "v1.json")).Replace("\r\n", "\n");
        var generated = EncodingManifest.Generate();
        Assert.True(committed == generated,
            "data/encoding/v1.json differs from the Encoding in code. Regenerate it with "
            + "`dotnet run --project engine/src/Cli -- manifest --out data/encoding/v1.json` and bump "
            + "NetworkEncoding.Version if an index changed meaning.");
    }

    [Fact]
    public void The_manifest_is_valid_json_with_the_sizes_Python_checks()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(EncodingManifest.Generate());
        var root = doc.RootElement;
        Assert.Equal(NetworkEncoding.Version, root.GetProperty("version").GetInt32());
        Assert.Equal(NetworkEncoding.InputSize, root.GetProperty("input").GetProperty("size").GetInt32());
        Assert.Equal(NetworkEncoding.InputSize, root.GetProperty("input").GetProperty("names").GetArrayLength());
        Assert.Equal(NetworkEncoding.PolicySize, root.GetProperty("policy").GetProperty("names").GetArrayLength());
        Assert.Equal("wonder baths sphinx",
            root.GetProperty("policy").GetProperty("names")[NetworkEncoding.PolicyIndex(
                Move.BuildWonder(GameData.CardIndex("baths"), GameData.WonderIndex("sphinx")))].GetString());
        Assert.Equal(7, root.GetProperty("heads").GetProperty("outcome").GetProperty("classes").GetArrayLength());
    }
}
