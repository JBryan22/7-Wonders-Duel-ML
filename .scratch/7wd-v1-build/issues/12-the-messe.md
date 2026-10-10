# 12: The Messe

**What to build:** The promo= switch and the 12/13/14 Wonder pool, and The Messe (messe <card>, top row even covered).

**Blocked by:** 09

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Messe with covered cards and with an empty top row
- [x] Invariants hold with promo=messe

## Notes

- Pool: `GameState.WonderPool` = `GameState.PoolFor(Rules.Promos)`: the 12 base Wonders plus each switched-on promo (a promo Wonder maps to its switch by its `effect`: messe / liberty). Draft Reveals outside the pool are rejected; `UnseenWonders` and `RandomChanceSource` follow the pool, so nothing else changed. `GameState.New` now refuses only `promo=liberty` (ticket 13 removes that guard; `PoolFor` already gives 13/14 with Liberty).
- Messe: `BuildWonder` queues `DecisionKind.Messe` (with the Wonder, like the other choice powers) before `EndTurn`; `MoveKind.Messe` / `Move.Messe(card)`, logged `P1 messe tavern`. The parser also takes a slot (`P1 messe 0.1`), since top-row cards are always face up; it is written by card name. Options = `GameState.TopRowSlots` (occupied row-0 slots, covered or not). Applying it empties the slot (no flip: nothing lies under row 0, so no Reveal and no hidden info; `Observe` needs no redaction) and calls `Build(free: true)`: no cost, no Urbanism, card effects including Strategy, science pairs (token decision queued behind) and wins. A Theology play-again comes after the choice. Taking the Age's last card ends the Age.
- Empty top row: in all three layouts row 0 lies under every other row, so it is empty only when the whole structure is; that happens only when the Messe is built with the Age's last card. Then the power is skipped without a decision (promo-wonders §2.6) and the Age ends.
- Soak: `7wd soak --promo messe` / `7wd random --promo messe` (the value is the header's promo list); `Soak.Run(..., promos:)`, `Soak.RulesFor(seed, promos)`. The taken-cards bookkeeping counts Messe Moves. Windows desktop: 100k promo=messe games in 9.0 s, all invariants hold (96,634 civilian incl. 144 draws, 3,270 military, 96 science); the base 100k soak gives exactly ticket 10's numbers, the determinism hashes and frozen C++ games are unchanged. CI soaks 3k Messe games.
- `StatePrinter` shows a pending Messe choice. Encoding (18) and the Heuristic (15) still need a Messe action head / value.
- Tests: 300 Engine (new `MesseTests`: pool sizes and switch, draft rejection, random drafts, covered top-row build, free build with Strategy, science pair, Theology order, illegal targets, slot form, last card ends the Age, empty top row skipped) and 33 Agents (3k Messe soak; Messe drafted/built/resolved in random games).
