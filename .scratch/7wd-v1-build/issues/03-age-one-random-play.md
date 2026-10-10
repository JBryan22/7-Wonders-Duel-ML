# 03: First playable slice: Age I with random players

**What to build:** A random game of Age I plays end to end: Game State with cheap copy, the take-card Pending Decision (build / discard), cheapest automatic payment with trading, Reveals for flipped cards from a seeded Chance Source, the Random Agent, Move Log writing and replay, and 7wd replay printing the state.

**Blocked by:** 02

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Random Age I games always end with all 20 cards taken
- [x] Replaying a written Move Log reproduces an identical Game State
- [x] Payment rule tests: trading prices count only the opponent's brown/grey; choice resources go on the most expensive missing resource
- [x] Coins never go negative over 10k random Age I games

## Notes

- Code: `engine/src/Engine/Rules/` (GameState, Moves and log entries, Payment, CardSet, Rng, Ruleset, ChanceSource) and `engine/src/Engine/Log/` (Game = state + log, MoveLog text format, StatePrinter). Agents: `IAgent`, `RandomAgent`, `GameRunner` in `engine/src/Agents/`. CLI: `7wd replay <log>` and `7wd random [--seed N]` (prints a random game's Move Log).
- Flow: `GameState.Step` says what comes next: `Reveal` (ask the Chance Source), `Decision` (front of the Pending Decision queue, `PlayerToDecide`) or `GameOver`. Reveals always come before the next decision, so a play-again turn (ticket 07) will naturally start after the flips.
- Hidden cards: a slot holds a card index, `Empty` (-1) or `Hidden` (-2). A slot needs a Reveal when it is `Hidden` and either face up in the layout or accessible, so the Age's opening Reveal and later flips are the same mechanism. The 3 cards never drawn per Age stay unnamed. Ticket 04 will need a Guild back value (e.g. -3) and a back-aware pool for Age III.
- `GameState.LastAge = 1` ends the game after Age I; ticket 04 raises it and replaces `EndAge`'s placeholder. There is no Result yet.
- Log format: `reveal first P1`, `reveal age1 0.0 card ...` (the opening Reveal of an Age names the Age), `reveal 3.1 card` (flips, slots in the current Age), `P1 build card` / `P1 discard card`. `#` comments and blank lines are ignored. Parsing replays as it goes (slot names need the current Age); errors name the line.
- Payment: printed coins and trading coins are kept apart in `PaymentQuote` (Economy, ticket 06, redirects only trading). Choice production is a list of "units" (resource bitmasks) placed by exhaustive search; Wonder producers (ticket 07) and Architecture/Masonry (ticket 06) should add units there. Production is cached per player in the state and updated in `AddToCity`; a destroy (ticket 08) must also subtract it.
- Randomness is our own xoshiro256** `Rng`, with pinned output values in `RngTests` so CI catches any cross-OS drift. `GameRunner.PlayRandom(seed)` derives the Chance Source and both Agents' seeds from one seed.
- Agents currently read the full `GameState`; ticket 08 switches `IAgent` to the Observation. Random-play invariants use plain seeded loops rather than a property-testing library so far; a failure names its seed.
- Tests: 64 Engine (data, payment, Age I rules, Move Log, Rng) and 7 Agents (random-play invariants: 1k games all 20 taken, 500 replays identical, 10k games coins non-negative, copies independent).
