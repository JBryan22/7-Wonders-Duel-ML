# Promo Wonders in the Engine, training and Encoding

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 15

## Question

The owner sometimes plays with the promo Wonders (Statue of Liberty, The Messe), so the Agent must handle them whenever they are in the Wonder pool. How do they fit in?
- **Engine:** how is the Wonder pool chosen at setup? Is it a game option such as "base 12" or "base 12 + promos"? How do any new Move types or Pending Decisions they introduce get modelled?
- **Training:** should Self-Play include them in some, most or all games, so the network learns them without weakening base-game play?
- **Encoding:** the encoding was decided for 12 Wonders, with a policy indexed by card and Wonder and expansions arriving as new Encoding versions (ADR 0003). Do the promos go into Encoding v1 directly or arrive as v2?
- **Adapters:** how do the Advisor Window picker and the TTS reader (`Statue Of Liberty` and `The Messe` cards are already in the mod) handle them?

## Context

- "Neural network input and output encoding" sized the policy at about 1,140 moves for 12 Wonders.
- "Capture a mid-game TTS save and verify unknowns on Windows": in the mod, both promo Wonders sit set aside at x ≈ −76.7 when they are not used.

## Context from "Promo Wonder rules: Statue of Liberty and The Messe"

- **Rules:** see `docs/research/promo-wonders.md`.
- **Proposed Moves:**
  - `messe <card>` (2 to 6 options);
  - `liberty <keep> <give>` (6 options).
- **Proposed Reveal:** `reveal returned age<N> a b c`, a new chance event.
- **Game State changes:**
  - the player who must decide can differ from the player whose turn it is;
  - Pending Decisions are queued and can belong to either player;
  - the non-active player can win immediately.
- **Ruleset:** one flag per promo is recommended. The Go engine uses one switch for both.
- **TTS:** the mod's Statue button cannot be trusted. The Adapter must see the 3 revealed cards some other way, or have them named in the picker.

## Context from "Tabletop Simulator Adapter architecture"

- The Lua filter reports a card's name only if it is face up or in the discard pile. Once the players flip the Statue of Liberty's 3 removed cards face up, they appear in the Table Snapshot, so the mod's Statue button isn't needed. If nobody flips them, the Advisor Window's picker asks.
- Reconciliation looks for a short chain of legal Moves and Reveals. The `liberty` and `messe` Moves and the `reveal returned` Reveal must therefore be expressible as table changes in the expected-table projection.

## Answer

Resolved by grilling on 2026-10-09. New glossary terms: Ruleset, Wonder pool. Pending Decision now notes that it belongs to a player.

**Engine and Ruleset**
- One switch per promo in the Ruleset, both off by default. Example header: `rules base start-choice=new promo=messe,liberty`. The Engine shuffles whatever Wonder pool the switches produce (12, 13 or 14 Wonders), and the draft is unchanged (8 drafted).
- Stonehenge and Sagrada Familia are display-name aliases for `pyramids` and `great-library`, not switches.
- **Player to decide** is tracked separately from the player whose turn it is. Pending Decisions sit in a queue, and each belongs to one player.
- **The Statue's owner makes every Statue choice.** The order is: build the Statue with an Age card → `reveal returned age<N> a b c` → `liberty-keep <card>` → `liberty-give <card>`, and the remaining card goes to the discard pile. Only then does resolution start: the owner's card fully first (including any token pick or win), then the opponent's card.
- **The one exception (owner, 2026-10-09):** if the given card completes a science pair for the opponent, the **opponent** picks that Progress token. This is the only time a player decides during the other player's turn. The opponent can also win outright from the given card, so the Result logic must not assume the active player wins.
- The Messe is `messe <card>`: any top-row card, even a covered one, with no Reveal. It is skipped if the top row is empty.

**Search and network point of view:** "me" is always the **player to decide**, in the Encoding and in search's value backup.

**Encoding: promos go into v1, not v2** (no network exists yet, so this costs nothing; about 10% more numbers):
- Wonder blocks are sized for 14 Wonders (input Wonder states, `draft <wonder>`, `wonder <card> <wonder>`).
- New 73-way card-indexed policy blocks: `messe <card>`, `liberty-keep <card>`, `liberty-give <card>`.
- A new card-centric input location: "offered by the Statue" (73).
- **There are no Ruleset promo bits in the input.** Instead, an "unseen Wonders" multi-hot (14) shows which Wonders could still appear in the draft. A base game then looks like a promo game where the promos happen to be missing, which avoids inputs the network has never seen (distribution shift). After the draft every Wonder is known, so the two kinds of game look the same.
- The PIMC and MCTS samplers draw only from the Wonders still unseen.

**Training**
- Self-Play always uses both promos: the run config has a promo rate with default **100%**, and it stays configurable for a sibling-run experiment. With 14 Wonders and 8 drafted, about 16% of drafts never offer either promo anyway.
- The headline Elo per Generation stays on **base-only** Matches, so it is comparable across runs and with the C++ cross-check, and it detects any harm to base play. A smaller Match with both promos is reported next to it.
- Every rung of the Agent Ladder supports promos through the Engine's legal Moves. The Heuristic gets a simple value for each: VP plus its best card grab.

**Adapters**
- **Advisor Window:** the new-game screen has a checkbox per promo that remembers the last choice.
- **TTS:** if a promo Wonder appears in a draft offer when its box is not ticked, the Adapter raises a Desync with a one-click "Enable <promo> for this game". That fix rewrites the header and replays the Move Log.
- The Statue's 3 cards are read from the Table Snapshot once they are flipped face up; otherwise the picker asks. The mod's Statue button is never used.
