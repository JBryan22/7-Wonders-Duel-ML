using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor;

// The JSON the page draws (web/app.js). Everything the page shows or offers comes from here, so the page holds no
// game rules: each clickable action carries the exact Move Log line it enters.

public sealed record CardView(string Id, string Name, string Color, int Age);
public sealed record OptionView(string Id, string Name, string? Color, bool Enabled, string? Reason, string? Ref = null);
/// <summary>A clickable action; <see cref="Refs"/> name the cards, Wonders or tokens it is about ("card:baths", "wonder:sphinx", "token:law").</summary>
public sealed record ActionView(string Label, string Line, bool Enabled, string? Reason, string Kind, string? Then = null, string[]? Refs = null);
/// <summary>A pyramid slot; <see cref="Token"/> / <see cref="TokenName"/> are a Pantheon Mythology or Offering token lying on it.</summary>
public sealed record SlotView(
    int Slot, string Name, int Row, int Col, string Face, CardView? Card, bool Accessible,
    bool Pulse, string? RevealPrefix, OptionView[]? Candidates, ActionView[] Actions, PriceView[]? Prices = null,
    string? Token = null, string? TokenName = null);
/// <summary>The pyramid; <see cref="PricePlayer"/> is whose price the card faces show (the player to decide, else the owner).</summary>
public sealed record PyramidView(int Age, int Rows, int Columns, SlotView[] Slots, int PricePlayer);
/// <summary>A Reveal to enter; the Pantheon forms (kind "fields") pick one option per <see cref="Fields"/> entry.</summary>
public sealed record RevealView(string Kind, string Title, int Count, string LinePrefix, OptionView[] Options, FieldView[]? Fields = null);
public sealed record DecisionView(string Kind, int Player, bool You, string Title, ActionView[] Tiles);
public sealed record WonderView(string Id, string Name, bool Built, bool Removed, CardView? Card, string Summary, PriceView[]? Prices = null);
public sealed record TokenView(string Id, string Name);
public sealed record PlayerView(
    int Index, string Label, bool You, bool First, bool ToDecide, int Coins, int Vp, string Production,
    string[] Science, CardView[] Cards, WonderView[] Wonders, TokenView[] Tokens);
public sealed record MilitaryTokenView(int Victim, int Coins, int Zone, bool Left);
/// <summary>The Military track; <see cref="Minerva"/> is the Pantheon's Minerva pawn's space, if she is on it.</summary>
public sealed record ConflictView(int Pawn, MilitaryTokenView[] Tokens, int? Minerva = null);
public sealed record LogLineView(int Index, string Text, string Kind, int Player, bool Auto);
public sealed record ScoreRowView(string Category, int P1, int P2);
public sealed record ResultView(int Winner, string Victory, string Summary, ScoreRowView[] Scores, int P1Total, int P2Total);
public sealed record GameView(
    string Screen, int Seat, string Rules, bool Messe, bool Liberty, bool Rule2015, int Age, string Step, string Prompt,
    PyramidView? Pyramid, RevealView? Reveal, DecisionView? Decision, PlayerView[] Players, ConflictView Conflict,
    TokenView[] BoardTokens, CardView[] Discard, string[] Offer, LogLineView[] Log, ResultView? Result, bool CanUndo,
    string SavedTo, bool PantheonGame = false, PantheonView? Pantheon = null);

/// <summary>Builds the <see cref="GameView"/> of an <see cref="AdvisorSession"/>.</summary>
public static class BoardView
{
    public static GameView Build(AdvisorSession session, string savedTo)
    {
        var s = session.State;
        var reveal = s.Step == StepKind.Reveal ? RevealOf(session) : null;
        var decision = s.Step == StepKind.Decision ? DecisionOf(session) : null;
        return new GameView(
            Screen: "game",
            Seat: session.Seat,
            Rules: s.Rules.ToString(),
            Messe: s.Rules.HasPromo(Promos.Messe),
            Liberty: s.Rules.HasPromo(Promos.Liberty),
            Rule2015: s.Rules.StartChoice == StartChoice.Rule2015,
            Age: s.Age,
            Step: s.Step switch { StepKind.Reveal => "reveal", StepKind.Decision => "decision", _ => "over" },
            Prompt: Prompt(session, reveal, decision),
            Pyramid: s.Age > 0 && s.CardsLeftInAge > 0 ? PyramidOf(session) : null,
            Reveal: reveal,
            Decision: decision,
            Players: [PlayerOf(session, 0), PlayerOf(session, 1)],
            Conflict: new ConflictView(s.ConflictPawn, Enumerable.Range(0, GameState.MilitaryTokenCount)
                .Select(t => new MilitaryTokenView(t / 2, GameState.MilitaryTokenCoins(t), GameState.MilitaryTokenZone(t), s.MilitaryTokenLeft(t)))
                .ToArray(), s.Pantheon is { Minerva: var minerva } && minerva != PantheonState.NoMinerva ? minerva : null),
            BoardTokens: ProgressTokens.In(s.BoardTokens).Select(Token).ToArray(),
            Discard: s.DiscardPile.Cards().Select(Card).ToArray(),
            Offer: OfferText(s),
            Log: session.Log.Select((e, i) => new LogLineView(i, MoveLog.Format(e),
                e is MoveEntry ? "move" : "reveal",
                e switch { MoveEntry m => m.Player, LibraryReveal l => l.Player, DivinityDrawReveal d => d.Player, TheaterReveal t => t.Player, _ => -1 },
                e is LibraryReveal { Redacted: true } or DivinityDrawReveal { Redacted: true } or TheaterReveal { Redacted: true })).ToArray(),
            Result: s.Result is { } r ? ResultOf(r) : null,
            CanUndo: session.Ai is null ? session.Log.Count > 0 : session.CanTakeBack,
            SavedTo: savedTo,
            PantheonGame: s.Pantheon is not null,
            Pantheon: s.Pantheon is not null ? PantheonText.Board(session) : null);
    }

    static string Who(AdvisorSession session, int player) =>
        player == session.Seat ? $"P{player + 1} (you)" : session.Ai is not null ? $"P{player + 1} (AI)" : $"P{player + 1}";

    static string Prompt(AdvisorSession session, RevealView? reveal, DecisionView? decision)
    {
        var s = session.State;
        if (s.Result is { } r)
            return r.IsDraw ? "Game over: a draw" : $"Game over: {Who(session, r.Winner)} wins by {r.Victory.ToString().ToLowerInvariant()}";
        if (reveal is not null) return reveal.Title;
        return decision!.Title;
    }

    // ---------------------------------------------------------------- the pyramid

    static PyramidView PyramidOf(AdvisorSession session)
    {
        var s = session.State;
        var layout = s.Layout;
        var toReveal = s.SlotsToReveal;
        var takeCard = s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.TakeCard;
        var slots = layout.Slots.Select(slot =>
        {
            var value = s.SlotCard(slot.Index);
            var name = layout.SlotName(slot.Index);
            var face = value switch
            {
                GameState.Empty => "empty",
                GameState.HiddenGuild => s.Pantheon is null ? "guild" : "temple",
                GameState.Hidden => "down",
                _ => "up",
            };
            var pulse = (toReveal & 1u << slot.Index) != 0;
            var (token, tokenName) = PantheonText.SlotToken(s, slot.Index);
            return new SlotView(
                Slot: slot.Index,
                Name: name,
                Row: slot.Row,
                Col: slot.Col,
                Face: face,
                Card: value >= 0 ? Card(value) : null,
                Accessible: value != GameState.Empty && s.IsAccessible(slot.Index),
                Pulse: pulse,
                RevealPrefix: pulse ? (s.AgeUntouched ? $"reveal age{s.Age} {name}" : $"reveal {name}") : null,
                Candidates: pulse ? Candidates(s, slot.Index) : null,
                Actions: takeCard && value >= 0 && s.IsAccessible(slot.Index) ? CardActions(s, value) : [],
                Prices: value >= 0 ? [CardText.CardPrice(s, 0, value, PriceWho(session, 0)), CardText.CardPrice(s, 1, value, PriceWho(session, 1))] : null,
                Token: token, TokenName: tokenName);
        }).ToArray();
        var pricePlayer = s.Step == StepKind.Decision ? s.PlayerToDecide : session.Seat;
        return new PyramidView(s.Age, layout.RowCount, layout.Slots.Max(x => x.Col) + 2, slots, pricePlayer);
    }

    /// <summary>The short name on a price: "you", "AI" or "P2".</summary>
    static string PriceWho(AdvisorSession session, int player) =>
        player == session.Seat ? "you" : session.Ai is not null ? "AI" : $"P{player + 1}";

    static string CardRef(int card) => $"card:{GameData.Card(card).Id}";
    static string WonderRef(int wonder) => $"wonder:{GameData.Wonder(wonder).Id}";
    static string TokenRef(int token) => $"token:{GameData.Token(token).Id}";

    /// <summary>
    /// What a face-down slot may be named as: every Guild behind a Guild back, else every card of the Age. Cards already
    /// revealed are listed but refused, so the picker can say why the card the owner sees cannot be right.
    /// </summary>
    static OptionView[] Candidates(GameState s, int slot)
    {
        var guild = s.SlotCard(slot) == GameState.HiddenGuild;
        var pool = s.UnseenPoolFor(slot);
        // Pantheon: Grand Temples stand behind the Temple backs (the Guilds are not used).
        var behindBack = s.Pantheon is null ? GameData.Cards.Where(c => c.IsGuild) : PantheonData.TempleCards;
        return (guild ? behindBack : GameData.Cards.Where(c => !c.IsGuild && c.Age == s.Age))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new OptionView(c.Id, c.Name, Color(c.Color), pool.Contains(c.Index),
                pool.Contains(c.Index) ? null : WhereSeen(s, c.Index), CardRef(c.Index)))
            .ToArray();
    }

    static string WhereSeen(GameState s, int card)
    {
        for (var slot = 0; slot < GameState.SlotCount; slot++)
            if (s.SlotCard(slot) == card) return $"already in slot {s.Layout.SlotName(slot)}";
        for (var p = 0; p < 2; p++)
            if (s.City(p).Contains(card)) return $"already in P{p + 1}'s city";
        if (s.DiscardPile.Contains(card)) return "already in the discard pile";
        return "already revealed";
    }

    /// <summary>Build / Discard / Wonder X for an accessible card; only the player's unbuilt Wonders are offered (spec A8).</summary>
    static ActionView[] CardActions(GameState s, int card)
    {
        var player = s.PlayerToDecide;
        var p = $"P{player + 1}";
        var actions = new List<ActionView>();
        var build = Move.Build(card);
        var cost = Payment.Quote(s, player, card).Total;
        actions.Add(Action($"Build ({Coins(cost)})", $"{p} {build}", s.WhyIllegal(build), "build"));
        actions.Add(Action($"Discard (+{2 + s.YellowCards(player)} coins)", $"{p} {Move.Discard(card)}", null, "discard"));
        foreach (var w in GameState.WondersIn(s.BuildableWonders(player)))
        {
            var move = Move.BuildWonder(card, w);
            var wonderCost = Payment.QuoteWonder(s, player, w).Total;
            actions.Add(Action($"Wonder: {GameData.Wonder(w).Name} ({Coins(wonderCost)})", $"{p} {move}", s.WhyIllegal(move), "wonder") with { Refs = [WonderRef(w)] });
        }
        return [.. actions];
    }

    static ActionView Action(string label, string line, string? whyNot, string kind) =>
        new(label, line, whyNot is null, whyNot, kind);

    static string Coins(int n) => n == 0 ? "free" : n == 1 ? "1 coin" : $"{n} coins";

    // ---------------------------------------------------------------- Reveals

    static RevealView RevealOf(AdvisorSession session)
    {
        var s = session.State;
        if (s.FirstPlayer < 0)
            return new RevealView("first", "Who plays first?", 1, "reveal first",
                [.. Enumerable.Range(0, 2).Select(p => new OptionView($"P{p + 1}", Who(session, p), null, true, null))]);
        if (s.AwaitingTokens)
            return new RevealView("tokens", "Name the 5 Progress tokens set out on the board", ProgressTokens.OnBoard, "reveal tokens",
                [.. TokenPool(s).Select(t => new OptionView(t.Id, t.Name, null, true, null, TokenRef(t.Index)))]);
        if (s.AwaitingDraftOffer)
            return new RevealView("draft", $"Name the 4 Wonders offered in draft round {s.DraftPicks / GameState.DraftOfferSize + 1}",
                GameState.DraftOfferSize, "reveal draft",
                [.. GameState.WondersIn(s.WonderPool).Select(w => new OptionView(GameData.Wonder(w).Id, GameData.Wonder(w).Name, null,
                    (s.UnseenWonders & 1 << w) != 0, (s.UnseenWonders & 1 << w) != 0 ? null : "already offered", WonderRef(w)))]);
        if (s.AwaitingGuildPattern)
        {
            var layout = s.Layout;
            // Pantheon: the 3 Grand Temples have their own back (R6) and take the Guilds' place.
            var (title, prefix) = s.Pantheon is null
                ? ("Age III: click the 3 cards showing a purple Guild back", "reveal guilds")
                : ("Age III: click the 3 cards showing a Grand Temple back", "reveal temples");
            return new RevealView("guilds", title, GameState.GuildsPerGame, prefix,
                [.. layout.Slots.Select(x => new OptionView(layout.SlotName(x.Index), layout.SlotName(x.Index), null, true, null))]);
        }
        if (s.SlotsToReveal == 0 && s.AwaitingLibraryDraw)
            // Only the owner's own draw gets here: the opponent's is logged redacted by the session.
            return new RevealView("library", "The Great Library: name the 3 Progress tokens you drew", GameState.LibraryDrawSize,
                $"reveal P{s.PlayerToDecide + 1} library",
                [.. ProgressTokens.In(s.TokensSetAside).Select(t => new OptionView(GameData.Token(t).Id, GameData.Token(t).Name, null, true, null, TokenRef(t)))]);
        if (s.Pantheon is not null && PantheonText.Reveal(session) is { } pantheon) return pantheon;
        if (s.SlotsToReveal == 0 && s.AwaitingReturnedCards)
        {
            var pool = s.UnseenPool(s.Age);
            return new RevealView("returned", $"Statue of Liberty: name the 3 removed Age {Roman(s.Age)} cards it returns",
                GameState.ReturnedCardCount, $"reveal returned age{s.Age}",
                [.. GameData.Cards.Where(c => !c.IsGuild && c.Age == s.Age).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new OptionView(c.Id, c.Name, Color(c.Color), pool.Contains(c.Index), pool.Contains(c.Index) ? null : WhereSeen(s, c.Index), CardRef(c.Index)))]);
        }
        var count = System.Numerics.BitOperations.PopCount(s.SlotsToReveal);
        return new RevealView("slots",
            s.AgeUntouched
                ? $"Age {Roman(s.Age)}: name the face-up cards ({count} left). Click a pulsing card."
                : count == 1 ? "A card was uncovered: click it and name it" : $"{count} cards were uncovered: click each and name it",
            count, "reveal", []);
    }

    // ---------------------------------------------------------------- other Pending Decisions

    static DecisionView DecisionOf(AdvisorSession session)
    {
        var s = session.State;
        var d = s.Pending(0);
        var who = Who(session, d.Player);
        var p = $"P{d.Player + 1}";
        var moves = s.LegalMoves();
        ActionView Tile(Move m, string label) => new(label, $"{p} {m}", true, null, m.Kind.ToString().ToLowerInvariant(), Refs: RefsOf(m));
        if (d.Kind == DecisionKind.TakeCard && s.Pantheon is not null && s.Age >= 2)
            return new("take-card", d.Player, d.Player == session.Seat,
                $"{who} takes a card{(s.PlayAgain ? " (and plays again after this)" : "")}: click an outlined card, or activate a Divinity in the Pantheon", []);
        return d.Kind switch
        {
            DecisionKind.TakeCard => new("take-card", d.Player, d.Player == session.Seat,
                $"{who} takes a card{(s.PlayAgain ? " (and plays again after this)" : "")}: click an outlined card", []),
            DecisionKind.DraftWonder => new("draft", d.Player, d.Player == session.Seat, $"{who} drafts a Wonder",
                [.. moves.Select(m => Tile(m, GameData.Wonder(m.Wonder).Name))]),
            DecisionKind.ChooseToken => new("token", d.Player, d.Player == session.Seat, $"{who} takes a Progress token (science pair)",
                [.. moves.Select(m => Tile(m, GameData.Token(m.Token).Name))]),
            DecisionKind.Library => new("library", d.Player, d.Player == session.Seat,
                s.LibraryDrawHidden ? $"The Great Library: which token did {who} keep?" : $"The Great Library: {who} keeps one token",
                [.. moves.Select(m => Tile(m, GameData.Token(m.Token).Name))]),
            DecisionKind.ChooseStarter => new("start", d.Player, d.Player == session.Seat, $"Age {Roman(s.Age)}: {who} chooses who starts",
                [Tile(Move.StartMe, $"{who} starts"), Tile(Move.StartOpponent, $"{Who(session, 1 - d.Player)} starts")]),
            DecisionKind.Destroy => new("destroy", d.Player, d.Player == session.Seat,
                $"{GameData.Wonder(d.Wonder).Name}: {who} destroys one of {Who(session, 1 - d.Player)}'s {s.DestroyColor.ToString()!.ToLowerInvariant()} cards",
                [.. moves.Select(m => Tile(m, GameData.Card(m.Card).Name))]),
            DecisionKind.Mausoleum => new("mausoleum", d.Player, d.Player == session.Seat, $"The Mausoleum: {who} builds a discarded card for free",
                [.. moves.Select(m => Tile(m, GameData.Card(m.Card).Name))]),
            DecisionKind.Messe => new("messe", d.Player, d.Player == session.Seat, $"The Messe: {who} builds a top-row card for free",
                [.. moves.Select(m => Tile(m, GameData.Card(m.Card).Name))]),
            // The keep and the give are entered together, as one of the 6 pairs (spec A8).
            DecisionKind.LibertyKeep => new("liberty-keep", d.Player, d.Player == session.Seat,
                $"Statue of Liberty: which card does {who} keep, and which does {who} give to {Who(session, 1 - d.Player)}? (the third is discarded)",
                [.. moves.SelectMany(keep =>
                {
                    var after = s.Clone();
                    after.Apply(keep);
                    return after.LegalMoves().Select(give => Tile(keep, $"Keep {GameData.Card(keep.Card).Name}, give {GameData.Card(give.Card).Name}")
                        with { Then = $"{p} {give}", Refs = [CardRef(keep.Card), CardRef(give.Card)] });
                })]),
            DecisionKind.LibertyGive => new("liberty-give", d.Player, d.Player == session.Seat,
                $"Statue of Liberty: which card does {who} give to {Who(session, 1 - d.Player)}? (the other is discarded)",
                [.. moves.Select(m => Tile(m, GameData.Card(m.Card).Name))]),
            _ => PantheonText.Decision(session, who, Who(session, 1 - d.Player)) ?? throw new InvalidOperationException($"Unknown decision {d.Kind}"),
        };
    }

    /// <summary>What a decision tile is about, for its readable face and hover details.</summary>
    static string[]? RefsOf(Move m) => m.Kind switch
    {
        MoveKind.Draft => [WonderRef(m.Wonder)],
        MoveKind.Token or MoveKind.Library => [TokenRef(m.Token)],
        MoveKind.Destroy or MoveKind.Mausoleum or MoveKind.Messe or MoveKind.LibertyKeep or MoveKind.LibertyGive => [CardRef(m.Card)],
        _ => null,
    };

    static string[] OfferText(GameState s)
    {
        if (s.DraftOffer != 0) return [.. GameState.WondersIn(s.DraftOffer).Select(w => GameData.Wonder(w).Name)];
        if (!s.LibertyOffer.IsEmpty) return [.. s.LibertyOffer.Cards().Select(c => GameData.Card(c).Name)];
        if (s.LibraryDraw != 0) return [.. ProgressTokens.In(s.LibraryDraw).Select(t => GameData.Token(t).Name)];
        return [];
    }

    // ---------------------------------------------------------------- cities

    static PlayerView PlayerOf(AdvisorSession session, int player)
    {
        var s = session.State;
        var production = string.Join(' ', Enum.GetValues<Resource>()
            .Where(r => s.Production(player, r) > 0)
            .Select(r => string.Concat(Enumerable.Repeat(Icons.Of(r), s.Production(player, r)))));
        var science = Enum.GetValues<ScienceSymbol>()
            .Where(sym => s.SymbolCount(player, sym) > 0)
            .Select(sym => $"{string.Concat(Enumerable.Repeat(Icons.Of(sym), s.SymbolCount(player, sym)))} {CardText.Word(sym)}")
            .ToArray();
        return new PlayerView(
            Index: player,
            Label: Who(session, player),
            You: player == session.Seat,
            First: s.FirstPlayer == player,
            ToDecide: s.Step == StepKind.Decision && s.PlayerToDecide == player,
            Coins: s.Coins(player),
            Vp: Scoring.Total(s, player),
            Production: production,
            Science: science,
            Cards: [.. s.City(player).Cards().Select(Card).OrderBy(c => ColorOrder(c.Color)).ThenBy(c => c.Age)],
            Wonders: [.. GameState.WondersIn(s.Wonders(player)).Select(w => new WonderView(
                GameData.Wonder(w).Id, GameData.Wonder(w).Name, s.IsWonderBuilt(w), s.IsWonderRemoved(w),
                s.WonderCard(w) >= 0 ? Card(s.WonderCard(w)) : null, WonderSummary(GameData.Wonder(w)),
                s.IsWonderBuilt(w) || s.IsWonderRemoved(w) || s.Age == 0 ? null : [CardText.WonderPrice(s, player, w, PriceWho(session, player))]))],
            Tokens: [.. ProgressTokens.In(s.Tokens(player)).Select(Token)]);
    }

    internal static string WonderSummary(WonderDef w)
    {
        var parts = new List<string> { CostText(w.Cost) };
        if (w.VictoryPoints > 0) parts.Add($"{w.VictoryPoints} VP");
        if (w.Coins > 0) parts.Add($"+{w.Coins} coins");
        if (w.OpponentLoses > 0) parts.Add($"opponent −{w.OpponentLoses} coins");
        if (w.Shields > 0) parts.Add($"{w.Shields} shield{(w.Shields > 1 ? "s" : "")}");
        if (w.ProduceChoice.Length > 0) parts.Add(string.Join('/', w.ProduceChoice.Select(Resources.Letter)));
        if (w.PlayAgain) parts.Add("play again");
        if (w.Effect != WonderEffect.None) parts.Add(w.Effect switch
        {
            WonderEffect.DestroyBrown => "destroy a brown card",
            WonderEffect.DestroyGrey => "destroy a grey card",
            WonderEffect.Library => "draw 3 tokens, keep 1",
            WonderEffect.Mausoleum => "build from the discard pile",
            WonderEffect.Messe => "build a top-row card free",
            WonderEffect.Liberty => "the 3 removed cards return",
            WonderEffect.Theater => "look through a Mythology deck, activate one for free",
            _ => w.Effect.ToString(),
        });
        return string.Join(" · ", parts);
    }

    static string CostText(Cost c)
    {
        var res = string.Concat(Enum.GetValues<Resource>().SelectMany(r => Enumerable.Repeat(Resources.Letter(r), c.Resources[(int)r])));
        return c.Coins > 0 ? $"cost ${c.Coins} {res}".TrimEnd() : $"cost {res}";
    }

    static ResultView ResultOf(Result r)
    {
        // Expansion categories (Grand Temples) show only when someone scored them, so a base game's table is unchanged.
        var rows = Enum.GetValues<ScoreCategory>()
            .Where(c => (int)c < ScoreSheet.BaseCategoryCount || r.P1[c] != 0 || r.P2[c] != 0)
            .Select(c => new ScoreRowView(c.ToString(), r.P1[c], r.P2[c])).ToArray();
        var summary = r.IsDraw ? "Draw" : $"P{r.Winner + 1} wins by {r.Victory.ToString().ToLowerInvariant()}";
        return new ResultView(r.Winner, r.Victory.ToString().ToLowerInvariant(), summary, rows, r.P1.Total, r.P2.Total);
    }

    // ---------------------------------------------------------------- small pieces

    static CardView Card(int card)
    {
        var c = GameData.Card(card);
        return new CardView(c.Id, c.Name, Color(c.Color), c.Age);
    }

    static TokenView Token(int token) => new(GameData.Token(token).Id, GameData.Token(token).Name);

    /// <summary>The Progress tokens the setup draws from: the base 10, and Pantheon's 3 in a Pantheon game.</summary>
    static IEnumerable<TokenDef> TokenPool(GameState s) => s.Pantheon is null ? GameData.Tokens : GameData.Tokens.Concat(PantheonData.Tokens);

    static string Color(CardColor c) => c.ToString().ToLowerInvariant();

    static int ColorOrder(string color) => color switch
    {
        "brown" => 0, "grey" => 1, "yellow" => 2, "red" => 3, "green" => 4, "blue" => 5, _ => 6,
    };

    static string Roman(int age) => age switch { 1 => "I", 2 => "II", 3 => "III", _ => age.ToString() };
}
