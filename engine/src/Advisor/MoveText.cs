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
        string Card(int c) => GameData.Cards[c].Name;
        string Effect(int c) => CardText.Card(GameData.Cards[c]).EffectIcons;
        string WonderName(int w) => GameData.Wonders[w].Name;
        string Token(int t) => GameData.Tokens[t].Name;
        int? SlotOf(int card)
        {
            for (var slot = 0; slot < GameState.SlotCount; slot++)
                if (before.SlotCard(slot) == card) return slot;
            return null;
        }
        var age = before.Age;
        string Paid(int total) => total == 0 ? "for free" : $"for {total}{Icons.Coin}";
        var cid = move.Card >= 0 && move.Card < GameData.Cards.Length ? GameData.Cards[move.Card].Id : null;
        return move.Kind switch
        {
            MoveKind.Build => Payment.IsChained(before, ai, move.Card)
                ? new($"AI built {Card(move.Card)} free (chain from {Card(GameData.Cards[move.Card].FreeIf)}) · {Effect(move.Card)}",
                    SlotOf(move.Card), age, cid, null, null)
                : new($"AI built {Card(move.Card)} {Paid(Payment.Quote(before, ai, move.Card).Total)} · {Effect(move.Card)}",
                    SlotOf(move.Card), age, cid, null, null),
            MoveKind.Discard => new($"AI discarded {Card(move.Card)} for +{2 + before.YellowCards(ai)}{Icons.Coin}",
                SlotOf(move.Card), age, cid, null, null),
            MoveKind.Wonder => new($"AI built {WonderName(move.Wonder)} using {Card(move.Card)} {Paid(Payment.QuoteWonder(before, ai, move.Wonder).Total)}"
                                   + $" · {CardText.Wonder(GameData.Wonders[move.Wonder]).EffectIcons}",
                SlotOf(move.Card), age, null, GameData.Wonders[move.Wonder].Id, null),
            MoveKind.Draft => new($"AI drafted {WonderName(move.Wonder)}", null, age, null, GameData.Wonders[move.Wonder].Id, null),
            MoveKind.Token => new($"AI took Progress token: {Token(move.Token)}", null, age, null, null, GameData.Tokens[move.Token].Id),
            MoveKind.Library => new($"AI kept Progress token {Token(move.Token)} from its Great Library draw", null, age, null, null,
                GameData.Tokens[move.Token].Id),
            MoveKind.StartMe => new($"AI chose to start Age {Roman(before.Age)} itself", null, age, null, null, null),
            MoveKind.StartOpponent => new($"AI chose: you start Age {Roman(before.Age)}", null, age, null, null, null),
            MoveKind.Destroy => new($"AI destroyed your {Card(move.Card)}", null, age, null, null, null),
            MoveKind.Mausoleum => new($"AI built {Card(move.Card)} from the discard pile for free (The Mausoleum)", null, age, cid, null, null),
            MoveKind.Messe => new($"AI built {Card(move.Card)} from the top row for free (The Messe)", SlotOf(move.Card), age, cid, null, null),
            MoveKind.LibertyKeep => new($"AI kept {Card(move.Card)} (Statue of Liberty)", null, age, cid, null, null),
            MoveKind.LibertyGive => new($"AI gave you {Card(move.Card)} (Statue of Liberty)", null, age, null, null, null),
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
