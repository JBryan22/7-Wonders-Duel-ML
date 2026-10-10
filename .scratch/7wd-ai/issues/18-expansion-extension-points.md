# Expansion extension points

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 17

## Question

v1 builds no expansion support, but it must name the places where Pantheon and Agora will plug in, so that adding them later doesn't mean rewriting the Engine. Using the findings of "Pantheon and Agora rules that affect the Engine's design", decide:
- **Ruleset:** do the expansions become Ruleset switches like the promo Wonders?
- **Game State:** what must stay open for extension, e.g. new component locations, new per-player resources, new victory conditions?
- **Moves, Pending Decisions and Reveals:** does the current model (a player-owned Pending Decision queue, card/slug-named Moves, Reveals at the moment of knowledge) cover every new choice and chance event, or do the expansions need something new?
- **Observation and hidden information:** if Agora's Conspiracies are truly private, what changes in the Observation, the search and the Agent Ladder? This has to stay consistent with "search sees only the Observation".
- **Adapters:** does the TTS Table Snapshot need new places, and does the Advisor Window need new board areas?

## Context

- Encoding: expansions arrive as a new Encoding version that adds input and policy blocks, with no reserved slots (ADR 0003, "Neural network input and output encoding").
- "Promo Wonders in the Engine, training and Encoding" introduced Ruleset switches, a "player to decide" separate from the player whose turn it is, and a queue of player-owned Pending Decisions. These are the closest precedent.
- Building Pantheon/Agora is out of scope. This ticket names the seams only.

## Answer

Resolved by grilling on 2026-10-09. New glossary term: Determinizer. Reveal now has an audience, and Ruleset mentions expansions. No ADR: each seam is cheap to change.

**Policy: build the cheap seams, name the rest.** v1 builds a seam only when it costs almost nothing now, would be painful to retrofit, and can be tested with the base game. Everything else is only named in the spec.

| Seam | v1 | Decision |
|---|---|---|
| **Ruleset** | Build: the grammar and the rejection | Expansions are Ruleset switches like the promos, and can be combined: `rules base start-choice=new promo=liberty expansion=pantheon,agora`. v1's Engine rejects a log with an expansion on, with a clear "not supported" error. |
| **Expansion modules** | Name | Each expansion is its own module hooking into named Engine points: setup, legal Moves, applying a Move, end-of-turn checks and scoring. No `if agora` checks scattered through the base rules. Its state lives in an optional block (Pantheon board, Senate, Conspiracy hands). |
| **Private Reveals** | Build | Every Reveal has an audience, both players or one. The Great Library draw uses this from day one. `Observe(state, player)` hides the other player's private Reveals by audience, not by a special case. In Advisor mode the opponent's private draws are logged redacted (`reveal P2 library ? ? ?`) and stay unknown. |
| **Payment choices** | Name | Where payment is a real choice (Pantheon Offering tokens), the Move picks *what*, and a "choose payment" Pending Decision follows for the same player. Payment stays automatic everywhere else. One Move = one decision still holds. |
| **Slot-named Moves** | Build: the grammar and parser | A Move names a card by default, and a slot (`row.col`) only when the card is unknown (Zeus on a face-down card) or not unique (Agora's identical Conspirators). A later Encoding version adds a slot-indexed policy block for them. Per-copy ids (`conspirator#3`) were rejected because identical copies teach the network nothing. |
| **Determinizer** | Build (uniform) | One named component that turns an Observation into one sampled Game State consistent with it. v1's search uses it for face-down cards and the opponent's Library draw. For Agora, the opponent's Conspiracies are more unknowns. A Determinizer that weights guesses by the opponent's play comes only if Matches show the uniform one losing strength. The Agent Ladder is unchanged, and this stays consistent with "search sees only the Observation". |
| **Game State shape** | Build | Pyramid layouts and the set of card-back types (Age, Guild; later Senator, Temple) come from data. Victory types and score categories are growable lists. The number of outcome classes is read from the Encoding manifest (7 now, 9 with Agora). |
| **Game State assumptions** | Name | "4 Wonders per player" and "a built Wonder stays built" stop holding (Ra, Anubis). The non-active player can win (already required by the promo decision). Decisions on the other player's turn reuse the player-owned Pending Decision queue from the promo decision; nothing new is needed. |
| **Table Snapshot** | Name | Places are named in data, so Pantheon spaces, Senate Chambers and Conspiracy hands are new place names. The filter keeps the owner's private zones and drops the opponent's. |
| **Advisor Window** | Build: the pyramid drawn from layout data | 25-slot Agora pyramids then draw without new code. Pantheon and Senate panels come later. The picker lets the owner enter private Reveals only for their own draws. |

The Encoding is unchanged: expansions arrive as a new Encoding version (ADR 0003).
