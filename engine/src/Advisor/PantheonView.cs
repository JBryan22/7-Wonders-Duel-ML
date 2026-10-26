using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor;

// The Pantheon part of the page's JSON (spec-pantheon P-A9, ticket 12): the Pantheon board, the Mythology decks, each
// player's Pantheon holdings, the Pantheon Reveals and decisions. Like the rest of the view it holds no rules: every
// clickable action carries the Move Log line it enters, and everything comes from the session's state, which is the
// owner's view (the opponent's private draws and face-down Divinities are '?').

/// <summary>
/// One field of a Pantheon Reveal form: the page picks one option per field and writes "<see cref="Key"/> option" (or
/// just the option when there is no key) after the Reveal's prefix, in field order.
/// </summary>
public sealed record FieldView(string? Key, string Label, OptionView[] Options);

/// <summary>
/// One Pantheon space (1..6, counted from P1's capital end) with both players' printed costs (<see cref="Costs"/>, P1
/// then P2) and what lies there: <see cref="Face"/> is empty, gate, up (face up), down (face down, known to the owner)
/// or hidden (face down, only its Mythology is known). <see cref="Activate"/> is the activation action while the player
/// to decide takes a card from Age II (disabled with the reason when they can't pay); <see cref="CostsNow"/> what
/// activating it costs each player now (the Gate doubled, the Sanctuary's discount).
/// </summary>
public sealed record PantheonSpaceView(int Number, int[] Costs, string Face, string? Id, string? Name, string? Mythology,
    string? MythologyName, int? Placer, ActionView? Activate, int[]? CostsNow);

/// <summary>A Mythology deck: its size and, top first, what the owner knows of each card ("Zeus", "? (P2 knows)", "?").</summary>
public sealed record PantheonDeckView(string Mythology, string Name, int Size, string[] Cards);

/// <summary>One player's Pantheon holdings: Mythology tokens, Offering tokens (and their worth), Astarte's coins, the Divinities activated.</summary>
public sealed record PantheonHoldingView(int Player, string[] MythologyTokens, string[] Offerings, int OfferingValue, int Astarte,
    string[] Activated);

/// <summary>
/// The Pantheon panel: the 6 spaces, the decks, the cards drawn for a pending placement, Enki's 2 tokens, the Snake's
/// card, Minerva's space, and both players' holdings.
/// </summary>
public sealed record PantheonView(PantheonSpaceView[] Spaces, PantheonDeckView[] Decks, string[] Drawn, string[] Enki, string? Snake,
    int? Minerva, PantheonHoldingView[] Players);

/// <summary>Pantheon names, Move labels and the parts of the view (<see cref="BoardView"/> calls these only in a Pantheon game).</summary>
public static class PantheonText
{
    public static string DivinityName(int d) => d == PantheonState.Hidden ? "a face-down Divinity" : PantheonData.Divinities[d].Name;
    public static string DivinityRef(int d) => $"divinity:{PantheonData.Divinities[d].Id}";
    public static string MythologyName(Mythology m) => PantheonData.MythologyDef(m).Name;

    /// <summary>"2+4 (6 coins' worth)".</summary>
    public static string OfferingNames(int mask) => string.Join(" + ",
        PantheonData.Offerings.Where(o => (mask & 1 << o.Index) != 0).Select(o => o.Name));

    /// <summary>A Military token by the side it lies on: "the 5-coin token on P1's side".</summary>
    public static string MilitaryToken(int t) => $"the {GameState.MilitaryTokenCoins(t)}-coin token on P{t / 2 + 1}'s side";

    /// <summary>A Military track space for Minerva: "space 3 toward P2 (P2's capital at 9)".</summary>
    public static string TrackSpace(int x) => x == 0 ? "the middle space" : $"space {Math.Abs(x)} toward P{(x > 0 ? 2 : 1)}'s capital";

    /// <summary>The cost of activating a face-up card of the Pantheon for a player now, or null if it isn't face up there.</summary>
    public static int? ActivationCost(GameState s, int player, int divinity)
    {
        var p = s.Pantheon!;
        for (var space = 0; space < PantheonData.SpaceCount; space++)
            if (p.Space(space) == divinity && !p.IsFaceDown(space)) return PantheonState.ActivationCost(s, player, divinity);
        return null;
    }

    /// <summary>A Pantheon Move in the owner's words, or null for a base Move.</summary>
    public static string? Label(GameState s, Move m)
    {
        string Card(int c) => GameData.IsCard(c) ? GameData.Card(c).Name : "a card";
        string Wonder(int w) => GameData.IsWonder(w) ? GameData.Wonder(w).Name : "a Wonder";
        switch (m.Kind)
        {
            case MoveKind.Divinity:
                return m.Divinity == PantheonState.Hidden ? $"Place a Divinity face down on space {m.Space + 1}"
                    : $"Place {DivinityName(m.Divinity)} on space {m.Space + 1}";
            case MoveKind.Activate:
            {
                var cost = s.Pantheon is not null && s.PendingCount > 0 ? ActivationCost(s, s.PlayerToDecide, m.Divinity) : null;
                return $"Activate {DivinityName(m.Divinity)}{(cost is { } c ? $" ({(c == 0 ? "free" : $"{c} coins")})" : "")}";
            }
            case MoveKind.Offer:
                return m.Offerings == 0 ? "Pay in coins (no Offering tokens)" : $"Offer {OfferingNames(m.Offerings)} ({PantheonState.OfferingValue(m.Offerings)} coins' worth)";
            case MoveKind.Enki: return $"Enki: keep {GameData.Token(m.Token).Name}";
            case MoveKind.Snake: return $"Nisaba's Snake on {Card(m.Card)}";
            case MoveKind.Steal: return $"Baal: steal {Card(m.Card)}";
            case MoveKind.Hades: return $"Hades: build {Card(m.Card)} from the discard pile";
            case MoveKind.Zeus:
            {
                var named = m.Wonder == s.Age && s.Age > 0;
                var card = named && s.SlotCard(m.Slot) >= 0 ? Card(s.SlotCard(m.Slot)) : "the face-down card";
                return named ? $"Zeus: discard {card} ({s.Layout.SlotName(m.Slot)})" : "Zeus: discard a card of the structure";
            }
            case MoveKind.Anubis: return $"Anubis: unbuild {Wonder(m.Wonder)}";
            case MoveKind.Isis: return $"Isis: build {Wonder(m.Wonder)} with {Card(m.Card)}";
            case MoveKind.Ra: return $"Ra: steal {Wonder(m.Wonder)}";
            case MoveKind.Gate: return $"The Gate: activate {DivinityName(m.Divinity)}";
            case MoveKind.TheaterDeck: return $"Divine Theater: look through the {MythologyName((Mythology)m.Card)} deck";
            case MoveKind.Theater: return $"Divine Theater: activate {DivinityName(m.Divinity)}";
            case MoveKind.TheaterOrder:
                return m.Divinity == PantheonState.Hidden ? "Stack the deck back (unseen)" : $"Put {DivinityName(m.Divinity)} back on top";
            case MoveKind.Minerva: return $"Minerva on {TrackSpace(m.MilitarySpace)}";
            case MoveKind.NeptuneDiscard: return $"Neptune: discard {MilitaryToken(m.MilitaryToken)}";
            case MoveKind.NeptuneApply: return $"Neptune: apply {MilitaryToken(m.MilitaryToken)}";
            default: return null;
        }
    }

    /// <summary>What a Pantheon decision tile is about (for its hover details), or null.</summary>
    public static string[]? Refs(Move m) => m.Kind switch
    {
        MoveKind.Divinity or MoveKind.Activate or MoveKind.Gate or MoveKind.Theater or MoveKind.TheaterOrder
            when m.Divinity >= 0 => [DivinityRef(m.Divinity)],
        MoveKind.Enki => [$"token:{GameData.Token(m.Token).Id}"],
        MoveKind.Snake or MoveKind.Steal or MoveKind.Hades when GameData.IsCard(m.Card) => [$"card:{GameData.Card(m.Card).Id}"],
        MoveKind.Anubis or MoveKind.Ra => [$"wonder:{GameData.Wonder(m.Wonder).Id}"],
        MoveKind.Isis => [$"card:{GameData.Card(m.Card).Id}", $"wonder:{GameData.Wonder(m.Wonder).Id}"],
        _ => null,
    };

    // ---------------------------------------------------------------- the Pantheon panel

    public static PantheonView Board(AdvisorSession session)
    {
        var s = session.State;
        var p = s.Pantheon!;
        var takeCard = s.Step == StepKind.Decision && s.Pending(0).Kind == DecisionKind.TakeCard && s.Age >= 2;
        var spaces = PantheonData.Spaces.Select(def =>
        {
            var i = def.Index;
            var value = p.Space(i);
            var costs = new[] { def.Cost(0), def.Cost(1) };
            if (value == PantheonState.EmptySpace)
                return new PantheonSpaceView(def.Number, costs, "empty", null, null, null, null, null, null, null);
            var myth = p.SpaceMythology(i);
            var mythId = myth is { } m1 ? PantheonData.MythologyDef(m1).Id : null;
            var mythName = myth is { } m2 ? MythologyName(m2) : null;
            if (p.IsHidden(i))
                return new PantheonSpaceView(def.Number, costs, "hidden", null, null, mythId, mythName, p.Placer(i), null, null);
            var d = PantheonData.Divinities[value];
            if (p.IsFaceDown(i))
                return new PantheonSpaceView(def.Number, costs, "down", d.Id, d.Name, mythId, mythName, p.Placer(i), null, null);
            var now = new[] { PantheonState.ActivationCost(s, 0, value), PantheonState.ActivationCost(s, 1, value) };
            ActionView? activate = null;
            if (takeCard)
            {
                var player = s.PlayerToDecide;
                var move = Move.Activate(value);
                var why = s.WhyIllegal(move);
                activate = new ActionView($"Activate ({(now[player] == 0 ? "free" : $"{now[player]} coins")})", $"P{player + 1} {move}",
                    why is null, why, "activate", Refs: [DivinityRef(value)]);
            }
            return new PantheonSpaceView(def.Number, costs, d.IsGate ? "gate" : "up", d.Id, d.Name, mythId, mythName,
                d.IsGate ? null : p.Placer(i), activate, now);
        }).ToArray();

        var decks = PantheonData.Mythologies.Select(m => new PantheonDeckView(m.Id, m.Name, p.DeckSize(m.Mythology),
            [.. Enumerable.Range(0, p.DeckSize(m.Mythology)).Select(pos =>
            {
                var card = p.DeckCard(m.Mythology, pos);
                if (card >= 0) return PantheonData.Divinities[card].Name;
                var who = p.DeckKnownBy(m.Mythology, pos);
                return who switch { 0 => "?", 3 => "? (both know)", _ => $"? (P{(who == 1 ? 1 : 2)} knows)" };
            })])).ToArray();

        var drawn = !p.HasDraw ? [] : Enumerable.Range(0, 2).Select(p.Drawn).Where(d => d != -1)
            .Select(d => d == PantheonState.Hidden ? "?" : PantheonData.Divinities[d].Name).ToArray();
        var players = Enumerable.Range(0, 2).Select(player => new PantheonHoldingView(player,
            [.. PantheonData.Mythologies.SelectMany(m => Enumerable.Repeat(m.Name, p.MythologyTokens(player, m.Mythology)))],
            [.. PantheonData.Offerings.Where(o => (p.OfferingTokens(player) & 1 << o.Index) != 0).Select(o => o.Name)],
            PantheonState.OfferingValue(p.OfferingTokens(player)), p.AstarteCoins(player),
            [.. PantheonData.Divinities.Where(d => (p.Activated(player) & 1 << d.Index) != 0).Select(d => d.Name)])).ToArray();
        return new PantheonView(spaces, decks, drawn,
            [.. ProgressTokens.In(p.Enki).Select(t => GameData.Token(t).Name)],
            p.Snake >= 0 ? GameData.Card(p.Snake).Name : null,
            p.Minerva == PantheonState.NoMinerva ? null : p.Minerva,
            players);
    }

    /// <summary>The Mythology or Offering token on a structure slot of the current Age: its slug and name, or nulls.</summary>
    public static (string? Id, string? Name) SlotToken(GameState s, int slot)
    {
        var code = s.Pantheon?.SlotToken(slot) ?? PantheonState.NoToken;
        if (code == PantheonState.NoToken) return (null, null);
        return code >= PantheonState.OfferingToken
            ? (PantheonData.Offerings[code - PantheonState.OfferingToken].Id, PantheonData.Offerings[code - PantheonState.OfferingToken].Name)
            : (PantheonData.Mythologies[code].Id, $"{PantheonData.Mythologies[code].Name} token");
    }

    // ---------------------------------------------------------------- Reveals

    /// <summary>
    /// The Pantheon Reveal the game waits for, as a picker for the owner, or null when it waits for none. The opponent's
    /// private draws never get here: the session logs them redacted.
    /// </summary>
    public static RevealView? Reveal(AdvisorSession session)
    {
        var s = session.State;
        var p = s.Pantheon!;
        if (s.SlotsToReveal != 0) return null;
        static OptionView Opt(int d, bool enabled, string? why) =>
            new(PantheonData.Divinities[d].Id, PantheonData.Divinities[d].Name, null, enabled, why, DivinityRef(d));
        if (p.AwaitingZeusDiscard)
        {
            var pool = s.UnseenPoolFor(p.ZeusTarget);
            return new RevealView("pick", $"Zeus discards the face-down card in {s.Layout.SlotName(p.ZeusTarget)}: which card is it?", 1,
                "reveal discarded",
                [.. CardsFor(s, p.ZeusTarget).Select(c => new OptionView(c.Id, c.Name, c.Color.ToString().ToLowerInvariant(),
                    pool.Contains(c.Index), pool.Contains(c.Index) ? null : "already revealed", $"card:{c.Id}"))]);
        }
        if (p.AwaitingGateReveal)
            return new RevealView("fields", "The Gate: name the top card of each deck (it is shown to both players)", 0, "reveal gate", [],
                [.. PantheonData.Mythologies.Where(m => p.DeckSize(m.Mythology) > 0)
                    .Select(m => new FieldView(null, $"{m.Name} deck, top card", DeckOptions(p, m.Mythology, 0, Opt)))]);
        if (p.AwaitingTheaterReveal)
        {
            var m = p.TheaterDeck;
            var player = s.Pending(0).Player;
            return new RevealView("fields", $"Your Divine Theater: name the cards of the {MythologyName(m)} deck, top card first (only you see them)", 0,
                $"reveal P{player + 1} theater {PantheonData.MythologyDef(m).Id}", [],
                [.. Enumerable.Range(0, p.DeckSize(m)).Select(pos => new FieldView(null, Ordinal(pos), DeckOptions(p, m, pos, Opt)))]);
        }
        if (p.AwaitingMythologyTokens)
            return new RevealView("fields", "Pantheon: name the Mythology token on each of the 5 marked Age I cards", 0, "reveal myth-tokens", [],
                [.. PantheonData.MythologySlots.Select(slot => new FieldView(GameData.Layout(1).SlotName(slot), $"Slot {GameData.Layout(1).SlotName(slot)}",
                    [.. PantheonData.Mythologies.Select(m => new OptionView(m.Id, m.Name, null, true, null))]))]);
        if (p.AwaitingPantheon)
            return new RevealView("fields", "Age II: the face-down Divinities are turned face up. Name each one", 0, "reveal pantheon", [],
                [.. Enumerable.Range(0, PantheonData.SpaceCount).Where(p.IsFaceDown).Select(space =>
                {
                    var m = p.SpaceMythology(space)!.Value;
                    var known = p.IsHidden(space) ? -1 : p.Space(space);
                    var unseen = Unseen(p, m);
                    return new FieldView((space + 1).ToString(), $"Space {space + 1} ({MythologyName(m)}, placed by P{p.Placer(space) + 1})",
                        [.. PantheonData.MythologyDef(m).Divinities.Select(d => known >= 0
                            ? Opt(d, d == known, d == known ? null : $"space {space + 1} holds {PantheonData.Divinities[known].Name}")
                            : Opt(d, unseen.Contains(d), unseen.Contains(d) ? null : "already seen elsewhere"))]);
                })]);
        if (p.AwaitingOfferings)
            return new RevealView("fields", "Age II: name the Offering token on each of the 3 marked cards", 0, "reveal offerings", [],
                [.. PantheonData.OfferingSlots.Select(slot => new FieldView(GameData.Layout(2).SlotName(slot), $"Slot {GameData.Layout(2).SlotName(slot)}",
                    [.. PantheonData.Offerings.Select(o => new OptionView(o.Id, o.Name, null, true, null))]))]);
        if (p.AwaitingEnki)
            return new RevealView("tokens", "Enki is face up: name the 2 Progress tokens drawn from the box onto it", 2, "reveal enki",
                [.. ProgressTokens.In(s.TokensSetAside).Select(t => new OptionView(GameData.Token(t).Id, GameData.Token(t).Name, null, true, null,
                    $"token:{GameData.Token(t).Id}"))]);
        if (s.PendingCount > 0 && s.Pending(0).Kind == DecisionKind.PlaceDivinity && !p.HasDraw)
        {
            var d = s.Pending(0);
            var m = (Mythology)d.Wonder;
            var count = Math.Min(2, p.DeckSize(m));
            return new RevealView("fields", $"You took a {MythologyName(m)} token: name the {(count == 2 ? "2 Divinities" : "Divinity")} you drew (top card first; only you see them)", 0,
                $"reveal P{d.Player + 1} divinities {PantheonData.MythologyDef(m).Id}", [],
                [.. Enumerable.Range(0, count).Select(pos => new FieldView(null, Ordinal(pos), DeckOptions(p, m, pos, Opt)))]);
        }
        return null;
    }

    static string Ordinal(int pos) => pos switch { 0 => "Top card", 1 => "Second card", _ => "Third card" };

    static int[] Unseen(PantheonState p, Mythology m) =>
        [.. PantheonData.MythologyDef(m).Divinities.Where(d => (p.SeenDivinities & 1 << d) == 0)];

    /// <summary>A deck position's options: the card the owner knows is there, or the Mythology's unseen cards.</summary>
    static OptionView[] DeckOptions(PantheonState p, Mythology m, int pos, Func<int, bool, string?, OptionView> opt)
    {
        var known = p.DeckCard(m, pos);
        var unseen = Unseen(p, m);
        return [.. PantheonData.MythologyDef(m).Divinities.Select(d => known >= 0
            ? opt(d, d == known, d == known ? null : $"you know {PantheonData.Divinities[known].Name} is there")
            : opt(d, unseen.Contains(d), unseen.Contains(d) ? null : "already seen elsewhere"))];
    }

    /// <summary>What a face-down slot can hold: Grand Temples behind a Temple back, else the Age's cards.</summary>
    static IEnumerable<CardDef> CardsFor(GameState s, int slot) =>
        (s.SlotCard(slot) == GameState.HiddenGuild ? PantheonData.TempleCards : GameData.Cards.Where(c => !c.IsGuild && c.Age == s.Age))
        .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- decisions

    /// <summary>A Pantheon Pending Decision as tiles (every legal Move, labelled), or null for a base decision.</summary>
    public static DecisionView? Decision(AdvisorSession session, string who, string other)
    {
        var s = session.State;
        var d = s.Pending(0);
        var you = d.Player == session.Seat;
        var p = $"P{d.Player + 1}";
        var moves = s.LegalMoves();
        ActionView Tile(Move m) => new(Label(s, m) ?? m.ToString(), $"{p} {m}", true, null, m.Kind.ToString().ToLowerInvariant(), Refs: Refs(m));
        DecisionView View(string kind, string title) => new(kind, d.Player, you, title, [.. moves.Select(Tile)]);
        var pan = s.Pantheon!;
        return d.Kind switch
        {
            // Owner ruling 2026-10-26: the placement comes before the uncovered cards are flipped.
            DecisionKind.PlaceDivinity => View("divinity", (pan.DrawHidden
                ? $"{who} places one of the {MythologyName((Mythology)d.Wonder)} Divinities they drew face down: on which space?"
                : $"{who} places a drawn Divinity face down on an empty Pantheon space (the other goes back on top of the deck)")
                + (s.FlipsHeld ? " Do this before flipping the uncovered cards." : "")),
            DecisionKind.Offer => View("offer", $"Activating {DivinityName(d.Wonder)} costs {PantheonState.ActivationCost(s, d.Player, d.Wonder)} coins: which Offering tokens does {who} spend?"),
            DecisionKind.Enki => View("enki", $"Enki: {who} keeps one of the 2 Progress tokens (the other goes back to the box)"),
            DecisionKind.Snake => View("snake", $"Nisaba: {who} puts the Snake on one of {other}'s green cards"),
            DecisionKind.Steal => View("steal", $"Baal: {who} steals one of {other}'s brown or grey cards"),
            DecisionKind.Hades => View("hades", $"Hades: {who} builds a card from the discard pile for free"),
            DecisionKind.Zeus => View("zeus", $"Zeus: {who} discards a card of the structure (and takes any token on it)"),
            DecisionKind.Anubis => View("anubis", $"Anubis: {who} unbuilds a Wonder (either player's)"),
            DecisionKind.Isis => View("isis", $"Isis: {who} builds a Wonder for free with a card from the discard pile"),
            DecisionKind.Ra => View("ra", $"Ra: {who} steals one of {other}'s unbuilt Wonders"),
            DecisionKind.Gate => View("gate", $"The Gate: {who} activates one of the deck tops"),
            DecisionKind.TheaterDeck => View("theater-deck", $"The Divine Theater: which Mythology deck does {who} look through?"),
            DecisionKind.Theater => View("theater", $"The Divine Theater: which {MythologyName((Mythology)d.Wonder)} Divinity does {who} activate?"),
            DecisionKind.TheaterOrder => View("theater-order", you
                ? "The Divine Theater: which unused card goes back on top of the deck?"
                : $"The Divine Theater: {who} stacks the unused cards back (you don't see the order)"),
            DecisionKind.Minerva => View("minerva", $"Minerva: on which Military space does {who} put her? (the Conflict pawn stops before her once)"),
            DecisionKind.NeptuneDiscard => View("neptune-discard", $"Neptune: which Military token does {who} discard?"),
            DecisionKind.NeptuneApply => View("neptune-apply", $"Neptune: which Military token does {who} apply? ({other} loses its coins)"),
            _ => null,
        };
    }
}
