# Game-playing algorithms for 7 Wonders Duel

Research for ticket `05-algorithms-for-hidden-info-games`. Terms in **bold** at first use (Agent, Observation, Game State, Engine, Self-Play, Agent Ladder, Advisor) are defined in [`GLOSSARY.md`](../../GLOSSARY.md).

How to read this: Part 1 explains the ideas in plain language. Part 2 goes through each algorithm in more detail. Part 3 covers compute and running ONNX from C#. Part 4 has the comparison table. Part 5 is a **recommendation**, kept separate so it can be argued with in the Agent Ladder grilling (ticket 07). The references at the end link the primary sources. Numbers marked *(estimate)* are my own back-of-envelope figures, not measurements. Measure them once the Engine exists.

---

## Part 1 — The ideas in plain language

### What kind of game is 7 Wonders Duel, for an algorithm?

Researchers sort games along two questions:

1. **Is there luck (chance)?** In 7WD there is: which cards are in each Age, which are face-down, which 3 are removed, which 3 Guilds join Age III, and which 5 Progress tokens are on the board.
2. **Does anyone know something the other doesn't (hidden information)?** This is the key question, and 7WD's answer is unusual.

In poker, *your opponent knows their own cards and you don't*. That is **private** information. It is what makes poker hard: the opponent's bets carry information about their cards, so you have to reason about what they know, what they think you know, bluffing, and so on.

In base 7WD, almost all hidden information is **hidden from both players equally**. Nobody knows what a face-down pyramid card is until it flips. Nobody knows which cards were removed. Nobody knows the unused Progress tokens. The one exception is the Great Library Wonder. Its owner secretly looks at 3 of the unused Progress tokens, keeps one and returns the other two to the box ([AADL helpsheet](https://aadl.org/files/catalog_guides/7-Wonder-Duel-Helpsheet.pdf)). Those two tokens never come back into play, so the secret knowledge has almost no effect on later decisions. *(Ticket 01, the rules reference, should confirm this reading of the rules.)*

**Why this matters a lot:** if nobody has private information, the opponent's moves can't "leak" secrets. A face-down card behaves exactly like a dice roll that happens at the moment the card flips. That makes base 7WD, for practical purposes, a **perfect-information game with chance**. Backgammon is in the same family. Poker and Stratego are not. Algorithms for that family are much simpler and better understood than poker-style algorithms. The heavy imperfect-information machinery (ReBeL, Student of Games, CFR) solves a problem base 7WD barely has.

The expansions change this. Agora adds secret Conspiracies, which are real private information, and Pantheon may too. So the design should leave room for upgrading later. It does not need to pay for that machinery now.

### The main families of algorithms, in one paragraph each

- **Heuristic Agent:** hand-written rules or a hand-weighted score ("military lead is worth X, each science symbol is worth Y"). No search, no learning. Fast, easy to understand, and a useful sparring partner. Its ceiling is how well you understand the game.
- **Monte Carlo Tree Search (MCTS):** look ahead by playing many imagined continuations from the current position. Spend more of those imagined games on moves that look promising, and pick the move that held up best. Classic MCTS needs no knowledge beyond the rules. It finishes each imagined game with random moves (a "rollout") to see who wins.
- **Determinization / Perfect-Information Monte Carlo (PIMC):** to handle hidden cards, *guess* them. Fill in every unknown card with a random but consistent guess, which gives a "determinization", then search that fully-known world as if it were real. Repeat for many guesses and average. It is simple and often works very well. It also has two known flaws, explained below.
- **Information-Set MCTS (ISMCTS):** a refinement where a single search tree is shared across all the guesses. The tree is keyed on "what I can see", not on "what is true", so it can't overfit to a single guess.
- **AlphaZero:** MCTS, except that instead of random rollouts a neural network does two jobs. It suggests which moves are worth looking at (the **policy**), and it estimates who is winning (the **value**). The network is trained on the results of the search itself during Self-Play. The search makes the net's suggestions better, and the net makes the search better, in a loop. For hidden cards, combine it with determinization or chance nodes.
- **Gumbel AlphaZero:** a 2022 refinement of AlphaZero that still learns well when the search is small (as few as 2–16 simulations instead of 800). That matters a lot on one GPU.
- **Stochastic MuZero:** AlphaZero for games with chance, where the *rules themselves are learned* by a network. We already have an exact Engine, so we only need its idea of explicit chance steps, not the learned model.
- **ReBeL / Student of Games:** research-grade algorithms that combine search and learning *and* are proven correct for games with real private information (poker, Scotland Yard). They are powerful and very complex. For base 7WD they mostly collapse into AlphaZero.
- **PPO self-play (policy gradient):** skip search entirely. A network learns to pick moves directly, by playing millions of games and nudging up the probability of moves that led to wins. It is simpler to reason about and handles hidden information naturally, because it only ever sees what the player sees. It usually needs more games, and without search it is weaker tactically.

### The two classic failure modes of guessing (determinization)

Frank and Basin (1998) named these, using Bridge as their example. Long et al. (2010) quote them as follows.

- **Strategy fusion.** When you search a world where you *know* the hidden card, your plan assumes you'll always pick the right follow-up for that world. In reality you can't tell the worlds apart, so you can't play differently in each. Averaging over worlds then overrates risky lines of the form "I'll figure it out later". In the paper's words, PIMC "believes it can use a different strategy in each world, whereas in reality there are situations … which consist of multiple perfect information scenarios" ([Long et al. 2010, §2](https://www.cs.du.edu/~sturtevant/papers/pimc.pdf)).
  *7WD example:* a determinized search "knows" the face-down card under the one you're about to uncover is a Guild you want. So it values uncovering it too highly, because in half the worlds it imagines grabbing a great card that in reality it can't foresee.
- **Non-locality.** In real hidden-information games, how good a position is can depend on *other* parts of the game tree, "primarily due to the opponent's ability to direct the play towards regions of the tree that he knows … are favorable for him, using private information that he possesses but we do not" (ibid.).
  *7WD:* this needs the opponent to have private information. Base 7WD has essentially none, so **non-locality is close to irrelevant here.**

Strategy fusion can be fixed cheaply in 7WD. Don't fix the hidden cards at the start of a search. Instead, **draw each hidden card at the moment it flips during the imagined game, freshly for each imagined game.** Both players are equally ignorant, so this is not an approximation. It is an exact model of the game (the "draw it when you need it" principle). The search can never plan around a card it couldn't know.

### Why PIMC tends to work anyway

Long, Sturtevant, Buro and Furtak (2010) measured which properties of a game make PIMC good or bad ([paper](https://www.cs.du.edu/~sturtevant/papers/pimc.pdf)):

- **Leaf correlation:** how often the outcome is already settled near the end, whatever you do. High correlation helps PIMC.
- **Bias:** whether the game strongly favours one side. This had a small effect.
- **Disambiguation factor:** how quickly hidden information gets revealed during play. They note that "as disambiguation approaches 1, the performance of PIMC improves drastically, since the game is approaching a perfect information game."

7WD reveals hidden cards steadily. Every taken card can flip a face-down one, and each Age's pyramid is fully revealed by the time it is played out. And no information is ever private. Both factors point towards determinization-style methods working well here *(my inference from their framework; not measured for 7WD)*.

---

## Part 2 — The algorithms in more detail

### 2.1 Heuristic Agents

**What:** score each legal Move with a hand-written evaluation (resources gained, coins, military shift, science progress, denying the opponent a card, and so on). Or score the resulting position and look 1–2 Moves ahead (a small "expectimax", averaging over card reveals).

- *Strength:* beats random easily. A good one plays at a reasonable casual level. It is weak at long plans such as setting up a science win and at timing.
- *Complexity:* low, and entirely C#.
- *Compute:* negligible.
- *Learnability:* high. It teaches you the game's levers, which is useful later when designing the network's input encoding.
- *Role:* an essential rung of the Agent Ladder and the *opponent that verifies* later Agents are actually improving. Its weights can also be tuned by self-play matches (a simple black-box optimiser), a gentle first taste of "learning".

### 2.2 MCTS with determinization (PIMC)

**What:** sample N determinizations of the hidden cards consistent with the Observation. Run ordinary MCTS (UCT selection plus random or heuristic rollouts) on each. Sum the root move visit counts or values across the searches, and play the best ([Cowling et al. 2012 §III](https://doi.org/10.1109/TCIAIG.2012.2200894) describe this baseline).

- *Strength:* typically a big jump over heuristics in card games. Long et al. note that PIMC produced expert play in Bridge and Skat and strong play in Hearts.
- *Failure modes:* strategy fusion (see above; fix by sampling at reveal time). Non-locality (negligible in base 7WD). Also note that it can't "reason about information", for example deliberately keeping an opponent ignorant. That barely matters when nobody knows more than anyone else.
- *Sampling correctly:* the sampler must reproduce 7WD's setup process given what has been seen. For Age III, exactly 3 of the 7 Guilds are in the 20-card layout, and the 3 removed cards come from the non-Guild Age III cards. The simplest correct approach is to rerun the Engine's own setup and *condition on the Observation*: fix the visible cards and shuffle the unseen ones into the unseen slots according to the deck rules. Get this wrong and the Agent will consistently misjudge the odds of a given card turning up.
- *Honesty:* during Self-Play the Engine holds the true Game State. The search **must** build its imagined worlds from the Observation only, never from the real hidden cards. Otherwise it is cheating, and anything trained on that search learns to expect information it won't have in Advisor play.
- *Complexity:* low to moderate. *Compute:* CPU only; thousands of simulations per move in C# are cheap. *Learnability:* high. It is the foundation of everything that follows.

### 2.3 Information-Set MCTS (ISMCTS)

**What:** Cowling, Powley and Whitehouse (2012) proposed searching a single tree whose nodes are **information sets**, meaning everything a player can't tell apart, rather than true states. Each iteration samples a fresh determinization and walks down the shared tree, using only branches legal in that sample ([paper](https://doi.org/10.1109/TCIAIG.2012.2200894)). The single-observer variant (SO-ISMCTS) is the simple one. Multi-observer MO-ISMCTS tracks the opponent's view separately, which matters when the opponent has private information.

- *Why it helps:* statistics are shared across worlds, so more of the compute goes to the real decision. And because the tree is keyed on what the player can see, it can't settle on a plan that depends on a single guess.
- *For 7WD:* with no private information, SO-ISMCTS that re-samples each iteration is essentially the same as MCTS with **chance nodes sampled at reveal time** (an "open-loop" tree over Moves, with reveals drawn as they happen). That is the cheapest correct form here. It is the same tree shape you'd use under AlphaZero, so it makes a natural stepping stone.
- *Complexity:* moderate. *Compute:* CPU. *Learnability:* good; it teaches the "decide from the Observation" discipline.

### 2.4 AlphaZero (with determinization / chance nodes)

**What:** Silver et al. (2018) replaced rollouts with a neural network f(s) → (policy p, value v). The search uses p as a prior for which Moves to explore (PUCT selection) and v instead of a rollout. Self-Play produces training targets: the search's visit distribution becomes the policy target, and the final game result becomes the value target. They trained "from random play, without any domain knowledge except the game rules" and beat world-champion programs in chess, shogi and Go ([Science 2018](https://doi.org/10.1126/science.aar6404); [arXiv preprint](https://arxiv.org/abs/1712.01815)). Expert Iteration (Anthony et al. 2017, [arXiv](https://arxiv.org/abs/1705.08439)) is the same idea, discovered independently.

**Adapting to 7WD:**

1. **Network input = the Observation**, never the hidden cards. The value then means "expected result given what I can see", which is what the Advisor needs.
2. **Chance inside search:** when a simulation uncovers a face-down card (or starts a new Age), draw its identity from the conditioned sampler at that moment. Either keep a chance node with one child per drawn outcome, or treat the draw as open-loop. Both avoid strategy fusion.
3. **Training targets** come from the search policy and the actual game outcome, which already averages over luck.

The "AlphaZero is fine for imperfect-information games" idea has direct support. Blüml, Czech and Kersting (2023) found AlphaZero-style baselines with determinization "surprisingly strong" in Stratego and DarkHex, games with far more *private* information than 7WD. They report win rates comparable to other Stratego bots, while still losing to specialised equilibrium methods ([Frontiers in AI 2023](https://doi.org/10.3389/frai.2023.1014561)).

- *Strength:* the highest practical ceiling for this project.
- *Complexity:* high but well documented. You need search with batched network calls, a replay buffer, a training loop, ONNX export, and model versioning and evaluation.
- *Compute:* GPU for network inference during Self-Play. See Part 3.
- *Learnability:* the best-documented route from "classic search" to "learned search". Every component you build on the earlier rungs (Engine, sampler, MCTS) is reused.

**Original AlphaZero compute, for perspective:** 5,000 first-generation TPUs generating Self-Play and 800 simulations per Move (Silver et al. 2018). That is far beyond one GPU, but chess and Go are much bigger than 7WD. Later work cut the cost a lot. KataGo reports roughly 50× less compute than earlier comparable Go bots, through techniques such as randomising the playout cap ([Wu 2019](https://arxiv.org/abs/1902.10565)). Gumbel AlphaZero targets tiny search budgets directly.

### 2.5 Gumbel AlphaZero and Stochastic MuZero

**Gumbel AlphaZero** (Danihelka, Guez, Schrittwieser, Silver, ICLR 2022): AlphaZero "can fail to improve its policy network, if not visiting all actions at the root of a search tree". Their fix samples root Moves *without replacement*, using Gumbel noise and a "Sequential Halving" schedule, and derives a policy target that provably improves the policy. It "significantly improve[s] prior performance when planning with few simulations" ([ICLR 2022](https://openreview.net/forum?id=bERaNdoegnO); [ICLR page](https://iclr.cc/virtual/2022/poster/6418)).
*Why we care:* the cost of Self-Play is roughly proportional to simulations per Move. Going from 800 to 16–64 simulations gives 10–50× more games for the same GPU. 7WD has a modest number of legal Moves per turn (a handful of accessible cards × {build, discard, build a Wonder}), which suits Sequential Halving. The change is local to the root of the search and the policy target, so it can be retrofitted onto plain AlphaZero later. DeepMind's open-source `mctx` library implements it, as a reference ([github.com/google-deepmind/mctx](https://github.com/google-deepmind/mctx)).

**Stochastic MuZero** (Antonoglou et al., ICLR 2022): MuZero learns a model of the rules. The stochastic version adds **afterstates** (the position right after your Move, before chance resolves) and **chance outcomes**, so the planner handles dice-like events correctly. It is demonstrated on 2048 and backgammon ([OpenReview](https://openreview.net/forum?id=X6D9bAHhBQ1)).
*Why we care:* the afterstate → chance → next state structure is exactly the right shape for "take card → face-down card flips". **We do not need the learned model.** Our exact C# Engine plus conditioned sampler already provide true chance outcomes. Learning the model would add a large source of error and complexity for nothing. Take the idea, not the algorithm.

### 2.6 ReBeL and Student of Games

**ReBeL** (Brown, Bakhtin, Lerer, Gong, NeurIPS 2020) does search plus learning over **public belief states**: the public information plus a probability distribution over each player's possible private information. It provably converges to a Nash equilibrium in two-player zero-sum games, and beat a top human professional at heads-up no-limit poker. The authors note that in perfect-information games ReBeL simplifies to an algorithm similar to AlphaZero ([arXiv 2007.13544](https://arxiv.org/abs/2007.13544)).

**Student of Games** (Schmid et al., Science Advances 2023) unifies the two worlds. It uses growing-tree counterfactual regret minimisation (GT-CFR) plus "sound self-play", and reaches strong play in chess, Go, poker and Scotland Yard with one algorithm ([Science Advances](https://doi.org/10.1126/sciadv.adg3256); [arXiv 2112.03178](https://arxiv.org/abs/2112.03178)).

*For 7WD:* both exist to handle **private** information. In base 7WD each player's private information is empty, so the belief state collapses to "the public state", and ReBeL is essentially AlphaZero with chance. You would pay a lot of complexity (CFR solving inside search, belief tracking, harder debugging) for little or no strength gain. They become relevant only if Agora's Conspiracies, or the private parts of another expansion, turn out to matter for strength. Even then, Blüml et al.'s result suggests trying determinized AlphaZero first.

DeepNash (Perolat et al., Science 2022), which reached expert level in Stratego with a model-free equilibrium-seeking method (R-NaD), belongs to the same "real private information" class and needed far more compute ([Science](https://doi.org/10.1126/science.add4679)).

- *Complexity:* very high. *Learnability for a newcomer:* poor as a first project, excellent as later reading.

### 2.7 PPO-style policy-gradient Self-Play

**What:** Proximal Policy Optimization (Schulman et al. 2017) is the standard "workhorse" reinforcement-learning algorithm. A policy network plays, and its move probabilities are nudged towards moves whose results beat the value network's expectation. A "clipped" objective stops each update from changing the policy too much ([arXiv 1707.06347](https://arxiv.org/abs/1707.06347)). In Self-Play the opponent is the current network or a pool of past versions.

**Evidence for hidden-information games:** Rudolph et al. ran over 5,000 training runs across several imperfect-information benchmarks with exact exploitability measurement. They found that generic policy-gradient methods like PPO are "competitive with or superior to" fictitious-play, double-oracle and CFR-based deep RL methods ([arXiv 2502.08938](https://arxiv.org/abs/2502.08938), ICLR 2026).

- *Strengths:* the network sees only the Observation, so hidden information is handled "for free" and there is no determinization or sampler inside search. Each game is cheap (one network call per Move, no search), and there is excellent library support (e.g. Stable-Baselines3, CleanRL).
- *Weaknesses:* it typically needs many more games than search-based learning. A raw policy has no lookahead, so it is weak at tactical sequences such as racing for the 6th science symbol or a military push. Training can be unstable (forgetting, cycling strategies), which is why people keep pools of past opponents.
- *Fit with our stack:* PPO is **on-policy**. After every update the data must be regenerated with the new network. Our games live in C# and training lives in Python. So either (a) C# plays with an ONNX export of the current policy and ships the data to Python each iteration, which is workable (export is fast and iterations are a few minutes), or (b) the Engine is exposed to Python, which conflicts with [ADR 0001](../adr/0001-csharp-engine-python-training.md)'s speed rationale. AlphaZero's loop tolerates stale networks much better, because its data comes from search and has a replay buffer, so it fits the C#/Python split more naturally.
- *Hybrid:* a PPO-trained value/policy net can still be dropped into MCTS at play time ("search on top").
- *Learnability:* the algorithm is conceptually simple, but tuning RL hyper-parameters is notoriously fiddly for newcomers.

---

## Part 3 — Compute and running the network from C#

### 3.1 How big is 7WD, for compute purposes? *(estimates — confirm with the Engine)*

- Decisions per game: about 60 card turns (20 per Age), plus the 8 Wonder-draft picks, Progress-token picks, extra turns and "who starts the next Age" choices. **Roughly 70–80 decisions per game.**
- Legal Moves per decision: up to ~6 accessible cards × {construct, discard, build one of up to 4 Wonders}. **Typically about 5–20, rarely above ~30.** That is tiny next to Go (~250) or chess (~35, but in much longer games).
- Network: the Observation fits in a few hundred to a couple of thousand numbers (cards, coins, military track, tokens, Wonders). A residual MLP of ~0.5–5 M parameters is a sensible starting size. Ticket 08 owns the encoding.

### 3.2 Self-Play throughput on one consumer NVIDIA GPU *(estimate)*

- A small MLP at batch 256–1024 should manage something like **10⁵ evaluations per second** on a recent consumer card (order of magnitude; it depends on the card, the model and transfer overhead). In practice the limit is usually feeding the GPU: CPU-side tree work, copying data, and launching the network calls.
- Cost per game ≈ decisions × simulations per Move ≈ 75 × 200 = 15,000 evaluations (classic AlphaZero settings scaled down), or 75 × 32 ≈ 2,400 (Gumbel-style small budget).
- That gives roughly **5–40 games per second**, i.e. **~0.5–3 million games per day** in theory. Assume several times less in a first implementation.
- **How many games are needed?** Unknown for 7WD. Small published or hobby AlphaZero setups for games of similar size (Connect Four, Othello, small board games) typically need from **tens of thousands to a few million** Self-Play games. Expect **a clear win over the heuristic and PIMC Agents within the first ~10⁵ games (hours to a day)**, with further gains over **days to a couple of weeks** of GPU time. Treat this as a planning range, not a promise. The map's "compute budget and stopping rule" item should fix the actual budget once Engine speed is measured.
- Training the network in Python/PyTorch is cheap next to Self-Play at this model size.
- CPU matters too. Each concurrent game needs CPU for the Engine and tree, so the Windows desktop's core count sets the ceiling as much as the GPU does.
- The Linux laptop (no NVIDIA GPU) can run everything on the CPU execution provider for development and small tests. Real training runs on the Windows box.

### 3.3 Running ONNX models from C#

**Packages and platforms** (official docs):

- `Microsoft.ML.OnnxRuntime` is the CPU build (Windows, Linux, Mac). `Microsoft.ML.OnnxRuntime.Gpu` is the CUDA build. On NuGet it is a meta-package that pulls `Microsoft.ML.OnnxRuntime.Gpu.Windows` and `Microsoft.ML.OnnxRuntime.Gpu.Linux` (current version 1.31.0 at time of writing) ([C# getting started](https://onnxruntime.ai/docs/get-started/with-csharp.html); [NuGet](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu)). One C# codebase can therefore choose CPU on the laptop and CUDA on the desktop at runtime.
- **CUDA/cuDNN versions:** "Starting with version 1.27, GPU packages published to PyPI (`onnxruntime-gpu`) and NuGet (`Microsoft.ML.OnnxRuntime.Gpu`) are built with CUDA 13.0 by default." Separate CUDA 12.x builds require CUDA ≥ 12.8. cuDNN 9.x is required, and cuDNN 8 and 9 builds are not interchangeable ([CUDA EP docs](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)). The generic [install page](https://onnxruntime.ai/docs/install/) still says "the default CUDA version for ORT is 12.x". The CUDA EP page is more specific, so trust it, and check the release notes for the version you pin. The NVIDIA driver must support the CUDA major version used.
- On Windows the CUDA and cuDNN `bin` folders must be findable (PATH). On Linux they must be on `LD_LIBRARY_PATH` (install page). DirectML is an alternative Windows-only GPU provider, but it is in "sustained engineering" mode, so prefer CUDA.

**Enabling CUDA in C#** (from the CUDA EP docs):

```csharp
var cuda = new OrtCUDAProviderOptions();            // dispose when done
cuda.UpdateOptions(new Dictionary<string, string> { ["device_id"] = "0" });
using var options = SessionOptions.MakeSessionOptionWithCudaProvider(cuda);
using var session = new InferenceSession("model.onnx", options);
```

**Performance notes from the docs:**

- "Creating and loading sessions are expensive" (C# guide). Create one session per model version and reuse it.
- Use `OrtValue` over pinned, reused buffers to avoid per-call allocations. `OrtValue` is `IDisposable` and must be disposed (C# guide).
- The CUDA EP page recommends **I/O binding** "to avoid overhead resulting from copies on inputs and outputs". CUDA Graphs further cut launch overhead but need I/O binding, fixed shapes, no control-flow ops, and are not multi-thread safe. They are a later optimisation.

**Batching leaf evaluations across parallel games** (the standard design; my synthesis):

1. Run **many games at once** (e.g. 256–1024) on CPU worker threads. Each game's search runs until it needs a network evaluation, posts the encoded Observation to a shared queue, and moves on to another game (or awaits a `Task`) instead of blocking.
2. A **single inference thread** drains the queue into one batch tensor of up to the maximum batch size, or whatever has arrived after a short timeout. It calls `session.Run` once and hands each result back to its waiting search.
3. Within one search you can also gather several leaves per pass by using a "virtual loss", a temporary penalty that steers parallel descents apart. Spreading work across games is simpler and usually enough.
4. Export the network from PyTorch with a **dynamic batch dimension**, so one model accepts any batch size.

Funnelling all GPU calls through one thread also sidesteps a question the official docs don't clearly answer: whether concurrent `Run` calls on one session are safe. I found no official ONNX Runtime documentation stating that `InferenceSession.Run` is thread-safe. It is widely relied on in practice, but CUDA/TensorRT bugs with multi-threaded `Run` have been reported and fixed (e.g. [onnxruntime#15154](https://github.com/microsoft/onnxruntime/issues/15154)). A single inference thread avoids the issue entirely.

---

## Part 4 — Comparison table

Ratings are relative, for this project (base 7WD, one consumer GPU, C#/Python split, ML newcomer).

| Algorithm | Expected strength in 7WD | Implementation complexity | Compute | Handles 7WD's hidden info | Newcomer learnability | Fits C#/ONNX split |
|---|---|---|---|---|---|---|
| Heuristic Agent | Low–medium | Low | Negligible (CPU) | Ignores it (fine) | Excellent | n/a (pure C#) |
| PIMC (MCTS + determinization, rollouts) | Medium | Low–moderate | CPU | Yes; strategy fusion fixable by sampling at reveal | Excellent | n/a (pure C#) |
| ISMCTS (SO) / MCTS with chance nodes | Medium (+ over PIMC) | Moderate | CPU | Yes, cleanly | Good | n/a (pure C#) |
| AlphaZero + chance/determinization | High | High | GPU (Self-Play dominates) | Yes, net sees Observation only | Good (best-documented learned path) | Natural: C# search + batched ONNX, Python trains from a replay buffer |
| Gumbel AlphaZero (add-on) | High, reached with less compute | High (+ small delta over AZ) | 10–50× fewer sims/Move | Same as AZ | Good once AZ is understood | Same as AZ |
| Stochastic MuZero (learned model) | High in theory | Very high | GPU, more | Yes | Poor | Adds a learned model we don't need |
| ReBeL / Student of Games | High; little or no gain over AZ in base game | Very high | GPU + CFR in search | Yes, provably, incl. private info | Poor (research-grade) | Awkward |
| PPO Self-Play (no search) | Medium–high; tactically weaker without search | Moderate (library) but fiddly tuning | Cheap per game, many games | Yes, naturally | Medium | On-policy loop crosses C#↔Python every iteration |

---

## Part 5 — Recommendation (for discussion in ticket 07)

> **This section is a recommendation, not a decision.** The Agent Ladder grilling (ticket 07) decides.

1. **Treat base 7WD as a perfect-information game with chance.** Hidden cards are chance events drawn *when revealed*, from a sampler conditioned on the Observation. Agents decide only from the Observation. This one design choice removes strategy fusion, makes non-locality moot, and keeps Advisor play honest.
2. **Ladder (each rung reuses the last):** Random → Heuristic → PIMC/MCTS with rollouts → MCTS with chance nodes (the ISMCTS-equivalent) → **AlphaZero with chance nodes**, Observation-only network, ONNX inference from C# → **Gumbel root selection** added once plain AlphaZero works, to cut simulations per Move and multiply games per GPU-hour.
3. **Don't build ReBeL / Student of Games / Stochastic MuZero for v1.** Revisit ReBeL-style beliefs only if expansion private information (Agora Conspiracies) measurably hurts the determinized Agent.
4. **PPO is optional:** a reasonable side experiment or baseline, not the main path, because its on-policy loop fights the C#/Python split and it lacks lookahead.
5. **Inference design:** many concurrent games → one batching inference thread → one `InferenceSession` (CUDA on Windows, CPU on Linux), with a dynamic batch dimension, reused `OrtValue` buffers, and I/O binding later.

## Open uncertainties

- The Great Library and any other private-knowledge effects need confirming against the official rulebook (ticket 01). If something private matters more than assumed, MO-ISMCTS or belief modelling becomes more relevant.
- All throughput and games-needed figures are estimates. Measure the Engine's Moves per second and the ONNX evaluations per second on the real desktop GPU before fixing a budget.
- The official ONNX Runtime docs don't clearly state whether concurrent `Run` calls on one session are thread-safe (hence the single-inference-thread design). The install page and CUDA EP page disagree on the default CUDA version (12.x vs 13.0 since ORT 1.27).
- Expansion private information (Agora Conspiracies, possibly Pantheon) has not been researched here.

## References (primary sources)

- Frank, I. & Basin, D. (1998). Search in games with incomplete information: a case study using Bridge card play. *Artificial Intelligence* 100. (Origin of "strategy fusion" and "non-locality"; quoted via Long et al.)
- Long, J., Sturtevant, N., Buro, M., Furtak, T. (2010). Understanding the Success of Perfect Information Monte Carlo Sampling in Game Tree Search. *AAAI-10*. https://www.cs.du.edu/~sturtevant/papers/pimc.pdf · https://ojs.aaai.org/index.php/AAAI/article/view/7562
- Cowling, P., Powley, E., Whitehouse, D. (2012). Information Set Monte Carlo Tree Search. *IEEE TCIAIG* 4(2). https://doi.org/10.1109/TCIAIG.2012.2200894
- Silver, D. et al. (2018). A general reinforcement learning algorithm that masters chess, shogi, and Go through self-play. *Science* 362. https://doi.org/10.1126/science.aar6404 · https://arxiv.org/abs/1712.01815
- Anthony, T., Tian, Z., Barber, D. (2017). Thinking Fast and Slow with Deep Learning and Tree Search (Expert Iteration). *NeurIPS*. https://arxiv.org/abs/1705.08439
- Wu, D. J. (2019). Accelerating Self-Play Learning in Go (KataGo). https://arxiv.org/abs/1902.10565
- Danihelka, I., Guez, A., Schrittwieser, J., Silver, D. (2022). Policy improvement by planning with Gumbel. *ICLR*. https://openreview.net/forum?id=bERaNdoegnO · https://iclr.cc/virtual/2022/poster/6418
- Antonoglou, I. et al. (2022). Planning in Stochastic Environments with a Learned Model (Stochastic MuZero). *ICLR*. https://openreview.net/forum?id=X6D9bAHhBQ1
- Brown, N., Bakhtin, A., Lerer, A., Gong, Q. (2020). Combining Deep Reinforcement Learning and Search for Imperfect-Information Games (ReBeL). *NeurIPS*. https://arxiv.org/abs/2007.13544
- Schmid, M. et al. (2023). Student of Games. *Science Advances* 9. https://doi.org/10.1126/sciadv.adg3256 · https://arxiv.org/abs/2112.03178
- Perolat, J. et al. (2022). Mastering the game of Stratego with model-free multiagent reinforcement learning (DeepNash). *Science* 378. https://doi.org/10.1126/science.add4679
- Blüml, J., Czech, J., Kersting, K. (2023). AlphaZe∗∗: AlphaZero-like baselines for imperfect information games are surprisingly strong. *Frontiers in AI* 6. https://doi.org/10.3389/frai.2023.1014561
- Schulman, J. et al. (2017). Proximal Policy Optimization Algorithms. https://arxiv.org/abs/1707.06347
- Rudolph, M. et al. (2025/2026). Reevaluating Policy Gradient Methods for Imperfect-Information Games. *ICLR 2026*. https://arxiv.org/abs/2502.08938
- ONNX Runtime docs: CUDA Execution Provider https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html · C# getting started https://onnxruntime.ai/docs/get-started/with-csharp.html · Install https://onnxruntime.ai/docs/install/ · NuGet `Microsoft.ML.OnnxRuntime.Gpu` https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu
- Google DeepMind `mctx` (reference Gumbel MuZero implementation): https://github.com/google-deepmind/mctx
- 7WD rules (Great Library): AADL helpsheet https://aadl.org/files/catalog_guides/7-Wonder-Duel-Helpsheet.pdf (secondary; confirm in official rulebook, ticket 01).
