using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor;

// Readable cards (ticket 37): every card, Wonder and Progress token gets a compact icon line (cost and effect) and a
// plain-words description, all derived from the card data (GameData, i.e. data/base/*.json) so there is one source of
// truth. The page draws these; it holds no per-card text. Prices for the current position come from Payment.

/// <summary>One icon of the legend: the emoji and what it means.</summary>
public sealed record IconView(string Icon, string Meaning);

/// <summary>A card, Wonder or Progress token in words and icons.</summary>
public sealed record InfoView(
    string Kind, string Id, string Name, string Type, string Color, int Age,
    string CostIcons, string CostText, string EffectIcons, string[] Effect,
    string? ChainFrom, string[] ChainTo);

/// <summary>Everything the page needs to draw readable cards: the legend and every card, Wonder and token.</summary>
public sealed record CatalogView(
    IconView[] Legend, Dictionary<string, InfoView> Cards, Dictionary<string, InfoView> Wonders, Dictionary<string, InfoView> Tokens);

/// <summary>What one player pays for a card or Wonder right now (the Engine's automatic payment).</summary>
public sealed record PriceView(int Player, string Who, int Total, int CardCoins, int TradeCoins, bool Chain, bool Affordable,
    string Short, string[] Detail);

/// <summary>The icons: one per resource, science symbol and effect kind.</summary>
public static class Icons
{
    public const string Coin = "🪙";
    public const string Vp = "🏆";
    public const string Shield = "⚔️";
    public const string Chain = "🔗";
    public const string PlayAgain = "🔁";
    public const string OpponentLoses = "💸";
    public const string Wonder = "🏛️";
    public const string Token = "🟢";
    public const string Most = "👑";
    public const string Free = "🎁";
    public const string Economy = "💱";
    public const string Destroy = "💥";
    public const string Library = "📚";
    public const string Mausoleum = "⚰️";
    public const string Messe = "🎪";
    public const string Liberty = "🗽";

    public static string Of(Resource r) => r switch
    {
        Resource.Wood => "🪵",
        Resource.Clay => "🧱",
        Resource.Stone => "🪨",
        Resource.Glass => "🫙",
        Resource.Papyrus => "📜",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public static string Of(ScienceSymbol s) => s switch
    {
        ScienceSymbol.Globe => "🌍",
        ScienceSymbol.Law => "⚖️",
        ScienceSymbol.Sundial => "🕰️",
        ScienceSymbol.Mortar => "🥣",
        ScienceSymbol.Compass => "🧭",
        ScienceSymbol.Quill => "🪶",
        ScienceSymbol.Wheel => "☸️",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string Of(CardColor c) => c switch
    {
        CardColor.Brown => "🟫",
        CardColor.Grey => "⬜",
        CardColor.Red => "🟥",
        CardColor.Green => "🟩",
        CardColor.Yellow => "🟨",
        CardColor.Blue => "🟦",
        CardColor.Purple => "🟪",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>The legend, in the order the page shows it.</summary>
    public static IconView[] Legend() =>
    [
        .. Enum.GetValues<Resource>().Select(r => new IconView(Of(r), CardText.Word(r))),
        new(Coin, "coins"),
        new(Vp, "victory points (VP)"),
        new(Shield, "shield (moves the Conflict pawn)"),
        .. Enum.GetValues<ScienceSymbol>().Select(s => new IconView(Of(s), $"science: {CardText.Word(s)}")),
        new(Chain, "chain: free if you own the linked card"),
        new("🪨=1🪙", "trading: buy that resource for 1 coin"),
        new("🫙/📜", "produces one of these each turn"),
        new(string.Concat(Enum.GetValues<CardColor>().Where(c => c != CardColor.Purple).Select(Of)),
            "per card of that colour (brown, grey, red, green, yellow, blue)"),
        new(Most, "in the city with the most"),
        new(Wonder, "Wonder"),
        new(Token, "Progress token"),
        new(PlayAgain, "play again"),
        new(OpponentLoses, "your opponent loses coins"),
        new(Free, "resources you don't pay (discount)"),
        new(Economy, "your opponent's trading coins go to you"),
        new(Destroy, "destroy an opponent's card"),
        new(Library, "draw 3 Progress tokens from the box, keep 1"),
        new(Mausoleum, "build a discarded card for free"),
        new(Messe, "build a top-row card for free"),
        new(Liberty, "the 3 removed cards come back"),
    ];
}

/// <summary>Builds the <see cref="InfoView"/>s and prices.</summary>
public static class CardText
{
    public static string Word(Resource r) => r.ToString().ToLowerInvariant();
    public static string Word(ScienceSymbol s) => s switch { ScienceSymbol.Law => "law (scales)", _ => s.ToString().ToLowerInvariant() };
    public static string Word(CardColor c) => c.ToString().ToLowerInvariant();

    public static string ColorType(CardColor c) => c switch
    {
        CardColor.Brown => "Raw materials (brown card)",
        CardColor.Grey => "Manufactured goods (grey card)",
        CardColor.Red => "Military building (red card)",
        CardColor.Green => "Scientific building (green card)",
        CardColor.Yellow => "Commercial building (yellow card)",
        CardColor.Blue => "Civilian building (blue card)",
        CardColor.Purple => "Guild (purple card)",
        _ => c.ToString(),
    };

    static string Roman(int age) => age switch { 1 => "I", 2 => "II", 3 => "III", _ => age.ToString() };

    public static CatalogView Catalog() => new(
        Icons.Legend(),
        GameData.Cards.ToDictionary(c => c.Id, Card),
        GameData.Wonders.ToDictionary(w => w.Id, Wonder),
        GameData.Tokens.ToDictionary(t => t.Id, Token));

    // ---------------------------------------------------------------- costs

    public static string CostIcons(Cost c)
    {
        var res = string.Concat(Enum.GetValues<Resource>().SelectMany(r => Enumerable.Repeat(Icons.Of(r), c.Resources[(int)r])));
        return c.Coins > 0 ? $"{c.Coins}{Icons.Coin} {res}".TrimEnd() : res;
    }

    public static string CostWords(Cost c)
    {
        var parts = new List<string>();
        if (c.Coins > 0) parts.Add(c.Coins == 1 ? "1 coin" : $"{c.Coins} coins");
        foreach (var r in Enum.GetValues<Resource>())
            if (c.Resources[(int)r] > 0) parts.Add($"{c.Resources[(int)r]} {Word(r)}");
        return parts.Count == 0 ? "free" : string.Join(", ", parts);
    }

    static string Join(IEnumerable<string> words, string last = "or")
    {
        var list = words.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} {last} {list[^1]}";
    }

    static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    // ---------------------------------------------------------------- cards

    public static InfoView Card(CardDef c)
    {
        var icons = new List<string>();
        var words = new List<string>();
        var produced = Enum.GetValues<Resource>().Where(r => c.Produce[(int)r] > 0).ToList();
        foreach (var r in produced)
        {
            icons.Add(string.Concat(Enumerable.Repeat(Icons.Of(r), c.Produce[(int)r])));
            words.Add($"Produces {c.Produce[(int)r]} {Word(r)} every turn.");
        }
        if (c.ProduceChoice.Length > 0)
        {
            icons.Add(string.Join('/', c.ProduceChoice.Select(Icons.Of)));
            words.Add($"Produces 1 {Join(c.ProduceChoice.Select(Word))} every turn (you choose each time).");
        }
        if (c.FixedPrice.Length > 0)
        {
            icons.Add($"{string.Concat(c.FixedPrice.Select(Icons.Of))}=1{Icons.Coin}");
            words.Add($"Trading: you buy {Join(c.FixedPrice.Select(Word), "and")} from the bank for 1 coin each.");
        }
        if (c.Shields > 0)
        {
            icons.Add(string.Concat(Enumerable.Repeat(Icons.Shield, c.Shields)));
            words.Add($"{Plural(c.Shields, "shield", "shields")}: the Conflict pawn moves {c.Shields} toward your opponent's capital.");
        }
        if (c.Science is { } sym)
        {
            icons.Add(Icons.Of(sym));
            words.Add($"Science symbol: {Word(sym)}. A pair of the same symbol gives you a Progress token; 6 different symbols win the game.");
        }
        if (c.Guild != GuildKind.None) Guild(c.Guild, icons, words);
        if (c.Coins > 0)
        {
            icons.Add($"{c.Coins}{Icons.Coin}");
            words.Add($"Gives {c.Coins} coins when built.");
        }
        if (c.CoinsPer != CoinsPerKind.None)
        {
            var (icon, what) = c.CoinsPer switch
            {
                CoinsPerKind.Wonder => (Icons.Wonder, "Wonder you have built"),
                var k => (Icons.Of(Enum.Parse<CardColor>(k.ToString())), $"{k.ToString().ToLowerInvariant()} card in your city"),
            };
            icons.Add($"{c.CoinsPerAmount}{Icons.Coin}/{icon}");
            words.Add($"When built: {Plural(c.CoinsPerAmount, "coin", "coins")} per {what}.");
        }
        if (c.VictoryPoints > 0)
        {
            icons.Add($"{c.VictoryPoints}{Icons.Vp}");
            words.Add($"Worth {c.VictoryPoints} VP at the end of the game.");
        }
        var from = c.FreeIf >= 0 ? GameData.Cards[c.FreeIf].Name : null;
        var to = GameData.Cards.Where(o => o.FreeIf == c.Index).Select(o => o.Name).ToArray();
        if (from is not null) words.Add($"Chain: free if you own {from}.");
        foreach (var t in to) words.Add($"Chain: makes {t} (a later Age) free for you.");
        return new InfoView("card", c.Id, c.Name, $"{ColorType(c.Color)}, Age {Roman(c.Age)}", Word(c.Color), c.Age,
            CostIcons(c.Cost), CostWords(c.Cost), string.Join(' ', icons), [.. words], from, to);
    }

    static void Guild(GuildKind g, List<string> icons, List<string> words)
    {
        switch (g)
        {
            case GuildKind.Builders:
                icons.Add($"2{Icons.Vp}/{Icons.Wonder}{Icons.Most}");
                words.Add("At the end: 2 VP per Wonder built in the city with the most Wonders (yours or your opponent's).");
                return;
            case GuildKind.Moneylenders:
                icons.Add($"1{Icons.Vp}/3{Icons.Coin}{Icons.Most}");
                words.Add("At the end: 1 VP per 3 coins in the richest city (yours or your opponent's).");
                return;
        }
        var colors = g switch
        {
            GuildKind.Merchants => new[] { CardColor.Yellow },
            GuildKind.Shipowners => [CardColor.Brown, CardColor.Grey],
            GuildKind.Magistrates => [CardColor.Blue],
            GuildKind.Scientists => [CardColor.Green],
            GuildKind.Tacticians => [CardColor.Red],
            _ => throw new ArgumentOutOfRangeException(nameof(g)),
        };
        icons.Add($"1{Icons.Coin}1{Icons.Vp}/{string.Concat(colors.Select(Icons.Of))}{Icons.Most}");
        var kind = Join(colors.Select(Word), "and");
        words.Add($"When built: 1 coin per {kind} card in the city with the most {kind} cards (yours or your opponent's).");
        words.Add($"At the end: 1 VP per {kind} card in that city.");
    }

    // ---------------------------------------------------------------- Wonders

    public static InfoView Wonder(WonderDef w)
    {
        var icons = new List<string>();
        var words = new List<string>();
        if (w.ProduceChoice.Length > 0)
        {
            icons.Add(string.Join('/', w.ProduceChoice.Select(Icons.Of)));
            words.Add($"Produces 1 {Join(w.ProduceChoice.Select(Word))} every turn (you choose each time).");
        }
        if (w.Shields > 0)
        {
            icons.Add(string.Concat(Enumerable.Repeat(Icons.Shield, w.Shields)));
            words.Add($"{Plural(w.Shields, "shield", "shields")}: the Conflict pawn moves {w.Shields} toward your opponent's capital.");
        }
        if (w.Coins > 0)
        {
            icons.Add($"{w.Coins}{Icons.Coin}");
            words.Add($"Gives {w.Coins} coins when built.");
        }
        if (w.OpponentLoses > 0)
        {
            icons.Add($"{Icons.OpponentLoses}{w.OpponentLoses}");
            words.Add($"Your opponent loses {w.OpponentLoses} coins.");
        }
        switch (w.Effect)
        {
            case WonderEffect.DestroyBrown:
                icons.Add($"{Icons.Destroy}{Icons.Of(CardColor.Brown)}");
                words.Add("Destroy one brown card of your opponent's (it goes to the discard pile).");
                break;
            case WonderEffect.DestroyGrey:
                icons.Add($"{Icons.Destroy}{Icons.Of(CardColor.Grey)}");
                words.Add("Destroy one grey card of your opponent's (it goes to the discard pile).");
                break;
            case WonderEffect.Library:
                icons.Add(Icons.Library);
                words.Add("Draw 3 of the Progress tokens left in the box (unseen by your opponent) and keep 1.");
                break;
            case WonderEffect.Mausoleum:
                icons.Add(Icons.Mausoleum);
                words.Add("Build one card from the discard pile for free.");
                break;
            case WonderEffect.Messe:
                icons.Add(Icons.Messe);
                words.Add("Build one card of the top row of the current Age for free.");
                break;
            case WonderEffect.Liberty:
                icons.Add(Icons.Liberty);
                words.Add("The 3 cards removed from this Age come back: keep one, give one to your opponent, discard the third.");
                break;
        }
        if (w.PlayAgain)
        {
            icons.Add(Icons.PlayAgain);
            words.Add("Play again: take another turn right away.");
        }
        if (w.VictoryPoints > 0)
        {
            icons.Add($"{w.VictoryPoints}{Icons.Vp}");
            words.Add($"Worth {w.VictoryPoints} VP at the end of the game.");
        }
        words.Add("Built by tucking any card you take from the pyramid under it. Only 7 of the 8 Wonders get built.");
        return new InfoView("wonder", w.Id, w.Name, w.IsPromo ? "Wonder (promo)" : "Wonder", "wonder", 0,
            CostIcons(w.Cost), CostWords(w.Cost), string.Join(' ', icons), [.. words], null, []);
    }

    // ---------------------------------------------------------------- Progress tokens

    /// <summary>The token's effects: VP, coins and Law's symbol from the data, the rest by id as in the Engine.</summary>
    public static InfoView Token(TokenDef t)
    {
        var (icons, words) = t.Index switch
        {
            var i when i == ProgressTokens.Agriculture => ($"{t.Coins}{Icons.Coin} {t.VictoryPoints}{Icons.Vp}", $"Gives {t.Coins} coins now and is worth {t.VictoryPoints} VP."),
            var i when i == ProgressTokens.Architecture => ($"{Icons.Wonder} {ProgressTokens.Discount}{Icons.Free}", $"Your Wonders cost {ProgressTokens.Discount} fewer resources (of your choice)."),
            var i when i == ProgressTokens.Economy => (Icons.Economy, "The coins your opponent pays the bank for trading go to you instead."),
            var i when i == ProgressTokens.Law => (Icons.Of(ScienceSymbol.Law), "A science symbol: law (scales). It counts toward pairs and the 6 symbols of a science victory."),
            var i when i == ProgressTokens.Masonry => ($"{Icons.Of(CardColor.Blue)} {ProgressTokens.Discount}{Icons.Free}", $"Your blue cards cost {ProgressTokens.Discount} fewer resources (of your choice)."),
            var i when i == ProgressTokens.Mathematics => ($"{ProgressTokens.MathematicsPointsPerToken}{Icons.Vp}/{Icons.Token}", $"At the end: {ProgressTokens.MathematicsPointsPerToken} VP per Progress token you own, this one included."),
            var i when i == ProgressTokens.Philosophy => ($"{t.VictoryPoints}{Icons.Vp}", $"Worth {t.VictoryPoints} VP."),
            var i when i == ProgressTokens.Strategy => ($"{Icons.Of(CardColor.Red)} +1{Icons.Shield}", "Each red card you build from now on gives 1 extra shield (not Wonders)."),
            var i when i == ProgressTokens.Theology => ($"{Icons.Wonder}{Icons.PlayAgain}", "All your Wonders get 'play again' (including the ones built later)."),
            var i when i == ProgressTokens.Urbanism => ($"{t.Coins}{Icons.Coin} {Icons.Chain}+{ProgressTokens.UrbanismChainCoins}{Icons.Coin}", $"Gives {t.Coins} coins now, and {ProgressTokens.UrbanismChainCoins} coins each time you build a card for free through a chain."),
            _ => throw new InvalidOperationException($"No text for Progress token {t.Id}"),
        };
        return new InfoView("token", t.Id, t.Name, "Progress token", "token", 0, "", "", icons, [words], null, []);
    }

    // ---------------------------------------------------------------- prices right now

    /// <summary>What <paramref name="player"/> pays to build <paramref name="card"/> now, with the reasons.</summary>
    public static PriceView CardPrice(GameState s, int player, int card, string who)
    {
        var def = GameData.Cards[card];
        if (Payment.IsChained(s, player, card))
            return new PriceView(player, who, 0, 0, 0, true, true, $"free ({Icons.Chain})",
                [$"Free: chain from {GameData.Cards[def.FreeIf].Name}."]);
        var quote = Payment.Quote(s, player, card);
        var discount = def.Color == CardColor.Blue && s.HasToken(player, ProgressTokens.Masonry) ? "Masonry" : null;
        return Price(s, player, def.Cost, quote, who, discount);
    }

    /// <summary>What <paramref name="player"/> pays to build Wonder <paramref name="wonder"/> now.</summary>
    public static PriceView WonderPrice(GameState s, int player, int wonder, string who)
    {
        var quote = Payment.QuoteWonder(s, player, wonder);
        var discount = s.HasToken(player, ProgressTokens.Architecture) ? "Architecture" : null;
        return Price(s, player, GameData.Wonders[wonder].Cost, quote, who, discount);
    }

    static PriceView Price(GameState s, int player, Cost cost, PaymentQuote quote, string who, string? discount)
    {
        var detail = new List<string>();
        if (cost.Coins > 0) detail.Add($"Printed cost: {cost.Coins}{Icons.Coin}.");
        var missing = Enum.GetValues<Resource>()
            .Select(r => (R: r, N: Math.Max(0, cost.Resources[(int)r] - s.Production(player, r))))
            .Where(x => x.N > 0).ToList();
        if (cost.ResourceTotal > 0 && missing.Count == 0) detail.Add("Own production covers every resource.");
        if (missing.Count > 0)
        {
            // The bank price per missing unit: 2 + what the opponent produces of it, or 1 with a trading card.
            var full = missing.Sum(x => x.N * Payment.TradingPrice(s, player, x.R));
            detail.Add("Buys from the bank: " + string.Join(", ",
                missing.Select(x => $"{x.N}×{Icons.Of(x.R)} at {Payment.TradingPrice(s, player, x.R)}{Icons.Coin} each")) + ".");
            if (quote.TradeCoins < full)
                detail.Add($"Less {full - quote.TradeCoins}{Icons.Coin}: covered by "
                           + (discount is not null ? $"{discount} ({ProgressTokens.Discount} resources free) and \"one of\" producers" : "\"one of\" producers")
                           + $", so trading costs {quote.TradeCoins}{Icons.Coin}.");
        }
        var afford = quote.Total <= s.Coins(player);
        if (!afford) detail.Add($"Has only {s.Coins(player)}{Icons.Coin}: can't afford it.");
        var shortText = quote.Total == 0 ? "free" : $"{quote.Total}{Icons.Coin}";
        return new PriceView(player, who, quote.Total, quote.CardCoins, quote.TradeCoins, false, afford, shortText, [.. detail]);
    }
}
