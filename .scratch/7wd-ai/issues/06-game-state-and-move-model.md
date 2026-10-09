# Game State, Observation and Move model

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 01, 04

## Question

What exactly are the Game State, Observation and Move in the Engine: which fields, what is hidden from whom, how chance (age layout, face-down reveals, progress-token draws, Great Library / Mausoleum choices) is represented, how multi-step decisions (choosing which progress token, which opponent card to destroy, who starts the next age) are modelled as Moves, and what textual notation the Move Log uses so any Adapter can produce and replay it?

## Answer

Resolved by grilling on 2026-10-09. Decision record: [ADR 0002](../../../docs/adr/0002-chance-resolved-at-reveal.md). New glossary terms: Reveal, Chance Source, Pending Decision.

**Chance is resolved lazily (ADR 0002).** A face-down slot stores only its back: "unknown Age N card" or "unknown Guild". Its identity is fixed only when it flips, by a **Reveal** in the Move Log. The Reveal comes from a **Chance Source**: a random draw from the unseen pool in Self-Play and search, or the human or TTS in Advisor mode. Setup tokens, draft offers, the Great Library draw, the Age III Guild back pattern and the first player are Reveals too. A card nobody ever sees never gets an identity.

**Multi-step choices are Pending Decisions.** Each Move resolves exactly one Pending Decision. A "play again" turn is a separate Move made after the Reveals of newly accessible cards, so the Agent decides it knowing those cards. (This was the owner's key point.)

| Pending Decision | Move |
|---|---|
| Draft a Wonder (8× before Age I, picks A-B-B-A per round of 4) | `draft <wonder>` |
| Take a card | `build <card>` / `discard <card>` / `wonder <card> <wonder>` |
| Progress token after a science pair (mandatory if any remain) | `token <token>` |
| Great Library (keep 1 of 3 drawn) | `library <token>` |
| Zeus (brown) / Circus Maximus (grey) | `destroy <card>` |
| Mausoleum (free build from discard pile) | `mausoleum <card>` |
| Who starts the next Age | `start me` / `start opponent` |

- A decision with no options is skipped silently. A decision with exactly one option is still logged as a Move, but Agents don't search it.
- **Payment is never part of a Move.** The Engine always pays the cheapest way, including choice resources and the Architecture/Masonry discounts.
- Automatic effects are Engine consequences, not Moves: military coin loss, the 8th Wonder being removed, immediate military or science victory.
- **Start-player rule:** the newer rule (that player chooses who starts) is the default. The 2015 rule (they start) sits behind a flag. The first player of the game is a random Reveal.

**Observation** = the Game State + the observing player, with the opponent's pending Great Library draw blanked out. It has no Move Log, because the Game State is Markov. Produced by `Observe(state, player)`.

**Game State fields:**
- **Setup:** ruleset flags, first player, draft offers and picks, each player's 4 Wonders (unbuilt / built + the Age card used / removed).
- **Current Age:** Age number, layout slots (empty / face-down with back type / face-up card ID), discard pile, unseen pools per Age and for the Guilds.
- **Per player:** coins, built cards, progress tokens, Great Library draw while pending.
- **Shared:** Conflict Pawn position, military tokens left, progress tokens on the board.
- **Turn:** player to move, Pending Decision, play-again flag, destroy target colour.
- **End:** the result.
- Derived values (production, trading prices, science symbols) are recomputed or cached, never stored as truth.
- **Implementation:** mutable state with a cheap fixed-size copy. `Apply` changes the state in place, and search copies it first.

**Result:** the winner or a draw, the victory type (military / science / civilian), and both players' scores by category. A draw happens only on civilian VP with equal blue VP. Whether the network trains on victory type is left to the Neural network input and output encoding ticket.

**Move Log:** plain text, one entry per line, starting with a version and rules header. IDs are stable kebab-case slugs (small integers internally). Moves name the card, since every base-game card is unique. Reveals name the slot as `row.col`. Undo or mis-entry correction means truncating the log and replaying it. Example:

```
7wd-log v1
rules base start-choice=new
reveal first P1
reveal draft pyramids colossus sphinx appian-way
P1 draft sphinx
reveal age1 0.0 lumber-yard 0.1 stone-pit ...
P1 build lumber-yard
reveal 3.1 baths
P1 wonder baths sphinx
```

The exact grammar gets finalised during the build.
