# Neural network input and output encoding

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 06, 07

## Question

How is an Observation turned into the network's input tensor, how is the Move space encoded as a fixed policy output (with legal-move masking), what does the value head predict (win probability? also victory type?), and what network shape/size do we start with? Must be defined identically in C# and Python (see ADR 0001), and leave room for Pantheon/Agora.

## Answer

Resolved by grilling on 2026-10-09. Decision record: [ADR 0003](../../../docs/adr/0003-csharp-sole-encoder.md). New glossary term: Encoding.

**Input: a flat vector of about 2,200 numbers, read by an MLP.** Why not a set of components and a Transformer (ZeusAI)? That design is far bigger and slower. Self-Play speed is the bottleneck, and a flat vector is easy to inspect. A set encoding is a possible later experiment if the MLP plateaus.

- **Point of view:** relative ("me / opponent", where "me" is the player to move). The network learns one skill instead of two mirrored ones, and the value always means "chance that I win".
- **Unknown information is zeros, never guessed.** Face-down slots show only their back. The opponent's Great Library draw is absent.
- **Raw identities plus a small set of derived features.** The derived features are the ones the Heuristic needs anyway, so the computation exists once.

| Block | Contents | ~Size |
|---|---|---|
| Layout (slot-centric) | 20 slots × {empty, face-down Age back, face-down Guild back, face-up, accessible} + 73-way card one-hot; current Age one-hot | 1,560 + 3 |
| Cards elsewhere (card-centric) | 73 cards × {my city, opp city, discard, under my Wonder, under opp Wonder} | 365 |
| Unseen | 73 multi-hot: cards that could still be revealed | 73 |
| Wonders | 12 × {in draft offer, mine unbuilt, mine built, opp unbuilt, opp built, removed} | 72 |
| Progress tokens | 10 × {on board, mine, opp, in my Great Library draw} | 40 |
| Scalars | coins (me, opp), Conflict Pawn position, military tokens left (4), I started the Age, ruleset start-rule bit | ~11 |
| Turn | Pending Decision kind (one-hot), play-again flag, destroy target colour | ~12 |
| Derived | production and trading price per resource (me, opp), science symbols held, VP so far, distance to each capital | ~40 |

- **Why the layout is slot-centric:** the network needs the geometry, i.e. which card a take uncovers. Cards outside the layout have no geometry, so they are card-centric.
- **Scaling:** counts are scaled to roughly 0–1 (e.g. coins ÷ 20, capped).
- **Exact field order and scaling** are finalised in code and recorded in the manifest.

**Policy output: about 1,140 logits, one block per Move kind, indexed by card or Wonder (never by slot), so an index always means the same Move.** Illegal Moves are masked to −∞ before the softmax.

| Block | Size |
|---|---|
| `build <card>` | 73 |
| `discard <card>` | 73 |
| `wonder <card> <wonder>` (by Wonder ID, not the player's Wonder slot) | 876 |
| `draft <wonder>` | 12 |
| `token <token>` | 10 |
| `library <token>` | 10 |
| `destroy <card>` (brown/grey only) | ~13 |
| `mausoleum <card>` | 73 |
| `start me` / `start opponent` | 2 |

**Heads and targets:**
- **Value:** tanh output in [−1, +1], used by search. Target: the final game result from the mover's view (+1 / 0 / −1).
- **Outcome class (auxiliary):** 7 classes: I win by military, science or civilian; draw; opponent wins by military, science or civilian. It has a low loss weight. Target: the actual result. It feeds Advisor explanations and Match victory-type reporting.
- **No score-margin head.** Military and science wins have no margin.
- **Policy target:** Gumbel's improved policy (prior plus completed Q-values). In classic mode it is the root visit counts.
- Loss weights and any mixing of search value into the value target belong to "Training pipeline and two-machine workflow".

**Network:** a residual MLP, meaning blocks of two fully-connected layers plus a skip connection. A shared trunk feeds the three heads.
- **Starting size:** 256 wide × 4 blocks (~1.4M parameters).
- The size is training config, not part of the Encoding.
- Growing to 512 × 6 (~5M) is a planned experiment: does a bigger brain beat a faster one at equal GPU-hours?

**Ownership (ADR 0003):**
- C# is the only encoder. Self-Play stores already-encoded examples plus the Move Logs, so data can be re-encoded later.
- A generated, versioned encoding manifest (sizes and index names) is the contract with Python.
- ONNX models carry the Encoding version, and C# rejects a mismatch.

**Expansions:** Pantheon/Agora arrive as a new Encoding version that adds blocks (e.g. god cards, `invoke <god>`), with no reserved slots. A new network may be warm-started by copying the weights that still line up.

**Handed on to "Training pipeline and two-machine workflow":**
- loss weights and value-target mixing;
- the Small vs Medium network experiment;
- the "with vs without derived features" experiment.
