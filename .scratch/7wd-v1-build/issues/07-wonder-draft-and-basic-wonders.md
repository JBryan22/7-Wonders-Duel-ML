# 07: Wonder draft and basic Wonders

**What to build:** The 8-Wonder draft (A-B-B-A, offer Reveals), building Wonders with an Age card, play-again turns after Reveals, play-again lost at end of Age, the 8th Wonder removed once 7 are built, and the Wonders without extra choices.

**Blocked by:** 04

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Draft order tests
- [x] Play-again happens after newly accessible cards are revealed
- [x] Only 7 Wonders can be built in total
- [x] Rule tests for each simple Wonder

## Notes

- Setup order is now `reveal first P1` → `reveal tokens ...` → `reveal draft a b c d` → 4 `Pn draft <wonder>` Moves → `reveal draft e f g h` → 4 more → Age I (the draft's first player starts). Picks are A-B-B-A, A = first player in round 1 and the second player in round 2 (`GameState.DraftPicker`). The last Wonder of a round is still a logged one-option Move. `DecisionKind.DraftWonder`; `AwaitingDraftOffer`, `DraftOffer`, `UnseenWonders` (the pool never offered: what a draft Reveal draws from, and what the Determinizer must fill during the draft).
- `Move` gained a third field `Wonder` (default -1): `Move.Draft(w)` (Card -1) and `Move.BuildWonder(card, w)` (`P1 wonder baths sphinx`). `TakesCard` includes Wonder Moves. Legal Wonder Moves = each accessible card × each owned, unbuilt, not removed, affordable Wonder.
- Wonder state: bitmasks per player `Wonders(p)` (drafted), `BuiltWonders(p)`, global `RemovedWonders`; `WonderCard(w)` / `CardsUnderWonders(p)` give the tucked Age card (never in the city or discard pile). `WonderPool` is the 12 base Wonders; tickets 12/13 add the promo switches there.
- `BuildWonder` pays via `Payment.QuoteWonder` (Architecture; trading coins to an opponent owning Economy), then coins, opponent loss (capped at 0), shields via `AddShields` (no Strategy), and the 7th build removes the last unbuilt Wonder. Built Lighthouse/Piraeus are choice units in `Payment.QuoteCost` (no effect on the opponent's prices).
- Play again: `BuildWonder` sets a flag only if the game is not over; `EndTurn` spends it (same player gets the next TakeCard, queued behind flip Reveals, so the Agent sees them); `EndAge` drops it. So `PlayAgain` is only true mid-turn (e.g. while a decision the Wonder caused is queued — relevant for ticket 08's Library/Mausoleum/destroy decisions, which should be pushed before `EndTurn` runs).
- Not done here (ticket 08): Zeus/Circus Maximus destroy, Mausoleum, Great Library. Building them now gives only their VP and shields.
- `WondersBuilt` and the Wonder score category are live (Arena, Builders Guild). `7wd replay` prints each player's Wonders, the draft offer and a pending play-again.
- Tests: 202 Engine (new `WonderTests`: draft order for both first players, bad draft Moves/offers, chance-source offers, building and legality, each of the 10 non-choice-power Wonders' printed effects, Lighthouse/Piraeus production, Architecture/Economy/Theology/Strategy, supremacy before play-again, play-again after Reveals and lost at Age end, the 7-Wonder limit, Builders Guild) and 10 Agents (random games: A-B-B-A order, ≤7 built, 8th removed, play-again turn order). Test helper `Setup.Draft` builds a draft log for any 8 Wonders.
