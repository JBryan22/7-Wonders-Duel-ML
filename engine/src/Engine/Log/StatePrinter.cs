using System.Text;
using SevenWondersDuel.Engine.Data;

namespace SevenWondersDuel.Engine;

/// <summary>A plain-text picture of a Game State, for `7wd replay` and debugging.</summary>
public static class StatePrinter
{
    public static string Describe(GameState s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Age == 0 ? "Setup" : $"Age {s.Age}").Append(" | ");
        sb.Append(s.Step switch
        {
            StepKind.GameOver => "game over",
            StepKind.Reveal => "waiting for a Reveal",
            _ => $"P{s.PlayerToDecide + 1} to decide: {s.Pending(0).Kind}",
        });
        sb.Append('\n');

        if (s.Age > 0 && s.CardsLeftInAge > 0)
        {
            sb.Append("Layout (* = accessible, ? = unknown, ?guild = unknown Guild):\n");
            var layout = s.Layout;
            for (var row = 0; row < layout.RowCount; row++)
            {
                sb.Append("  ");
                foreach (var slot in layout.Slots.Where(x => x.Row == row).OrderBy(x => x.Col))
                {
                    var card = s.SlotCard(slot.Index);
                    var text = card switch
                    {
                        GameState.Empty => "--",
                        GameState.Hidden => "?",
                        GameState.HiddenGuild => "?guild",
                        _ => GameData.Cards[card].Id,
                    };
                    sb.Append($"{layout.SlotName(slot.Index)} {text}{(s.IsAccessible(slot.Index) ? "*" : "")}  ");
                }
                sb.Append('\n');
            }
        }

        for (var p = 0; p < 2; p++)
        {
            var production = string.Join(' ', Enum.GetValues<Resource>()
                .Where(r => s.Production(p, r) > 0)
                .Select(r => $"{Resources.Letter(r)}{s.Production(p, r)}"));
            sb.Append($"P{p + 1}{(p == s.FirstPlayer ? " (first)" : "")}: {s.Coins(p)} coins")
              .Append($" | production: {(production.Length > 0 ? production : "none")}")
              .Append($" | city: {(s.City(p).IsEmpty ? "empty" : s.City(p).ToString())}");
            if (s.Tokens(p) != 0) sb.Append($" | tokens: {ProgressTokens.Names(s.Tokens(p))}");
            if (s.DistinctSymbols(p) > 0) sb.Append($" | science symbols: {s.DistinctSymbols(p)}");
            if (s.Wonders(p) != 0)
                sb.Append(" | wonders: ").Append(string.Join(", ", GameState.WondersIn(s.Wonders(p)).Select(w =>
                    GameData.Wonders[w].Id + (s.IsWonderBuilt(w) ? $" (built with {GameData.Cards[s.WonderCard(w)].Id})"
                        : s.IsWonderRemoved(w) ? " (removed)" : ""))));
            sb.Append('\n');
        }
        if (s.LibraryDraw != 0)
            sb.Append($"Great Library draw (P{s.PlayerToDecide + 1} only): {ProgressTokens.Names(s.LibraryDraw)}\n");
        if (s.LibraryDrawHidden) sb.Append($"Great Library draw: P{s.PlayerToDecide + 1} drew 3 hidden tokens\n");
        if (s.DestroyColor is { } color) sb.Append($"P{s.PlayerToDecide + 1} destroys one of P{2 - s.PlayerToDecide}'s {color.ToString().ToLowerInvariant()} cards\n");
        if (s.DraftOffer != 0)
            sb.Append($"Draft offer: {string.Join(' ', GameState.WondersIn(s.DraftOffer).Select(w => GameData.Wonders[w].Id))}\n");
        if (s.PlayAgain) sb.Append($"P{s.CurrentPlayer + 1} plays again after this turn\n");
        if (s.TokensSetOut != 0)
            sb.Append($"Progress tokens on the board: {(s.BoardTokens != 0 ? ProgressTokens.Names(s.BoardTokens) : "none")}\n");
        if (s.Age > 0)
        {
            var tokens = string.Join(' ', Enumerable.Range(0, GameState.MilitaryTokenCount).Where(s.MilitaryTokenLeft)
                .Select(t => $"P{t / 2 + 1}:{GameState.MilitaryTokenCoins(t)}"));
            var pawn = s.ConflictPawn == 0 ? "centre" : $"{Math.Abs(s.ConflictPawn)} toward P{(s.ConflictPawn > 0 ? 2 : 1)}";
            sb.Append($"Conflict Pawn: {pawn} | military tokens left: {(tokens.Length > 0 ? tokens : "none")}\n");
        }
        sb.Append($"Discard pile: {(s.DiscardPile.IsEmpty ? "empty" : s.DiscardPile.ToString())}\n");
        if (s.Result is { } result) sb.Append("Result: ").Append(result).Append('\n');
        return sb.ToString();
    }
}
