using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using SevenWondersDuel.Agents;
using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor.Tests;

/// <summary>
/// Readable cards (ticket 37): every card, Wonder and Progress token gets icons and words from the card data, the icon
/// legend covers every icon used, prices match the Engine's payment, the "AI played" banner describes every kind of AI
/// Move, and none of it names anything the owner may not see.
/// </summary>
public sealed class ReadableCardsTests : IDisposable
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    readonly string _folder = Path.Combine(Path.GetTempPath(), "7wd-advisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static int Count(string text, string icon)
    {
        var n = 0;
        for (var i = text.IndexOf(icon, StringComparison.Ordinal); i >= 0; i = text.IndexOf(icon, i + icon.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Every_card_Wonder_and_token_has_icons_and_words_derived_from_the_data()
    {
        var catalog = CardText.Catalog();
        Assert.Equal(GameData.Cards.Length, catalog.Cards.Count);
        Assert.Equal(GameData.Wonders.Length, catalog.Wonders.Count);
        Assert.Equal(GameData.Tokens.Length, catalog.Tokens.Count);
        foreach (var info in catalog.Cards.Values.Concat(catalog.Wonders.Values).Concat(catalog.Tokens.Values))
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Name));
            Assert.False(string.IsNullOrWhiteSpace(info.Type));
            Assert.False(string.IsNullOrWhiteSpace(info.EffectIcons), $"{info.Id} has no effect icons");
            Assert.NotEmpty(info.Effect);
            Assert.All(info.Effect, e => Assert.False(string.IsNullOrWhiteSpace(e)));
        }

        foreach (var c in GameData.Cards)
        {
            var info = catalog.Cards[c.Id];
            Assert.Equal(c.Name, info.Name);
            Assert.Equal(c.Age, info.Age);
            Assert.Equal(c.Color.ToString().ToLowerInvariant(), info.Color);
            // The cost icons are the printed cost, unit for unit.
            foreach (var r in Enum.GetValues<Resource>()) Assert.Equal(c.Cost.Resources[(int)r], Count(info.CostIcons, Icons.Of(r)));
            Assert.Equal(c.Cost.Coins > 0, info.CostIcons.Contains($"{c.Cost.Coins}{Icons.Coin}"));
            Assert.Equal(c.Cost.ResourceTotal == 0 && c.Cost.Coins == 0, info.CostText == "free");
            // The effect icons say what the card does.
            Assert.Equal(c.Shields, Count(info.EffectIcons, Icons.Shield));
            foreach (var r in Enum.GetValues<Resource>())
                Assert.Equal(c.Produce[(int)r] + (c.ProduceChoice.Contains(r) ? 1 : 0) + (c.FixedPrice.Contains(r) ? 1 : 0),
                    Count(info.EffectIcons, Icons.Of(r)));
            if (c.Science is { } sym) Assert.Contains(Icons.Of(sym), info.EffectIcons);
            if (c.VictoryPoints > 0) Assert.Contains($"{c.VictoryPoints}{Icons.Vp}", info.EffectIcons);
            if (c.Coins > 0) Assert.Contains($"{c.Coins}{Icons.Coin}", info.EffectIcons);
            if (c.CoinsPer != CoinsPerKind.None) Assert.Contains($"{c.CoinsPerAmount}{Icons.Coin}/", info.EffectIcons);
            if (c.IsGuild) Assert.Contains(Icons.Most, info.EffectIcons);
            // The chain both ways.
            Assert.Equal(c.FreeIf >= 0 ? GameData.Cards[c.FreeIf].Name : null, info.ChainFrom);
            Assert.Equal(GameData.Cards.Where(o => o.FreeIf == c.Index).Select(o => o.Name), info.ChainTo);
            if (info.ChainFrom is not null) Assert.Contains(info.Effect, e => e.Contains(info.ChainFrom));
        }
        // The Courthouse/Tribunal trap (ticket 32): this UI uses the Engine's names.
        Assert.Equal("Courthouse", catalog.Cards["courthouse"].Name);

        foreach (var w in GameData.Wonders)
        {
            var info = catalog.Wonders[w.Id];
            foreach (var r in Enum.GetValues<Resource>()) Assert.Equal(w.Cost.Resources[(int)r], Count(info.CostIcons, Icons.Of(r)));
            Assert.Equal(w.Shields, Count(info.EffectIcons, Icons.Shield));
            Assert.Equal(w.PlayAgain, info.EffectIcons.Contains(Icons.PlayAgain));
            if (w.VictoryPoints > 0) Assert.Contains($"{w.VictoryPoints}{Icons.Vp}", info.EffectIcons);
            if (w.OpponentLoses > 0) Assert.Contains($"{Icons.OpponentLoses}{w.OpponentLoses}", info.EffectIcons);
            Assert.Contains(info.Effect, e => e.StartsWith("Built by tucking"));
        }
        foreach (var t in GameData.Tokens)
        {
            var info = catalog.Tokens[t.Id];
            if (t.VictoryPoints > 0) Assert.Contains($"{t.VictoryPoints}{Icons.Vp}", info.EffectIcons);
            if (t.Coins > 0) Assert.Contains($"{t.Coins}{Icons.Coin}", info.EffectIcons);
        }
    }

    /// <summary>
    /// Every icon on any card, Wonder or token is in the legend: removing the legend's icons from every icon line leaves
    /// only numbers and joiners. Each resource, science symbol and effect has its own icon.
    /// </summary>
    [Fact]
    public void The_legend_covers_every_icon_and_icons_are_distinct()
    {
        var catalog = CardText.Catalog();
        var legend = catalog.Legend;
        Assert.Equal(legend.Length, legend.Select(l => l.Icon).Distinct().Count());
        Assert.Equal(legend.Length, legend.Select(l => l.Meaning).Distinct().Count());
        // Single icons, longest first (so a two-codepoint emoji is removed before a shorter one it contains).
        var singles = legend.Select(l => l.Icon).Where(i => new StringInfo(i).LengthInTextElements == 1)
            .Concat(Enum.GetValues<CardColor>().Where(c => c != CardColor.Purple).Select(Icons.Of))
            .Distinct().OrderByDescending(i => i.Length).ToList();
        var lines = catalog.Cards.Values.Concat(catalog.Wonders.Values).Concat(catalog.Tokens.Values)
            .SelectMany(i => new[] { i.EffectIcons, i.CostIcons });
        foreach (var line in lines)
        {
            var rest = singles.Aggregate(line, (s, icon) => s.Replace(icon, ""));
            Assert.True(rest.All(ch => char.IsDigit(ch) || " +=/".Contains(ch)), $"'{line}' has icons the legend doesn't explain: '{rest}'");
        }
        foreach (var r in Enum.GetValues<Resource>()) Assert.Contains(legend, l => l.Icon == Icons.Of(r));
        foreach (var s in Enum.GetValues<ScienceSymbol>()) Assert.Contains(legend, l => l.Icon == Icons.Of(s));
        var icons = Enum.GetValues<Resource>().Select(Icons.Of).Concat(Enum.GetValues<ScienceSymbol>().Select(Icons.Of))
            .Concat(Enum.GetValues<CardColor>().Select(Icons.Of))
            .Concat([Icons.Coin, Icons.Vp, Icons.Shield, Icons.Chain, Icons.PlayAgain, Icons.OpponentLoses, Icons.Wonder, Icons.Token,
                Icons.Most, Icons.Free, Icons.Economy, Icons.Destroy, Icons.Library, Icons.Mausoleum, Icons.Messe, Icons.Liberty]).ToList();
        Assert.Equal(icons.Count, icons.Distinct().Count());
    }

    /// <summary>
    /// Over random games entered by hand, each face-up pyramid card carries both players' prices, equal to the Engine's
    /// payment (0 and "chain" when chained), each unbuilt Wonder its owner's; face-down cards carry no price and no name.
    /// </summary>
    [Fact]
    public void Prices_on_the_board_are_the_Engines_payment_and_face_down_cards_carry_nothing()
    {
        var checks = 0;
        for (ulong seed = 0; seed < 12; seed++)
        {
            var session = AdvisorSession.New(seed % 2 == 0 ? Ruleset.Default : new Ruleset(Promos: Promos.Messe | Promos.Liberty), (int)(seed % 2));
            var game = GameRunner.Play(session.Rules, new RandomChanceSource(seed), new RandomAgent(seed), new RandomAgent(seed + 99));
            var log = game.Log.ToList();
            for (var i = 0; i < log.Count; i++)
            {
                var s = session.State;
                if (s.Step == StepKind.Decision && s.Age > 0)
                {
                    var view = BoardView.Build(session, "x");
                    if (view.Pyramid is { } pyramid)
                    {
                        Assert.Equal(s.PlayerToDecide, pyramid.PricePlayer);
                        foreach (var slot in pyramid.Slots)
                        {
                            if (slot.Face != "up")
                            {
                                Assert.Null(slot.Card);
                                Assert.Null(slot.Prices);
                                continue;
                            }
                            var card = GameData.Cards.Single(c => c.Id == slot.Card!.Id).Index;
                            for (var p = 0; p < 2; p++)
                            {
                                var price = slot.Prices![p];
                                Assert.Equal(p, price.Player);
                                Assert.Equal(Payment.Quote(s, p, card).Total, price.Total);
                                Assert.Equal(Payment.IsChained(s, p, card), price.Chain);
                                Assert.Equal(price.Total <= s.Coins(p), price.Affordable);
                                checks++;
                            }
                        }
                    }
                    foreach (var player in view.Players)
                        foreach (var w in player.Wonders)
                        {
                            var index = GameData.Wonders.Single(x => x.Id == w.Id).Index;
                            if (w.Built || w.Removed) Assert.Null(w.Prices);
                            else Assert.Equal(Payment.QuoteWonder(s, player.Index, index).Total, w.Prices!.Single().Total);
                        }
                }
                // The opponent's Great Library draw is private: the session has already logged it as "? ? ?".
                if (log[i] is LibraryReveal l && l.Player != session.Seat) continue;
                session.Enter(MoveLog.Format(log[i]));
            }
        }
        Assert.True(checks > 1000);
    }

    /// <summary>
    /// Games against the AI (random and the Heuristic, all rule sets): after every AI turn the banner lists exactly the
    /// AI's Moves since the owner's last Move, each in words starting "AI ", with the slot the card came from; over the
    /// games every kind of AI Move gets described. The banner never names a token of the AI's Library draw it didn't keep.
    /// </summary>
    [Fact]
    public void The_AI_banner_describes_every_kind_of_AI_Move_and_names_nothing_hidden()
    {
        Ruleset[] rules = [Ruleset.Default, new(Promos: Promos.Messe | Promos.Liberty), new(StartChoice.Rule2015, Promos.Liberty)];
        var kinds = new HashSet<MoveKind>();
        for (ulong seed = 0; seed < 60; seed++)
        {
            var seat = (int)(seed % 2);
            var session = AdvisorSession.New(rules[seed % 3], seat, new AiOpponent(seed % 4 == 3 ? "heuristic" : "random", "AI", 50, seed * 17 + 3));
            var rng = new Rng(seed + 500);
            var expected = new List<(Move Move, GameState Before)>();
            var secret = new HashSet<string>();
            while (!session.State.IsOver)
            {
                if (session.AiToMove)
                {
                    var observation = session.AiObservation();
                    var move = AiPlayer.Choose(observation, session.Ai!, session.AiSeed);
                    foreach (var t in ProgressTokens.In(observation.State.LibraryDraw))
                        if (t != move.Token) secret.Add(GameData.Tokens[t].Name);
                    expected.Add((move, session.State.Clone()));
                    session.EnterAiMove(move);
                    var banner = MoveText.SinceOwnersMove(session);
                    Assert.Equal(expected.Count, banner.Length);
                    for (var k = 0; k < banner.Length; k++)
                    {
                        var (m, before) = expected[k];
                        var b = banner[k];
                        Assert.StartsWith("AI ", b.Text);
                        kinds.Add(m.Kind);
                        if (m.TakesCard)
                        {
                            Assert.Contains(GameData.Cards[m.Card].Name, b.Text);
                            Assert.NotNull(b.Slot);
                            Assert.Equal(m.Card, before.SlotCard(b.Slot!.Value));
                            Assert.Equal(before.Age, b.Age);
                        }
                        if (m.Kind == MoveKind.Build)
                            Assert.Contains(Payment.IsChained(before, session.AiSeat, m.Card) ? "free (chain"
                                : Payment.Quote(before, session.AiSeat, m.Card).Total is var t && t == 0 ? "for free" : $"for {t}{Icons.Coin}", b.Text);
                        if (m.Kind == MoveKind.Wonder) Assert.Contains(GameData.Wonders[m.Wonder].Name, b.Text);
                        if (m.Kind is MoveKind.Token or MoveKind.Library) Assert.Contains(GameData.Tokens[m.Token].Name, b.Text);
                        foreach (var name in secret) Assert.DoesNotContain(name, b.Text);
                    }
                    var json = JsonSerializer.Serialize(banner, Web);
                    foreach (var name in secret) Assert.DoesNotContain(name, json);
                }
                else
                {
                    var moves = session.State.Observe(seat).LegalMoves();
                    session.EnterOwn([MoveLog.Format(new MoveEntry(seat, moves[rng.Next(moves.Count)]))]);
                    expected.Clear();
                    Assert.Empty(MoveText.SinceOwnersMove(session));
                }
            }
        }
        foreach (var kind in new[] { MoveKind.Build, MoveKind.Discard, MoveKind.Wonder, MoveKind.Draft, MoveKind.Token })
            Assert.Contains(kind, kinds);
        Assert.True(kinds.Count >= 8, $"only {kinds.Count} kinds of AI Move seen: {string.Join(", ", kinds)}");
    }

    [Fact]
    public void A_game_entered_by_hand_has_no_AI_banner()
    {
        var session = AdvisorSession.New(Ruleset.Default, 0);
        Assert.Empty(MoveText.SinceOwnersMove(session));
    }

    /// <summary>The catalog is served once over HTTP, and the state carries the banner in a game against the AI.</summary>
    [Fact]
    public async Task The_catalog_and_the_banner_are_served()
    {
        var app = AdvisorServer.Build(new AdvisorOptions(Path.Combine(_folder, "data"), Port: 0, AiPause: TimeSpan.Zero));
        await app.StartAsync();
        try
        {
            var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/") };
            var res = await http.GetAsync("api/catalog");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var catalog = await ServerTests.Json(res);
            Assert.Equal(GameData.Cards.Length, catalog.GetProperty("cards").EnumerateObject().Count());
            Assert.Equal(Icons.Of(Resource.Wood), catalog.GetProperty("legend")[0].GetProperty("icon").GetString());

            var created = await http.PostAsync("api/new", JsonContent(new
            {
                messe = false, liberty = false, rule2015 = false, seat = 1, vsAi = true, opponent = "random", opponentLabel = "Random",
                aiSimulations = 10, randomSeat = false,
            }));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var server = (AdvisorServer)app.Services.GetService(typeof(AdvisorServer))!;
            var rng = new Rng(1);
            JsonElement moves = default;
            for (var i = 0; i < 4; i++)
            {
                await server.AiSettled();
                var state = await ServerTests.Json(await http.GetAsync("api/state"));
                moves = state.GetProperty("play").GetProperty("aiMoves");
                if (moves.GetArrayLength() > 0) break;
                var session = server.Session!;
                var legal = session.State.Observe(session.Seat).LegalMoves();
                await http.PostAsync("api/entry", JsonContent(new { line = MoveLog.Format(new MoveEntry(session.Seat, legal[rng.Next(legal.Count)])) }));
            }
            Assert.True(moves.GetArrayLength() >= 1, "the AI drafted, so the banner shows it");
            Assert.StartsWith("AI ", moves[0].GetProperty("text").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, Web), System.Text.Encoding.UTF8, "application/json");
}
