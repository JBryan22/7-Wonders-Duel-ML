# 7 Wonders Duel AI: Pantheon spec and build plan

Written 2026-10-15. It extends [spec-v1.md](spec-v1.md): everything there still holds unless this doc says otherwise. Rules sources: [expansions.md](research/expansions.md) Part A (the research) and the official Pantheon rulebook (revised, Repos 2016), whose setup diagrams were read for this spec. Tickets: `.scratch/7wd-pantheon-build/issues/`.

# Part A: the spec

## P-A1. What this adds

Pantheon support in the Engine, the Agents, the Encoding and training, the Advisor Window and the TTS Adapter, switched on by the Ruleset (`expansion=pantheon`) and combinable with the promos. Agora stays out of scope (it is still rejected as "not supported").

**The base game must not change.** Every ticket keeps, unchanged:
- the determinism hashes (`engine/tests/Agents.Tests/DeterminismTests.cs`) and the frozen C++ cross-check games (`engine/tests/Engine.Tests/FrozenGames/`);
- Encoding v1, `data/encoding/v1.json`, and every existing v1 network and Run;
- the base 100k soak numbers.

This matters beyond tidiness: the Windows desktop's training loop rebuilds `7wd` at every start, so a base-game change would silently alter the running main Run.

## P-A2. Rules rulings

Where sources disagreed ([expansions.md §12](research/expansions.md)), the Engine follows these rulings:

| # | Point | Ruling | Source |
|---|---|---|---|
| R1 | Token slots | Mythology tokens on **Age I slots 2, 4, 9, 11, 13** (row 1 both ends; row 3 first, middle, last). Offering tokens on **Age II slots 6, 8, 10** (row 1 first, middle, last). | Rulebook diagrams pp. 6–7 (read 2026-10-15); matches BGA |
| R2 | Zeus on a card holding a token | The player who used Zeus **takes** the token. | Revised rulebook; BGA code |
| R3 | Gate and Divine Theater | **Gate:** the deck tops are revealed to **both players**. **Divine Theater:** the deck is revealed to **the activator only** (`reveal P1 theater …` is a private Reveal). The opponent learns only the Divinity that gets activated, which is public as soon as it is activated. The unused cards and the order they are stacked back in stay known to the activator alone, so `theater-order` is a **private Move**. With the Theater, Enki's 2 Progress tokens are drawn only if Enki is the one activated. | Owner rulings 2026-10-15 (rulebook says "Reveal") |
| R4 | Offering tokens | **Public**, on the board and when held. | Rulebook "then reveal the tokens" |
| R5 | Divine Theater cost | **PP G WW** (2 papyrus, 1 glass, 2 wood). | Owner, from the card, 2026-10-15; matches BGA |
| R6 | Grand Temple back | Grand Temples have **their own card back**, distinct from Guilds and Age III cards. | Rulebook p. 7 illustration |
| R7 | Astarte's coins | Spending is **automatic: the treasury first, then Astarte's coins.** Coin losses (military, Poliorcetics) only ever hit the treasury. | BGA; keeps payment automatic |
| R8 | Offering tokens when activating | A real choice, made in a follow-up **`offer` Pending Decision** (spec A10). Any subset of held tokens whose total, with coins, covers the cost; overpaying is allowed and gives no change. | Rulebook; spec A10 |
| R9 | Free Grand Temple (with its Mythology token) | **Not** a chain build: Urbanism does not trigger. The token is kept. | No source; same as the Engineering FAQ |
| R10 | Combining | Pantheon combines with either promo: the Wonder pool is 14 + promos (14–16). With Pantheon, the Progress pool is 13. | Rulebook |

## P-A3. Components and setup in the Engine

**Data** (`data/pantheon/`, loaded only when the switch is on): 15 Divinities in 5 Mythologies (Greek, Phoenician, Mesopotamian, Egyptian, Roman) plus the Gate; 5 Grand Temples (Age III cards with the `temple` back); 10 Mythology tokens (2 per Mythology); 3 Offering tokens (2, 3, 4); 2 Wonders (The Sanctuary, The Divine Theater); 3 Progress tokens (Mysticism, Poliorcetics, Engineering); the Pantheon board's 6 spaces with each player's activation cost (3 to 8, cheapest at the end nearest that player's capital, each space's two costs summing to 11). Costs and effects are in expansions.md §3, with R5. Base data files are not touched.

**Setup, as Reveals** (spec A4: chance is resolved at the Reveal):
1. **Age I:** the base setup (first player, Progress tokens from 13, the draft from 14–16 Wonders), then `reveal myth-tokens` names the Mythology token on each of the 5 R1 slots. The other 5 tokens are out of the game.
2. **Taking a token:** flipping a card that holds a token gives it to the player whose turn caused the flip, before anything else (including play again).
   - **Mythology token** → a **private Reveal** of the top 2 cards of that Mythology's deck (`reveal P1 divinities greek zeus hades`, redacted for the opponent in Advisor mode), then a `divinity <divinity> <space>` decision for the same player: place one face down on an empty Pantheon space; the other goes back on top of its deck, known only to that player. Two tokens from one flip queue two decisions.
   - **Offering token:** no decision; the player holds it.
3. **Age II setup:** the Gate goes face up in the one empty space; `reveal pantheon` names every face-down Divinity (public from here on); `reveal offerings` places the 2/3/4 tokens on the R1 slots. If Enki becomes face up, `reveal enki <token> <token>` draws its 2 Progress tokens from the box.
4. **Age III setup:** the Guilds are not used: 3 random Grand Temples join 17 Age III cards (20, as in the base). The back-pattern Reveal now names `temple` backs instead of `guild` backs.

**Mythology decks** are modelled lazily, like everything else: each deck is an ordered list of *known* top cards (each known to one player or both) above an unknown remainder. Drawing takes the known tops first, then Reveals from the deck's unseen pool. A deck never runs out (3 cards; at most 2 are ever placed from one deck).

## P-A4. Pending Decisions and Moves (additions to spec A4)

| Pending Decision | Move | Options |
|---|---|---|
| Place a drawn Divinity | `divinity <divinity> <space>` | 2 × ≤ 6 |
| Take a card (Age II–III), extended | `activate <divinity\|gate>` | ≤ 6 more |
| Pay with Offering tokens (only if holding any and the cost is above 0) | `offer <tokens>` e.g. `offer 2+4`, `offer none` | ≤ 8 |
| Enki | `enki <token>` | 2 |
| Nisaba | `snake <card>` | opponent's green cards |
| Baal | `steal <card>` | opponent's brown + grey |
| Hades | `hades <card>` | discard pile |
| Zeus | `zeus <row.col>` (**slot-named**: the card may be face down) | ≤ 20 |
| Anubis | `anubis <wonder>` | built Wonders, either player |
| Isis | `isis <card> <wonder>` | discard pile × own unbuilt Wonders |
| Ra | `ra <wonder>` | opponent's unbuilt Wonders |
| Minerva | `minerva <space>` | 19 |
| Neptune | `neptune-discard <token>` then `neptune-apply <token>` | ≤ 4, then ≤ 3 |
| Gate | `gate <divinity>` (after `reveal gate …`) | ≤ 5 |
| Divine Theater | `theater-deck <mythology>`, `theater <divinity>` (after `reveal theater …`), `theater-order <divinity>` | 5, ≤ 3, ≤ 2 |

- **Activation** is a main action from Age II: cost = the player's cost for that space (doubled for the Gate), minus 2 with the Sanctuary, payable from treasury + Astarte's coins (R7) + Offering tokens (R8). It takes no structure card. The turn ends normally unless the effect says otherwise. Activating a Divinity whose effect is impossible is allowed (it just does nothing). Empty spaces stay empty.
- **Effects without a decision:** Ishtar (the Law symbol), Astarte (7 protected coins, 1 VP each at the end), Tanit (12 coins), Aphrodite (9 VP), Mars (2 shields).
- **Zeus:** the slot's card goes to the discard pile; a face-down card is first named by a `reveal discarded <card>` (public). The player takes any token on it (R2). Newly accessible cards are revealed as usual.
- **Anubis:** the Age card under the Wonder goes to the discard pile; the Wonder becomes unbuilt and can be rebuilt; effects already gained stay. The removed 8th Wonder stays removed. A Statue of Liberty (promo) rebuilt in the Age it was first built finds its 3 returned cards already gone, so the rebuild does nothing (ticket 06: there are only 3 removed cards per Age).
- **Ra:** the stolen Wonder joins the thief's Wonders; at most 7 built in total still holds.
- **Minerva:** the pawn sits on a Military space (capitals allowed); if the Conflict Pawn would enter that space, it stops where it is (the rest of its move is lost, nothing is looted there), and Minerva is then discarded. Rulebook p. 13: "If the Conflict pawn would enter the space which contains the Minerva pawn, it instead stops moving" (so Minerva on your capital saves it once).
- **Grand Temples:** built like any Age III card (cost in expansions.md §3), or free while holding that Mythology's token (R9). End score 5 / 12 / 21 VP for 1 / 2 / 3 Temples.
- **New Progress tokens:** Mysticism (2 VP per Mythology and Offering token still held), Poliorcetics (each forward Conflict Pawn space costs the opponent 1 coin), Engineering (any card showing a chain symbol under its cost costs 1 coin, not a chain build).
- The FAQ rulings in expansions.md §3 hold (Nisaba/Ishtar symbols count for pairs and supremacy; an unchosen Enki's tokens go back to the box; Neptune may pick a token on either side).

**Engine structure:** Pantheon is one **expansion module** hooking into named Engine points (setup, legal Moves, applying a Move, end-of-turn checks, scoring), with its state in an optional block that is empty in base games (spec A10). No `if pantheon` checks scattered through the base rules. The base game's code paths, iteration order and RNG draws stay exactly as they are (P-A1).

## P-A5. Hidden information, Observation and Determinizer

Pantheon's private information (expansions.md §4.2, with R3–R4):
- **The opponent's placed Divinity** during Age I: its Mythology and space are public; which of that Mythology's cards it is, is not.
- **The card a player returned to the top of a deck:** known to that player until it leaves the deck (drawn by a later token, revealed by the Gate or Theater, or never).
- **What the Divine Theater showed** (R3): the whole deck is revealed to the activator only. The opponent sees the activated Divinity and nothing else: not the unused cards, not their order. Example: in Age I, with one card of that Mythology already placed face down in the Pantheon, only the activator learns the other remaining card. `theater-order` is the one **private Move**: it has an audience like a Reveal. In Advisor mode the opponent's Theater is logged redacted (`reveal P2 theater greek ? ? ?`, one `?` per card of the deck, and `P2 theater-order ?`), and the owner enters only what they saw on their own Theater. The opponent's Age I draw and placement are logged the same way: `reveal P2 divinities greek ? ?` and `P2 divinity ? 4` (only the space is seen).

`Observe` already hides by Reveal audience (spec A4); it now also covers deck-top knowledge. A hidden face-down card shows only its Mythology; a hidden deck card still shows who knows it; a state's "seen" Divinities are only those it can name, so every hidden place of a Mythology is filled from that Mythology's unseen cards. Replaying a log with the opponent's private lines redacted gives a state whose Observation is the true one's (checked in the Pantheon soak). The **Determinizer** fills these unknowns uniformly from what the observing player hasn't seen, consistent with the public Mythology of each face-down Pantheon card. The **no-peeking test** (two states with the same Observation but different hidden Divinities give the same search result) extends to Pantheon.

Pantheon's private information is short-lived and mostly gone at Age II setup, so the uniform Determinizer and the existing Agent Ladder are expected to be enough (expansions.md §11).

## P-A6. Agents

- **Heuristic:** values for Pantheon actions (Divinity effects, activation cost vs benefit, tokens, Grand Temples). The presets keep their skews.
- **PIMC and chance-node MCTS:** no new search code; they work through the Engine and the Determinizer. A Pantheon chance-node MCTS becomes the **Pantheon Anchor**, the fixed point of a separate **Pantheon Elo scale** (Random = 0 in Pantheon games). Base and Pantheon Elo are never compared.
- `7wd match` and `7wd soak` take `--expansion pantheon`.

## P-A7. Encoding v2 and the Pantheon network

**Encoding v2 = Encoding v1 unchanged, followed by appended Pantheon blocks**, for both the input and the policy. Every v1 index keeps its meaning in v2. New identities (Grand Temples, the 2 Wonders, the 3 Progress tokens) get their own appended blocks rather than growing v1's blocks. In a base game every Pantheon input is 0 and every Pantheon Move is masked.

| New input block | ~Size |
|---|---|
| Token on each structure slot (5 Mythology + 3 Offering kinds) | 160 |
| Divinity-centric location (Pantheon space 1–6, face up/down, mine, opponent's, deck top known to me, known to opponent, unseen) | ~200 |
| Grand Temples (layout one-hot per slot, location, unseen), `temple` back per slot | ~170 |
| New Wonders and Progress tokens (as v1's blocks) | ~25 |
| Tokens held, Astarte coins, Minerva position, Enki tokens, deck sizes, Snake target, Pantheon costs | ~70 |

| New policy block | ~Size |
|---|---|
| `divinity <divinity> <space>` | 15 × 6 = 90 |
| `activate` (15 + Gate), `gate`, `theater`, `theater-order` | 64 |
| `offer <tokens>` | 8 |
| `enki`, `theater-deck` | 13 + 5 |
| `snake`, `steal`, `hades` (by card), `zeus` (by slot) | ~180 |
| `anubis`, `ra`, `isis` (card × Wonder, appended like v1's `wonder`) | ~1,300 |
| `build/discard` of the 5 Temples, `wonder` with the 2 new Wonders and Temples | ~250 |
| `minerva`, `neptune-discard`, `neptune-apply` | ~30 |

So roughly **+600 inputs and +1,900 policy outputs** (exact numbers come from the manifest). The outcome head stays at 7 classes.

- `data/encoding/v2.json` is committed with its own manifest test; `v1.json` and its test stay.
- C# encodes with whichever version the loaded network declares, and refuses a Pantheon game with a v1 network.
- **Network surgery** (`sevenwd-train surgery`): a new v2 network is made from a v1 network by copying the trunk and heads, copying the first layer's v1 columns and the policy head's v1 rows, and starting the new columns and rows at zero. **Validation: in a base game, the operated network gives exactly the same outputs as its parent** (a cheap, exact check that the surgery is right).
- **Separate networks:** the Pantheon network plays Pantheon games; base games keep using the base "best" network. The Advisor picks the network by the game's Ruleset.

## P-A8. Training

- A Pantheon Run is a normal Run (spec A7) whose config sets `expansion: pantheon` and starts from a surgery copy of the base "best" network (a warm start: it already knows the base game). Same network size (Small 256×4), Gumbel Self-Play, the same promo mix as the main Run.
- Matches use the Pantheon Anchor and a Pantheon reference opponent; the stop rules (cap, plateau) are set with the owner from a short calibration.
- **It runs on the Windows desktop only after the base main Run has stopped**: there is one GPU, and the main Run must not be disturbed.
- **Success:** the Pantheon "best" network passes the Pantheon Anchor (lower 95% bound > 50%).

## P-A9. Advisor Window and TTS

- **Advisor Window:** a Pantheon checkbox on the new-game screen; a Pantheon panel (6 spaces with both players' costs, face-down Mythologies, the Gate); tokens drawn on structure slots; each player's Mythology and Offering tokens, Astarte's coins, the Snake and Minerva; Move entry and advice tiles for every new decision; the picker takes private Reveals only for the owner's own draws (the opponent's are logged redacted). Play against the AI works with Pantheon.
- **TTS Adapter:** names and places for every Pantheon piece in the "ULTIMATE ed." mod (Pantheon board spaces, Divinities, Gate, Grand Temples, both token kinds, Snake, Minerva); the snapshot filter keeps the owner's private zone (their drawn Divinities) and drops the opponent's; reconciliation for the new Moves. Facts only the live table can show are checked by the owner at the desktop, like v1's `docs/tts-live-checks.md`.

## P-A10. Proving it works

- **Rule tests:** one per Pantheon rule and per R ruling; every Divinity, the Gate, both Wonders, the 3 Progress tokens, Grand Temples; the token-taking order; Anubis then rebuilding; Ra past 4 Wonders; Minerva stopping the pawn; Zeus on a face-down card holding an Offering token.
- **Scripted games:** hand-traced Pantheon scenarios with scores worked out by hand; later, the owner's Pantheon game in TTS whose score matches the mod's scoring. There is no second Pantheon engine to cross-check against.
- **Random-play invariants** over 100k Pantheon games (with each promo setting): spec A11's list, plus "the Observation never holds the opponent's placed Divinity or private deck tops before they are public".
- **Base unchanged** (P-A1) in every ticket.
- **Determinism:** the same seed gives the same Pantheon game on Linux and Windows (pinned Pantheon hashes in CI).

# Part B: build plan

Tickets are worked top to bottom; each ends with its tests passing, the base-unchanged checks green, a commit and a push.

```
01 Data + module ──┬─ 02 Setup, Age I tokens, Grand Temples ── 03 Activation and payment ── 04 Divinities I ── 05 Divinities II ── 06 Soak, scenarios, determinism
                   │
06 ─┬─ 07 Observation + Determinizer ── 08 Heuristic, MCTS, Pantheon Anchor ─┐
    └──────────────────────────────────── 09 Encoding v2 ── 10 Surgery + Python v2 ─┴─ 11 Pantheon Self-Play + laptop check ── 15 Pantheon Run (desktop)
                                                                                                                   │
08 ── 12 Advisor Window + play vs AI ── 13 TTS names, places, reconciliation ── 14 TTS live checks (owner) ──────── 16 Pantheon acceptance game (owner)
```
