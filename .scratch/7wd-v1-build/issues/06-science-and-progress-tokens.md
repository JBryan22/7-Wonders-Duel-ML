# 06: Science and Progress tokens

**What to build:** Science symbols and pairs, the setup Progress-token Reveal, the compulsory token Pending Decision, all 10 token effects, and science victory.

**Blocked by:** 04

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] A token pick is mandatory whenever tokens remain
- [x] Rule tests for each of the 10 tokens, including Strategy, Urbanism, Law (7th symbol), Architecture/Masonry discounts
- [x] Science victory on 6 distinct symbols

## Notes

- Code: `Rules/ProgressTokens.cs` (token indices, constants, mask helpers); science and tokens in `GameState` (`SymbolCount`, `DistinctSymbols`, `GainSymbol`, internal `TakeToken`, `WonderPlaysAgain`); `Payment.QuoteWonder` and the discount; `Scoring.ProgressPoints`.
- Setup order is now `reveal first P1` → `reveal tokens a b c d e` (5 slugs, index order) → Age I. The token Reveal calls `StartAge(1)`; ticket 07 must put the Wonder draft between them. `TokensSetAside` (the 5 never named) is the Great Library's pool; its pick should go through `TakeToken` (it handles coins and Law → supremacy).
- Token pick: `DecisionKind.ChooseToken`, Move `P1 token law` (`Move.PickToken`; the token index sits in `Move.Card`, read it via `Move.Token`). Pushed after the build; flips of newly accessible cards are revealed before it. No pass Move exists. After the pick, an empty queue ends the turn as usual.
- `GainSymbol` runs from `Build` for any green card, so Mausoleum/Messe/Liberty free builds (tickets 07/12/13) get pairs and science wins if they go through `Build`. Supremacy is checked before the pair; the queue is cleared on any win.
- Discounts: Masonry (blue cards) and Architecture (`QuoteWonder`) are 2 wildcard units in `Payment.Cheapest`: choice units are placed exhaustively, then wildcards take the dearest remaining resources (optimal). Ticket 07 adds Wonder choice producers to `QuoteCost`'s units and must use `QuoteWonder` (with Economy: pass `TradeCoins` to an opponent owning Economy, as `Build` does) and `AddShields` for Wonder shields (no Strategy bonus there).
- Urbanism +4 uses `Payment.IsChained`. Theology: `WonderPlaysAgain(player, wonder)`.
- Tests: 165 Engine (adds ScienceTests and ProgressTokenTests: setup Reveal, compulsory pick, empty board, Law supremacy, 6-symbol win, every token's effect) and 9 Agents (random games now include science wins; token conservation over 2k games).
