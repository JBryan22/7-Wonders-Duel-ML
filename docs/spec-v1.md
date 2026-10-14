# 7 Wonders Duel AI: v1 spec and build plan

Assembled 2026-10-09 from the resolved tickets of the `/wayfinder` map (`.scratch/7wd-ai/map.md`). This document is the hand-off: it says **what v1 is** (Part A) and **in what order to build it** (Part B). Every decision here was made in a ticket. The ticket holds the reasoning, and this spec holds the result, citing the ticket by name in *italics* so the reason can be looked up. Vocabulary follows `GLOSSARY.md`. Capitalised terms (Game State, Move, Reveal, Pending Decision, …) carry their glossary meaning.

**Records this spec rests on**
- ADRs: [0001 C# engine, Python training](adr/0001-csharp-engine-python-training.md) · [0002 chance resolved at reveal](adr/0002-chance-resolved-at-reveal.md) · [0003 C# is the sole encoder](adr/0003-csharp-sole-encoder.md) · [0004 TTS snapshot and reconcile](adr/0004-tts-snapshot-and-reconcile.md)
- Research: [rules reference](research/rules-reference.md) · [promo Wonders](research/promo-wonders.md) · [expansions](research/expansions.md) · [algorithms](research/algorithms.md) · [prior art](research/prior-art.md) · [TTS mod and API](research/tts-mod-and-api.md) · [TTS save and probe](research/tts-mod/)
- Prototypes (branches, never merged): `prototype/manual-entry-adapter`, `prototype/advice-presentation`

---

# Part A: the v1 spec

## A1. What v1 is

An AI that learns 7 Wonders Duel by Self-Play and advises the owner while they play in Tabletop Simulator (TTS) or at a physical table.

**In v1**
- The **base game**, under the official current English rules with no house rules, plus the optional promo Wonders **Statue of Liberty** and **The Messe**. When a promo is in the Wonder pool, every Agent treats it as a live option.
- A C# **Engine** that is proven to follow the rules.
- The **Agent Ladder**: Random → Heuristic → PIMC → chance-node MCTS → Gumbel AlphaZero (the neural Agent).
- A **training pipeline** that runs on the Windows desktop's GPU and can be driven from the Linux laptop.
- The **Advisor Window**, one local web page fed by two Adapters: **manual entry** (clicks) and **Tabletop Simulator** (automatic, mod "7 Wonders Duel [ULTIMATE ed.]", Workshop 3368311210).
- Named **extension points** for Pantheon, Agora and an AI-controlled TTS seat.

**Not in v1**
- Building Pantheon or Agora (v1 only reserves the seams; *Expansion extension points*).
- Implementing the AI-controlled seat in TTS (only its seam).
- Adapters for Board Game Arena or the official digital app: they would need screen-reading, and using a bot in live play breaks those services' terms.
- Advice inside TTS itself; joining a game already in progress (*Tabletop Simulator Adapter architecture*).
- A learning track or teaching material (*Learning track format*). The owner gets a walkthrough of the finished project on request.

**Honesty rule.** During real play an Agent decides only from an **Observation**, never from hidden Game State. Advisor use is for practice and friendly games, not online competitive play.

**v1 is done when** (*Training compute budget and v1 stopping rule*):
1. the "best" network **passes the Anchor** (chance-node MCTS at 1,000 simulations): in a 400-game base-only Match, the lower 95% bound of its win rate is above 50%. The aim is ≥ +200 Elo over the Anchor (≈ 76% wins); it is reported against but does not block v1;
2. the Advisor Window gives advice from that network in a real TTS game, fed automatically by the TTS Adapter (Part B, phase 10).

If the training cap is reached without passing the Anchor, v1 does not ship anyway: the shortfall is diagnosed (warm start, network size, simulation count) with the owner.

## A2. Stack, machines and repository

**Stack** ([ADR 0001](adr/0001-csharp-engine-python-training.md)): the Engine, search, Self-Play, Matches, Adapters and the Advisor are **C# on .NET 10**. **Python + PyTorch** only trains the network and exports it to **ONNX**; C# runs ONNX models through ONNX Runtime (`Microsoft.ML.OnnxRuntime.Gpu`, CUDA on Windows, CPU on Linux).

**Machines** (everything must run on both):
- **Linux laptop** (no NVIDIA GPU): development, tests, a "tiny" CPU training config, remote control of the desktop.
- **Windows desktop** (NVIDIA GPU): real training Runs, Tabletop Simulator, the Advisor in real games.
- Code moves through git (GitHub). Run directories, training data and networks stay out of git; networks are copied with `scp` when needed. Remote control is SSH over Tailscale; TensorBoard goes through an SSH tunnel.

**Repository** (*Solution structure and Engine testing strategy*):

| Folder | Holds |
|---|---|
| `engine/` | C# solution `SevenWondersDuel.sln` (.NET 10): all C# projects and tests |
| `training/` | Python package: `train.py`, `loop.py`, `pyproject.toml`, `uv.lock`, pytest tests |
| `data/` | Card JSON (`data/base/`, plus the promo Wonders) and the committed Encoding manifest (`data/encoding/v1.json`) |
| `tts/` | The read-only Lua snapshot script and the TTS-name → slug table |
| `docs/` | This spec, ADRs, research |
| `runs/` | Git-ignored Run directories |

**C# projects**

| Project | Contents | Depends on |
|---|---|---|
| `Engine` | Game State, Moves, Pending Decisions, rules, Move Log, Observation, Determinizer, derived features, and the Encoding (own folder) | nothing outside .NET |
| `Agents` | Random, Heuristic, PIMC, chance-node MCTS, Gumbel/classic AlphaZero search, batched ONNX inference | Engine, ONNX Runtime |
| `Cli` | the `7wd` program: `selfplay`, `match`, `replay`, `bench`, `train`, `manifest`, … | Engine, Agents |
| `Advisor` | the local web server for the Advisor Window (plain HTML/JS modules, no build step, no Node.js); the Adapters plug in here | Engine, Agents |
| `Engine.Tests`, `Agents.Tests` | xUnit, plus a property-testing library for random-play invariants | |

**Python environment:** uv, Python ≥ 3.12, one `pyproject.toml` with a committed lock. Platform markers select CPU PyTorch on Linux and CUDA PyTorch on Windows, so `uv sync` is the only setup step on either machine.

## A3. Rules and card data

- **Rules source:** the official 2015 English rulebook, as transcribed in [rules-reference.md](research/rules-reference.md), including its list of 20 common implementation mistakes. The promos follow [promo-wonders.md](research/promo-wonders.md).
- **Rulings** (owner, 2026-10-09):
  - A Progress token **cannot be declined** after a science pair: taking one is compulsory whenever any remain on the board.
  - **Start of the next Age** when military is tied: the newer rule (the player who took the last card *chooses* who starts) is the default; the 2015 rule (they start) sits behind a Ruleset flag.
  - **Statue of Liberty:** works as the rule sheet says (below). **The Messe:** any card in the top row, *even covered*. The mod's shorter card text is incomplete, not a different rule.
  - The coin bank is unlimited. The game's first player is random.
- **Card data:** one JSON file set in `data/base/`: 73 cards (66 Age cards, 7 Guilds), 12 base Wonders + 2 promo Wonders, 10 Progress tokens. It starts from the cross-checked BGA/7wd-io data and kilianmandon's MIT JSON, with attribution. It is loaded once at startup into flat integer arrays.
- **Identity:** stable kebab-case slugs (small integers internally), never display names. The rulebook and engines disagree on names (Tribunal/Courthouse, Traders/Merchants Guild); Stonehenge and Sagrada Familia are display-name aliases for `pyramids` and `great-library`.

## A4. Engine

### Ruleset
Fixed at setup and recorded in the Move Log header (*Promo Wonders in the Engine, training and Encoding*; *Expansion extension points*):
```
rules base start-choice=new promo=messe,liberty
```
- `start-choice=new|2015`: the start-of-Age rule.
- `promo=`: one switch per promo, both off by default. The Wonder pool is the 12 base Wonders plus the switched-on promos (12, 13 or 14). The draft is unchanged: 8 Wonders drafted in two rounds of 4, picks A-B-B-A.
- `expansion=pantheon,agora`: the grammar parses, and the v1 Engine **rejects** any expansion with a clear "not supported" error.

### Chance: resolved at the Reveal ([ADR 0002](adr/0002-chance-resolved-at-reveal.md))
The Game State never knows a hidden identity. A face-down slot stores only its **back**: "unknown Age N card" or "unknown Guild" (Guild backs are visibly purple, so the Observation shows which face-down cards are Guilds). An identity is fixed only when it becomes known, by a **Reveal** in the Move Log supplied by a **Chance Source**: a seeded random draw from the right unseen pool in Self-Play and search, or the human / TTS in Advisor mode. A card nobody ever sees (removed at setup, an unused Guild) never gets an identity.

Reveals: the first player; the Progress tokens set out; each draft offer; the Age III Guild back pattern; each card flipped face up; the Great Library draw; the Statue of Liberty's returned cards (`reveal returned age<N> a b c`).

**Every Reveal has an audience**, both players or one. The Great Library draw is private to its drawer. In Advisor mode the opponent's private draws are logged redacted (`reveal P2 library ? ? ?`) and stay unknown forever.

### Game State
Mutable, with a cheap fixed-size copy: `Apply` changes the state in place, and search copies first. Derived values (production, trading prices, science symbols, VP) are recomputed or cached, never stored as truth (*Game State, Observation and Move model*).

- **Setup:** Ruleset, first player, draft offers and picks, each player's Wonders (unbuilt / built with the Age card used / removed).
- **Current Age:** Age number, layout slots (empty / face-down with back type / face-up card), discard pile, unseen pools per Age and for the Guilds.
- **Per player:** coins, built cards, Progress tokens, private Great Library draw while pending.
- **Shared:** Conflict Pawn position, military tokens left, Progress tokens on the board.
- **Turn:** player whose turn it is; a **queue of Pending Decisions, each owned by a player** (the *player to decide* can differ from the player whose turn it is); play-again flag; destroy target colour; cards offered by the Statue.
- **End:** the Result.
- **Data-driven shape** (*Expansion extension points*): pyramid layouts and the set of card-back types come from data; victory types and score categories are growable lists.

### Pending Decisions and Moves
Every Move resolves exactly one Pending Decision. A decision with no options is skipped silently; a decision with exactly one option is still logged as a Move but Agents don't search it. **Payment is never part of a Move**: the Engine always pays the cheapest way, including choice resources and the Architecture/Masonry discounts placed on the most expensive missing resources.

| Pending Decision | Move | Options |
|---|---|---|
| Draft a Wonder | `draft <wonder>` | 1–4 |
| Take a card | `build <card>` / `discard <card>` / `wonder <card> <wonder>` | per accessible card |
| Progress token after a science pair (mandatory if any remain) | `token <token>` | ≤ 5 |
| Great Library: keep 1 of 3 drawn | `library <token>` | 3 |
| Statue of Zeus (brown) / Circus Maximus (grey) | `destroy <card>` | 0–n |
| Mausoleum: build free from the discard pile | `mausoleum <card>` | 0–n |
| The Messe: build free from the top row, even covered | `messe <card>` | 0–6 |
| Statue of Liberty: build one of the 3 returned cards | `liberty-keep <card>` | 3 |
| Statue of Liberty: give one of the remaining 2 to the opponent | `liberty-give <card>` | 2 |
| Who starts the next Age | `start me` / `start opponent` | 2 |

- **Play again** is a separate turn that begins *after* the Reveals of newly accessible cards, so the Agent chooses knowing them. A play-again is lost at the end of an Age.
- **Automatic consequences** are not Moves: military coin loss, removal of the 8th Wonder once 7 are built, immediate military or science victory.
- **Statue of Liberty order:** build the Statue with an Age card → `reveal returned age<N> a b c` → `liberty-keep` → `liberty-give` (the third card goes to the discard pile). Only then does resolution start: the owner's card fully first (including any token pick or win), then the opponent's card. All Statue choices belong to the Statue's owner, **except** a Progress-token pick earned by the opponent's given card, which the opponent makes: the one time a player decides during the other player's turn. In Age III the returned cards are the removed Age III cards, not the unused Guilds.
- **The non-active player can win** (the Statue's given card), so Result logic never assumes the active player won.

### Result
Winner or draw; victory type (military / science / civilian); both players' scores by category. A draw happens only on equal civilian VP *and* equal blue VP.

### Observation
`Observe(state, player)` = the Game State plus the observing player, with every Reveal whose audience excludes that player hidden (in the base game, only the opponent's pending Great Library draw). The Observation has no Move Log: the Game State is Markov.

### Move Log
Plain text, one entry per line, a version line and a rules header first. Moves name the card (every base card is unique); Reveals of layout cards name the slot as `row.col`. Undo and correction mean truncating the log and replaying it. Indicative example (the exact grammar is finalised in phase 1):
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
The grammar also accepts a **slot-named Move** (`row.col` instead of a card) for when the card is unknown or not unique. v1 parses it and the base game never produces it (*Expansion extension points*).

### Determinizer
One named component that turns an Observation into one sampled Game State consistent with it, filling every unknown uniformly from what that player hasn't seen (face-down cards, removed cards, unused tokens, the opponent's Great Library draw, unseen Wonders during the draft). All search uses it. A weighted Determinizer comes only if Matches show the uniform one losing strength.

### Derived features
One module computes production and trading prices per resource, science symbols held, VP so far, distance to each capital, and similar board signals. It is shared by the Heuristic, the Encoding's derived block and the Advisor's reason tags, so the computation exists once.

## A5. Agents and measuring strength

### Agent Ladder (*Agent Ladder and target algorithm*)
Built in this order; each rung must pass the one before it.

| Rung | What it is | Default budget |
|---|---|---|
| Random | any legal Move | |
| Heuristic | hand-written scoring with configurable weights; presets **science**, **military**, **civilian** (fixed weights) and **adaptive balanced** (reweights each turn from opponent science symbols, the Conflict Pawn's distance to a capital, and the point lead). Promo Wonders are valued as VP plus their best card grab | |
| PIMC | per Move: sample hidden cards with the Determinizer, 1 random playout each, pick the Move that wins most | 50 × 1 |
| Chance-node MCTS (**the Anchor**) | tree search that samples each Reveal at the moment it happens | 1,000 simulations |
| Neural (Gumbel AlphaZero) | MCTS guided by the policy/value network | 64 sims in training; 800 in Matches and the Advisor |

- **Search sees only the Observation, everywhere**, even in Self-Play where the true state is available. Every simulated Reveal is drawn by the Determinizer from what the deciding player hasn't seen.
- **"Me" is the player to decide**, in search's value backup and in the Encoding.
- **One neural search, two root modes:** **Gumbel** (all real training) and **classic AlphaZero** (PUCT with Dirichlet noise and temperature), kept only as debug scaffolding to tell a search bug apart from "the network isn't learning". Classic is not a rung. Gumbel's own parameters (number of sampled actions, `c_visit`, `c_scale`) start from the published defaults.
- The Agent interface returns, besides the Move, what the Advisor shows: per-Move visit shares, per-Move value with its standard error, the outcome-class distribution, and the principal line.

### Matches and Elo
- A **Match** is 400 games in 200 seed pairs, seats swapped inside each pair so both sides get the same luck. No SPRT in v1.
- Results: win rate ± 95% CI, and **Elo on one shared scale with Random = 0** (200 points ≈ 76% expected wins). Every Match records the victory-type breakdown.
- **Pass rule:** a rung passes when the lower 95% bound of its win rate against the previous rung is above 50%.
- Search Agents are compared at a fixed simulation count, never wall-clock; wall-clock is logged separately.
- **Headline Elo is measured on base-only Matches** (comparable across Runs and with the C++ cross-check). A smaller Match with both promos is reported next to it.

## A6. Encoding and network

### Encoding v1 ([ADR 0003](adr/0003-csharp-sole-encoder.md); *Neural network input and output encoding*; *Promo Wonders in the Engine, training and Encoding*)
- **C# is the only encoder.** It generates a versioned **manifest** (`data/encoding/v1.json`: version, sizes, a name for every index, e.g. `policy[147] = wonder baths sphinx`). The manifest is committed, and a test fails if the generated one differs, so any Encoding change is a visible diff that forces a revert or a version bump. Python reads the manifest and never encodes. Each ONNX model carries its Encoding version, and C# refuses a mismatch.
- **Point of view:** relative to the player to decide ("me / opponent"); the value always means "my chance to win".
- **Unknowns are zeros, never guesses.**
- The promos are in v1 from the start: Wonder blocks are sized for 14. There are **no Ruleset bits** in the input; an "unseen Wonders" multi-hot shows which Wonders could still appear, so a base game looks like a promo game whose promos happen to be missing.

**Input: a flat vector of about 2,300 numbers**, scaled to roughly 0–1.

| Block | Contents | ~Size |
|---|---|---|
| Layout (slot-centric) | 20 slots × {empty, face-down Age back, face-down Guild back, face-up, accessible} + 73-way card one-hot; current Age one-hot | 1,563 |
| Cards elsewhere (card-centric) | 73 cards × {my city, opp city, discard, under my Wonder, under opp Wonder, offered by the Statue} | 438 |
| Unseen cards | 73 multi-hot: cards that could still be revealed | 73 |
| Wonders | 14 × {in draft offer, mine unbuilt, mine built, opp unbuilt, opp built, removed} | 84 |
| Unseen Wonders | 14 multi-hot | 14 |
| Progress tokens | 10 × {on board, mine, opp, in my Great Library draw} | 40 |
| Scalars | coins (me, opp), Conflict Pawn position, military tokens left (4), I started the Age, start-rule bit | ~11 |
| Turn | Pending Decision kind (one-hot), play-again flag, destroy target colour | ~14 |
| Derived | production and trading price per resource (me, opp), science symbols held, VP so far, distance to each capital | ~40 |

The layout is slot-centric because the network needs the geometry (which card a take uncovers); cards outside the layout have no geometry, so they are card-centric. The exact field order and scaling are fixed in code and recorded in the manifest.

**Policy: about 1,510 logits**, one block per Move kind, indexed by card or Wonder, **never by slot**, so an index always means the same Move. Illegal Moves are masked to −∞ before the softmax.

| Block | Size |
|---|---|
| `build <card>`, `discard <card>` | 73 + 73 |
| `wonder <card> <wonder>` | 73 × 14 = 1,022 |
| `draft <wonder>` | 14 |
| `token <token>`, `library <token>` | 10 + 10 |
| `destroy <card>` (brown/grey only) | 13 |
| `mausoleum <card>`, `messe <card>`, `liberty-keep <card>`, `liberty-give <card>` | 4 × 73 |
| `start me` / `start opponent` | 2 |

**Heads and targets**
- **Value:** tanh in [−1, +1]; target is the final result from the mover's view (+1 / 0 / −1).
- **Outcome class (auxiliary):** 7 classes (I win by military / science / civilian; draw; opponent wins by military / science / civilian). Target: the actual result. Feeds Advisor explanations and victory-type reporting. The class count is read from the manifest (9 with Agora).
- **Policy target:** Gumbel's improved policy (prior plus completed Q-values); root visit counts in classic mode.
- No score-margin head (military and science wins have no margin).

**Network:** a residual MLP (blocks of two fully connected layers plus a skip connection), a shared trunk, three heads. Starting size **256 wide × 4 blocks** (~1.4M parameters). The size is training config, not part of the Encoding. A set/Transformer encoding is a possible later experiment if the MLP plateaus.

## A7. Training pipeline

### The loop (*Training pipeline and two-machine workflow*)
Synchronous **Generations**, orchestrated by Python `loop.py`, which calls the C# CLI as a subprocess. Each Generation:
1. **Self-Play (C#):** `7wd selfplay` plays the Generation's games with the newest network (many games in parallel, one batching inference thread) and writes Game Records and encoded shards.
2. **Training (Python):** `train.py` trains on a sliding window of recent positions and exports ONNX.
3. **Measuring (C#):** `7wd match` plays 400 games against the previous Generation every Generation, and against the Anchor every 5 Generations.

- **No gating:** Self-Play always uses the newest network. A separate **"best" pointer** (the network the Advisor uses) moves only when a Generation beats the current best under the pass rule.
- **Self-Play details:** the played Move is the one Gumbel search selects; its Gumbel noise supplies exploration (no temperature or Dirichlet noise outside classic mode). Only searched decisions become training examples. Generation 0 is a randomly initialised network. Self-Play always includes both promos (`promo-rate`, default 100%, configurable).
- **Loss:** policy cross-entropy (weight 1) against the improved policy; value MSE (weight 1) against the pure result *z*; outcome-class cross-entropy (weight 0.25); AdamW weight decay 1e-4. No search-value mixing in v1.

### Storage
- **Game Record** (kept forever, a few KB per game): the Move Log; for each searched decision, the sparse policy target over legal Moves; the result; the seeds for the Chance Source and the Gumbel noise, so any game replays exactly. Policy targets are stored because they can't be recomputed without rerunning the search.
- **Encoded shards** (a disposable cache): per-Generation NumPy `.npy` files that C# writes directly and Python memory-maps: float16 inputs, sparse policies, value and outcome class. Deleted once their Generation leaves the training window; re-encodable from Game Records under a new Encoding version.

### Runs
- A **Run** is a directory `runs/<name>/` started from a frozen `config.yaml` (network size, simulations, loop numbers, Encoding version, seeds). At start it records the git commit, the Encoding manifest and the machine.
- Changing a Run's config is refused. Experiments are sibling Runs, optionally initialised from another Run's network, so every Elo curve traces to exactly one setup.
- **Watching:** TensorBoard (losses, Elo per Generation, victory-type mix, policy entropy) and a plain `generations.csv` with one row per Generation.

### Starting defaults
Revised once by the calibration run (A7, "Tuning procedure"), then frozen into the main Run.

| Setting | Default |
|---|---|
| Games per Generation | 2,000 (~150k positions), then sized to ~1 desktop-hour |
| Training window | newest ~1M positions (~7 Generations) |
| Training steps | each new position seen ~2× on average; batch 1,024 |
| Optimiser | AdamW, learning rate 1e-3, constant |
| Simulations | 64 Gumbel simulations per searched decision |
| Matches | vs previous Generation every Generation; vs the Anchor every 5 |
| Tiny config (laptop, CPU, smoke tests) | e.g. 10-game Generations, 8 simulations |

### Control: always manual (*Training compute budget and v1 stopping rule*)
- `7wd train start | pause | status`, usable locally or over SSH, plus two Windows desktop shortcuts, **Start training** and **Pause training**.
- **Start** always continues the current Run where it left off. **Status** is one line: what it is doing, how far it has got, the latest Elo, and why it stopped if it did.
- **Pause** frees the machine within about a minute and loses at most a minute of work: Self-Play saves each finished game, training checkpoints every few minutes, and a Match resumes from its last finished seed pair.
- Nothing starts or pauses training automatically. The only exception: the Run **stops itself** on a plateau, the cap, a warm-start trigger, or an unrecoverable fault. It saves everything, records the reason in `status`, `generations.csv` and TensorBoard, and shows a Windows notification.

### Budget and stopping
Budget is counted in **desktop-hours** (hours with the whole Windows desktop training), because CPU-side Self-Play, not the GPU, is the likely bottleneck.
- **Experiments: 150 desktop-hours**, run first as pilots (~20 desktop-hours each, compared on Anchor Elo at equal desktop-hours):
  1. Small (256×4) vs Medium (512×6) network;
  2. with vs without derived features;
  3. classic vs Gumbel at an equal simulation count (one-off sanity check).

  The winners of 1 and 2 shape the main Run. Value-target mixing and playout cap randomisation run only if budget is left; otherwise they move past v1.
- **Main Run: cap of 300 desktop-hours** (≈ 3–4 weeks of nights and weekends). Sanity check after `7wd bench`: 300 hours should buy ≈ 500k+ Self-Play games; if far fewer, raise it with the owner rather than quietly cutting quality.
- **Stop** at whichever comes first: **plateau** (Anchor Elo hasn't risen by more than its error bar over the last 20 Generations, i.e. 4 Anchor Matches) or **cap**. Once networks outgrow the Anchor, a stronger **Reference** (MCTS at 20,000 simulations, on the Ladder) is matched alongside it, and the plateau test uses whichever of the two still has headroom (ticket 28). The main Run's cap is 150 desktop-hours (owner, 2026-10-14).
- **Warm-start triggers** (each stops the Run and flags why; the owner decides whether to start the warm-start sibling Run): science wins under 1% of Self-Play games over Generations 6–10, or no Anchor Elo progress by Generation 20. The warm start trains the network to imitate games between the Heuristic presets, then continues with Self-Play (alternative: mix in 10–20% of games against the presets, fading to 0).

### Tuning procedure
One short **calibration run** measures Self-Play positions/s and training steps/s. Then: size each Generation to ≈ 1 desktop-hour, keep the window at ≈ 7 Generations with each position seen ≈ 2×, keep 64 training simulations unless a 32 / 64 / 128 pilot at equal desktop-hours shows a clear winner, and freeze the result into the main Run's config.

## A8. Advisor Window

One local web page served by the `Advisor` project, shared by every Adapter (*Manual-entry Adapter: how it looks and behaves*; *Advice presentation: reasons and the principal line*).

**Board and entry**
- The current Age's layout as a clickable pyramid **drawn from layout data**, accessible cards outlined; each city and its Wonders below, with coins.
- Enter a Move: click an accessible card, then Build / Discard / Wonder X (only that player's unbuilt Wonders are offered).
- Reveals: a card that becomes accessible face down pulses; nothing else can be entered until it is named from a filterable picker of the unseen pool. Revealing a card already on the table is rejected. The picker accepts private Reveals only for the owner's own draws.
- The Move Log is visible and is the source of truth (state = replay of the log). **Undo** removes the last Move and any Reveals after it; clicking a log line rewinds to it. Both truncate and replay.
- The Move Log is saved to disk after every entry, so a restart resumes.
- **New game screen:** one checkbox per promo (remembers the last choice) and the start-player rule; the owner's seat.

**Advice** (on request; 800 simulations, a **Think longer** button runs 3,200; wall-clock logged; if the laptop needs more than ~3 s at 800 its local default drops to 400)
- **% badges** on the board's cards (share of the search), top card in gold.
- **Compare table** (side panel ≈ 560 px), up to 5 Moves, best first. Each row: the Move and its search share; its win % as a bar with ±2-standard-error whiskers and a 50% guide; the gap to the best, shown as "≈ same" when inside the combined noise 2·√(se₁² + se₂²), otherwise "−x.x%"; a stacked bar of the 7-class outcome head with a legend; every reason tag.
- **Reason tags** come from derived-feature changes, computed by the shared derived-features module (e.g. "completes your ✒ pair: you pick a Progress token", "+2 shields: pawn moves to 3", "uncovers School for them").
- **Expected line** for the hovered or clicked row (the best by default): a list under the table and numbered markers on the board (filled for you, outlined for them), continuing through chance steps shown as "face-down card revealed". Too little search depth shows "no line" rather than a guess.
- **Other Pending Decisions** (token, draft, destroy, Statue keep/give): options appear as tiles above the dimmed board with % badges and are ranked in the same table. The Statue is shown as 6 keep/give pairs, each pair's share being the product of its two steps.
- Agents without an outcome head (MCTS) leave the outcome bar out; everything else works the same.

## A9. Adapters

Every Adapter connects a place where the game is played to the canonical Game State and Move Log. The contract has two directions: **observe** (table → Move Log, built in v1) and **act** (execute a Move at the table, declared but not implemented).

### Manual entry
The Advisor Window's own clicks (A8). It is also the fallback and the corrector for the TTS Adapter.

### Tabletop Simulator ([ADR 0004](adr/0004-tts-snapshot-and-reconcile.md); *Tabletop Simulator Adapter architecture*)
- **Transport: pull, zero changes to the mod.** About once a second the Advisor sends a read-only Lua script through the External Editor API (`localhost:39999`). The script returns the **Table Snapshot** as a JSON string via `sendExternalMessage` (received on `:39998`). Nested tables are dropped by TTS, so the payload is always a JSON string. No object is added to the table and the save is untouched. Side effect: TTS code-editor plugins can't run while the Advisor is connected.
- **Hidden information is filtered inside TTS:** the Lua names a card only if it is face up or in the discard pile (public by the rules, though the mod stacks it face down). Face-down pyramid cards, removed cards, unused Wonders and undealt decks are reported as nameless pieces.
- **Places, not coordinates:** the snapshot reports each piece by place (pyramid slot, city, under a Wonder, discard pile, Wonder area, Progress-token area, …). Places and the TTS-name → slug mapping live in data. Placement facts from the probe save (`docs/research/tts-mod/`): pyramid grid with x step 2 and rows 2.5 apart in z, covering visible as height; city columns by colour (Red left at x −17…−5, White right at x 4.7…17.2); Wonders in columns at x ≈ ±40 with the tucked Age card ≈ 3 units inwards; the discard pile is one face-down deck on a locked tile at ≈ (−50.6, 2.3); removed cards and unused Wonders sit in face-down decks that must be filtered; promo Wonders sit at x ≈ −76.7 when unused.
- **Reconciliation:** the Adapter projects the **expected table** from the Game State and compares it with the snapshot by place.
  - No difference: nothing happens.
  - Explained by exactly one chain of up to about 4 legal Moves and Reveals: the chain is appended.
  - Explained by several chains: the Advisor Window offers them as one-click choices.
  - Otherwise a **Desync**: flagged in the Advisor Window with a list of mismatches; nothing is appended. It clears itself once the table is tidied or the Move Log is corrected.
- **Reconcile only when the table is settled:** nobody holding an object, nothing moving, two identical snapshots in a row.
- **Inputs vs soft checks:** cards, Wonders and Progress tokens are inputs. Coins, the Conflict Pawn and looting tokens are soft checks only ("TTS shows 7 coins, expected 9"), never a Desync.
- Reveals the table can't show (a card nobody flipped, the owner's Great Library draw kept face down, the Statue's 3 cards if not flipped up) are asked for in the picker. The mod's Statue of Liberty button is never used.
- **Promos:** a promo Wonder appearing in a draft offer when its box is unticked raises a Desync with a one-click "Enable <promo> for this game", which rewrites the header and replays the log.
- **Seat:** auto-detected from the host's TTS player colour (the owner usually sits White, on the right); asked if the host isn't seated; always overridable. Both players' Moves are tracked; advice is for the owner's seat.
- **New game:** a fresh setup on the table is offered as "new game detected, start?", not started automatically. No cold mid-game join.
- **AI-seat seam:** the expected-table projection is a named, tested component. A later AI seat would move pieces to match it through the same channel and let reconciliation confirm the result.

## A10. Extension points (*Expansion extension points*)

**Built in v1** (cheap now, painful to retrofit, testable with the base game):
- Ruleset grammar with `expansion=`, rejected by the v1 Engine.
- Reveal audiences and audience-based `Observe`.
- Slot-named Moves in the grammar and parser.
- The uniform Determinizer as one named component.
- Data-driven layouts and card-back types; growable victory types and score categories; outcome-class count read from the manifest.
- Player-owned Pending Decision queue; the non-active player can win.
- Pyramid drawn from layout data in the Advisor Window.
- The TTS expected-table projection and the declared `act` direction.

**Named only:**
- Each expansion is its own module hooking into named Engine points (setup, legal Moves, applying a Move, end-of-turn checks, scoring), with its state in an optional block. No scattered `if agora` checks.
- Payment choices (Pantheon Offering tokens) become a follow-up "choose payment" Pending Decision for the same player; payment stays automatic everywhere else.
- "4 Wonders per player" and "a built Wonder stays built" stop holding (Ra, Anubis).
- Table Snapshot places for Pantheon spaces, Senate Chambers and Conspiracy hands; the filter keeps the owner's private zones and drops the opponent's.
- Expansions arrive as a **new Encoding version** that adds blocks (plus a slot-indexed policy block), with no reserved slots now; a new network may be warm-started by copying the weights that still line up. Rough growth: Pantheon +450 inputs / +250 policy; Agora +1,200 / +800 ([expansions.md](research/expansions.md)).
- Agora breaks "perfect information with chance" (long-lived private Conspiracies): the opponent's Conspiracies become more unknowns for the Determinizer; a weighted Determinizer comes only if needed.

## A11. Proving it works

**Engine correctness** (*Solution structure and Engine testing strategy*):
1. **Rule tests:** one small test per rule, from the rulebook's examples and the 20 common mistakes; the same for each promo rule.
2. **Scripted games:** Move Logs whose scores and winner were worked out independently of our Engine: hand-traced short scenarios; ≈ 10 full games frozen after the cross-check agrees, covering military, science and civilian wins and a draw; later, real table games whose scores match the score sheet.
3. **Random-play invariants** over thousands of games (with and without promos): coins never negative; every game ends; a legal Move exists unless the game is over; replaying the Move Log gives an identical Game State; a copied state never affects the original; the Observation never holds the opponent's private Reveals.
4. **Data tests:** card counts, chain links resolve, science-symbol distribution, Age layout shapes.
5. **Cross-check against indjev99's C++ engine (MIT):** the same Moves and Reveals through both engines; flag any disagreement on legal Moves or final scores. Linux only, opt-in, not in CI; run before training first trusts the Engine and after any rules change. Its start-player rule is matched via our flag. Base game only (it has no promos).

**Other guards**
- Committed Encoding manifest test (A6).
- **Determinism:** our own seeded RNG, so the same seed gives the same Reveals and Game State on Linux and Windows (tested in CI). Full search with a network is reproducible only on the same machine and runtime.
- **Training-loop smoke test:** `loop.py` runs 2 Generations in the tiny config (Self-Play → training → ONNX export → Match) and checks the files and the Encoding-version handshake. About a minute on CPU; in CI on Linux and before any real Run on Windows.
- **CI:** GitHub Actions runs the fast C# and Python tests on Linux and Windows on every push. The cross-check and long soaks stay out.
- **Speed:** `7wd bench` reports random games/s and Self-Play positions/s, run by hand, results written down. Target: **≥ 10k random games/s on one laptop core** (≈ 1/6 of the C++ engine). Missing it triggers an optimisation pass before training.

## A12. Left to the build (implementation checks, not open decisions)

These were deliberately deferred; each is settled while building the phase named, without returning to the owner unless it changes a decision above.
- The exact Move Log grammar (phase 1).
- The exact Encoding field order and scaling (phase 6), recorded in the manifest.
- Gumbel parameters beyond the published defaults (phase 6).
- Every number marked "default" in A7, set by the calibration run (phase 8).
- TTS: Execute Lua with `returnID` (untested); held-object detection; the cost of 1 Hz polling; the Age I and Age III pyramid grids (only Age II was observed); mapping Conflict Pawn x positions to track spaces (a soft check only) (phase 10).

---

# Part B: phased build plan

Each phase ends in **something runnable** and a **validation experiment** that proves it works (*Learning track format*): these are the acceptance criteria, not learning milestones. A phase starts only when the phases it depends on have passed their validation. Phases on different branches of the graph can run in parallel; the training Run (phase 8) runs on the desktop for weeks while the Advisor phases are built.

```
P0 Foundations
 └─ P1 Base Engine
     ├─ P2 Engine cross-check ──┐
     └─ P3 Promo Wonders ───────┤
                                └─ P4 Match harness + Heuristic
                                    └─ P5 PIMC + chance-node MCTS (the Anchor)
                                        ├─ P6 Encoding, network, neural search
                                        │   └─ P7 Training loop and Run control
                                        │       └─ P8 Calibration, pilots, main Run ─┐
                                        └─ P9 Advisor Window + manual entry          │
                                            └─ P10 TTS Adapter ──────────────────────┴─ v1 done
```

### P0. Foundations
**Build:** the repo layout (A2): `engine/` solution with empty projects and test projects, `training/` uv package, `data/`, `tts/`, `runs/` git-ignored. Card data JSON in `data/base/` (73 cards, 12 + 2 Wonders, 10 tokens, the three Age layouts) with attribution. Data loader into flat arrays. GitHub Actions on Linux and Windows.
**Runnable:** `dotnet test` and `uv run pytest` on both machines; `uv sync` selects CPU or CUDA PyTorch by platform.
**Validation:** CI green on Linux and Windows. Data tests pass: card counts per Age, every chain link resolves, the science-symbol distribution matches the rules reference, layout shapes are 2-3-4-5-6 / 6-5-4-3-2 / 2-3-4-2-4-3-2.

### P1. Base Engine
**Build:** Ruleset parsing (with the expansion rejection and the `start-choice` flag), Game State with cheap copy, the Pending Decision queue, all base Moves and their legality, automatic payment, Reveals with audiences, seeded Chance Source, Result and scoring, `Observe`, the Move Log writer/parser/replayer (including slot-named Moves), the derived-features module, the Random Agent. CLI: `7wd replay <log>`, `7wd bench`.
**Runnable:** play random games end to end; replay any Move Log and print the final state and scores.
**Validation:**
- Rule tests for every rule in the rules reference and each of the 20 common mistakes.
- Hand-traced scripted scenarios give the expected scores and winners.
- Random-play invariants hold over 100k games.
- Same seed → byte-identical Move Log and Game State on Linux and Windows (in CI).
- `7wd bench` ≥ 10k random games/s on one laptop core (else optimise before moving on).

### P2. Engine cross-check
**Build:** an opt-in Linux harness that drives indjev99's C++ engine with our Moves and Reveals and compares legal-Move sets and final scores; about 10 full games frozen as scripted tests after it agrees (military, science and civilian wins and a draw).
**Runnable:** `7wd crosscheck --games N` (Linux).
**Validation:** zero disagreements over at least 10,000 random games, with our start-player flag set to match the C++ engine. Any disagreement is resolved against the rulebook, and the fix gets a rule test.

### P3. Promo Wonders
**Build:** the two `promo=` switches and the 12/13/14 Wonder pool; The Messe (`messe`); the Statue of Liberty (`reveal returned`, `liberty-keep`, `liberty-give`, the resolution order and the opponent's token-pick exception); the non-active player winning.
**Runnable:** random games and `7wd replay` with either or both promos.
**Validation:**
- Rule tests from [promo-wonders.md](research/promo-wonders.md): Messe with covered top-row cards and an empty top row; Statue in each Age (Age III uses removed Age III cards, not Guilds); the given card completing the opponent's science pair (opponent picks the token) and winning outright for the opponent.
- Random-play invariants hold over 100k games with each promo setting.
- Base-only games are unchanged: the frozen P2 games still pass.

### P4. Match harness and Heuristic
**Build:** `7wd match` (seed pairs, seat swap, win rate ± 95% CI, Elo with Random = 0, victory-type breakdown, base-only vs promo Matches, wall-clock logging, resume from the last finished seed pair). The Heuristic with its four presets, using the derived-features module, with promo values.
**Runnable:** `7wd match heuristic:balanced random`.
**Validation:**
- Adaptive balanced Heuristic **passes** Random under the pass rule, by a clear Elo margin.
- Each style preset shows its intended skew in the victory-type breakdown (the science preset wins by science noticeably more often than the military preset, and vice versa).
- A Match of an Agent against itself lands within its CI of 50% (checks the seat swap and the statistics).

### P5. PIMC and chance-node MCTS
**Build:** the Determinizer (uniform), PIMC (50 × 1), chance-node MCTS (1,000 simulations), all searching from the Observation only. The Agent output (visit shares, per-Move values with standard errors, principal line).
**Runnable:** `7wd match mcts pimc`, and either as an Agent anywhere an Agent is accepted.
**Validation:**
- PIMC passes the adaptive balanced Heuristic; chance-node MCTS passes PIMC. Both are recorded as Ladder Elo with the victory-type mix.
- **No-peeking test:** two Game States with the same Observation but different hidden cards give the same search result under the same seed.
- MCTS's chosen Moves and statistics are deterministic for a fixed seed.

The MCTS Agent becomes **the Anchor**. Its Elo is fixed from here on.

### P6. Encoding, network and neural search
**Build:** Encoding v1 in C# (A6) and the manifest generator (`7wd manifest`), with `data/encoding/v1.json` committed and the manifest test. ONNX inference with a batching thread (CPU and CUDA). The neural search with both root modes (classic and Gumbel). In Python: the residual-MLP network read from the manifest, the losses, `train.py`, ONNX export stamped with the Encoding version.
**Runnable:** a randomly initialised network exported to ONNX and playing through `7wd match neural:<onnx> random`.
**Validation:**
- Encoding tests: every legal Move maps to a unique policy index and back; illegal Moves are masked; the input never changes when only hidden cards change (no peeking through the Encoding).
- The manifest test passes, and C# refuses an ONNX model with a different Encoding version.
- `train.py` can drive the loss near zero on a small fixed set of positions (overfit test: the network can learn at all).
- Classic-mode search with a uniform policy and zero value behaves like plain MCTS on the same positions (search-bug check against a known reference).

### P7. Training loop and Run control
**Build:** `7wd selfplay` (parallel games, Game Records, `.npy` shards, per-game saving); `loop.py` with Generations, sliding window, shard cleanup, Matches every Generation and against the Anchor every 5, the "best" pointer; Run directories with frozen `config.yaml` and recorded commit/manifest/machine; `7wd train start | pause | status`, the Windows desktop shortcuts, checkpointing, self-stop on plateau / cap / warm-start trigger / fault, with Windows notifications; TensorBoard and `generations.csv`; the tiny config.
**Runnable:** `7wd train start` on the laptop in the tiny config, controllable over SSH.
**Validation:**
- The smoke test (2 tiny Generations, Encoding handshake) passes in CI on Linux and on the Windows desktop.
- A short laptop Run in classic mode, then in Gumbel mode, shows Elo against Random rising over its Generations.
- Pause during each step (Self-Play, training, Match) frees the machine within about a minute; Start resumes, losing at most a minute of work.
- Replaying a Game Record from its seeds reproduces its Move Log exactly.
- A Run whose config is edited refuses to start.

### P8. Calibration, pilots and the main Run (Windows desktop)
**Build:** nothing new unless calibration shows a bottleneck. Run the calibration, then the pilots, then the main Run.
**Runnable:** the main Run, started and paused by the owner with the shortcuts.
**Validation, step by step:**
1. **Calibration:** measure Self-Play positions/s and training steps/s; size Generations to ≈ 1 desktop-hour; check the 300-hour cap buys ≈ 500k+ games (otherwise raise it with the owner).
2. **Pilots** (≈ 150 desktop-hours, ≈ 20 each): Small vs Medium network; with vs without derived features; classic vs Gumbel at an equal simulation count; optionally 32 / 64 / 128 training simulations. Freeze the winners into the main Run's config.
3. **Main Run** until plateau or the 300-hour cap, watching the warm-start triggers.
4. **v1 training success:** the "best" network passes the Anchor (base-only Match, lower 95% bound > 50%); report the Elo margin against the +200 aim and the promo Match next to it.

### P9. Advisor Window and manual entry
Can start as soon as P5 passes, using the MCTS Agent; it switches to the "best" network once P8 produces one.
**Build:** the `Advisor` web server and page (A8): data-driven pyramid, Move entry, the Reveal picker, Move Log with undo and rewind, persistence, new-game screen with promo checkboxes, advice on request (badges, compare table with error whiskers and "≈ same", outcome bar, reason tags, expected line, tiles for other Pending Decisions), Think longer.
**Runnable:** `7wd advisor` opens the page in a browser on either machine.
**Validation:**
- The owner enters one complete real game (physical or TTS) by hand; the final scores match the game's score sheet. The log becomes a scripted test.
- Undo and rewind at random points always reproduce the same state as replaying the truncated log.
- Advice at 800 simulations returns within ~3 s on the desktop (laptop default drops to 400 if needed), and the compare table shows "≈ same" for Moves within noise.

### P10. Tabletop Simulator Adapter
**Build:** the read-only Lua snapshot script and name → slug table in `tts/`; the External Editor API client; place mapping for all three Age grids; settled-table detection; the expected-table projection; reconciliation (one chain appends, several chains offer choices, otherwise Desync); soft checks; seat detection; the promo Desync with its one-click fix; new-game detection; restart catch-up. Settle the A12 TTS checks first, with the probe and the owner at the desktop.
**Runnable:** the Advisor connected to a live TTS game.
**Validation:**
- Loading the committed save `docs/research/tts-mod/ml-practice.json` in TTS gives a Table Snapshot that matches the probe's findings place by place (13 Age II cards taken, 7 discards, the built Wonders, 2 Progress tokens taken), with no hidden card named.
- **The v1 acceptance game:** the owner plays one complete base game in TTS with the Advisor connected. Every Move is appended automatically, with manual input only for Reveals the table can't show; advice comes from the "best" network; final scores match the mod's scoring.
- Induced mistakes (a card dropped in the wrong city, a moved coin, a restart mid-game) produce, respectively, a Desync that clears when fixed, a soft-check note only, and a clean catch-up.
- One game with a promo Wonder in the pool, including the unticked-promo Desync and its fix.

**v1 is done** when P8's training success and P10's acceptance game both pass (A1).
