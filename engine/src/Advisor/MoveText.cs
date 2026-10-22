using SevenWondersDuel.Engine;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Advisor;

/// <summary>
/// One of the AI's Moves in plain words for the big "AI played" banner (ticket 37), with what to highlight on the
/// board: the pyramid slot the card came from (only while that Age's pyramid is still shown) and the card, Wonder or
/// token it went to. Everything named here was public when it happened (a card taken from the pyramid was face up).
/// </summary>
public sealed record AiMoveView(string Text, int? Slot, int Age, string? Card, string? Wonder, string? Token);

public static class MoveText
{
    /// <summary>The AI's Move in the owner's words, priced from the state just before it.</summary>
    public static AiMoveView Describe(GameState before, Move move, int ai)
    {
        string Card(int c) => GameData.Card(c).Name;
        string Effect(int c) => CardText.Card(GameData.Card(c)).EffectIcons;
        string WonderName(int w) => GameData.Wonder(w).Name;
        string Token(int t) => GameData.Token(t).Name;
        int? SlotOf(int card)
        {
            for (var slot = 0; slot < GameState.SlotCount; slot++)
                if (before.SlotCard(slot) == card) return slot;
            return null;
        }
        var age = before.Age;
        string Paid(int total) => total == 0 ? "for free" : $"for {total}{Icons.Coin}";
        var cid = move.TakesCard || move.Kind is MoveKind.Mausoleum or MoveKind.Messe or MoveKind.LibertyKeep or MoveKind.Hades or MoveKind.Steal
            ? GameData.IsCard(move.Card) ? GameData.Card(move.Card).Id : null : null;
        return move.Kind switch
        {
            MoveKind.Build => Payment.IsChained(before, ai, move.Card)
                ? new($"AI built {Card(move.Card)} free (chain from {Card(GameData.Card(move.Card).FreeIf)}) · {Effect(move.Card)}",
                    SlotOf(move.Card), age, cid, null, null)
                : new($"AI built {Card(move.Card)} {Paid(Payment.Quote(before, ai, move.Card).Total)} · {Effect(move.Card)}",
                    SlotOf(move.Card), age, cid, null, null),
            MoveKind.Discard => new($"AI discarded {Card(move.Card)} for +{2 + before.YellowCards(ai)}{Icons.Coin}",
                SlotOf(move.Card), age, cid, null, null),
            MoveKind.Wonder => new($"AI built {WonderName(move.Wonder)} using {Card(move.Card)} {Paid(Payment.QuoteWonder(before, ai, move.Wonder).Total)}"
                                   + $" · {CardText.Wonder(GameData.Wonder(move.Wonder)).EffectIcons}",
                SlotOf(move.Card), age, null, GameData.Wonder(move.Wonder).Id, null),
            MoveKind.Draft => new($"AI drafted {WonderName(move.Wonder)}", null, age, null, GameData.Wonder(move.Wonder).Id, null),
            MoveKind.Token => new($"AI took Progress token: {Token(move.Token)}", null, age, null, null, GameData.Token(move.Token).Id),
            MoveKind.Library => new($"AI kept Progress token {Token(move.Token)} from its Great Library draw", null, age, null, null,
                GameData.Token(move.Token).Id),
            MoveKind.StartMe => new($"AI chose to start Age {Roman(before.Age)} itself", null, age, null, null, null),
            MoveKind.StartOpponent => new($"AI chose: you start Age {Roman(before.Age)}", null, age, null, null, null),
            MoveKind.Destroy => new($"AI destroyed your {Card(move.Card)}", null, age, null, null, null),
            MoveKind.Mausoleum => new($"AI built {Card(move.Card)} from the discard pile for free (The Mausoleum)", null, age, cid, null, null),
            MoveKind.Messe => new($"AI built {Card(move.Card)} from the top row for free (The Messe)", SlotOf(move.Card), age, cid, null, null),
            MoveKind.LibertyKeep => new($"AI kept {Card(move.Card)} (Statue of Liberty)", null, age, cid, null, null),
            MoveKind.LibertyGive => new($"AI gave you {Card(move.Card)} (Statue of Liberty)", null, age, null, null, null),
            // Pantheon (ticket 12): a face-down placement or a restack shows only what the owner sees (the log's redacted Move).
            MoveKind.Divinity => new(move.Divinity == PantheonState.Hidden
                ? $"AI placed a Divinity face down on Pantheon space {move.Space + 1}"
                : $"AI placed {PantheonText.DivinityName(move.Divinity)} on Pantheon space {move.Space + 1}", null, age, null, null, null),
            MoveKind.Activate => new($"AI activated {PantheonText.DivinityName(move.Divinity)}"
                                     + (PantheonText.ActivationCost(before, ai, move.Divinity) is { } c ? $" {Paid(c)}" : ""), null, age, null, null, null),
            MoveKind.Steal => new($"AI stole your {Card(move.Card)} (Baal)", null, age, cid, null, null),
            MoveKind.Hades => new($"AI built {Card(move.Card)} from the discard pile for free (Hades)", null, age, cid, null, null),
            MoveKind.Snake => new($"AI put Nisaba's Snake on your {Card(move.Card)}: its science symbol counts for the AI too", null, age, null, null, null),
            MoveKind.Ra => new($"AI stole your Wonder {WonderName(move.Wonder)} (Ra)", null, age, null, GameData.Wonder(move.Wonder).Id, null),
            MoveKind.Isis => new($"AI built {WonderName(move.Wonder)} with {Card(move.Card)} from the discard pile (Isis)", null, age, null,
                GameData.Wonder(move.Wonder).Id, null),
            MoveKind.Enki => new($"AI kept Progress token {Token(move.Token)} (Enki)", null, age, null, null, GameData.Token(move.Token).Id),
            MoveKind.Zeus => new($"AI played {PantheonText.Label(before, move)}", move.Wonder == before.Age ? move.Slot : null, age, null, null, null),
            _ when move.Kind > MoveKind.LibertyGive => new($"AI played {PantheonText.Label(before, move)}", null, age, null, null, null),
            _ => new($"AI played {AdviceTable.Label(before, move)}", null, age, null, null, null),
        };
    }

    /// <summary>
    /// The AI's Moves since the owner's last Move, oldest first (a "play again" or a Wonder power can make several), by
    /// replaying the log. Empty when the owner moved last or in a game entered by hand.
    /// </summary>
    public static AiMoveView[] SinceOwnersMove(AdvisorSession session)
    {
        if (session.Ai is null) return [];
        var log = session.Log;
        var lastOwn = -1;
        for (var i = 0; i < log.Count; i++)
            if (log[i] is MoveEntry m && m.Player == session.Seat) lastOwn = i;
        if (!Enumerable.Range(lastOwn + 1, log.Count - lastOwn - 1).Any(i => log[i] is MoveEntry m && m.Player == session.AiSeat))
            return [];
        var game = new Game(session.Rules);
        var moves = new List<AiMoveView>();
        for (var i = 0; i < log.Count; i++)
        {
            if (i > lastOwn && log[i] is MoveEntry m && m.Player == session.AiSeat)
                moves.Add(Describe(game.State, m.Move, session.AiSeat));
            game.Apply(log[i]);
        }
        return [.. moves];
    }

    static string Roman(int age) => age switch { 1 => "I", 2 => "II", 3 => "III", _ => age.ToString() };
}
