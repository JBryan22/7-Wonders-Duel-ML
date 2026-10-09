# Prior art: existing 7 Wonders Duel engines, AIs and data

Research for ticket `04-prior-art-7wd-ai`. Researched 2026-10-09.

**Method.** I searched the GitHub API for "7 wonders duel", "seven wonders duel", "sevenwondersduel" and AI terms (about 100 repos), plus arXiv, the web, and the BGA forum. I shallow-cloned and read the code of every repo that has an AI, ML or reusable data. I built two engines and ran throughput benchmarks on the dev laptop. Claims marked *(code)* were checked in the source. Claims marked *(README)* or *(paper)* are the author's own words and I did not reproduce them. Commit hashes are the HEAD commits I read.

## TL;DR

- **Only one strong AI exists, and it is closed source: ZeusAI** (Paolini et al., CoG 2024). It is AlphaZero-style: MCTS plus a ~92M-parameter Transformer, trained on ~420k self-play games. It won 26 of 38 games against top BGA players. The authors will not release code, citing cheating ([FAQ](https://sites.google.com/view/zeus-ai/faq)). Its paper is the best blueprint we have for a neural Agent.
- **No open project has a trained neural Agent with a measured strength above toy level.** The open AIs are plain MCTS (C++ and Kotlin), alpha-beta over a hand-written evaluation with determinization (TypeScript and Python), and one work-in-progress AlphaZero and PPO repo (Python, MIT) with no published results.
- **Key modelling insight, shared by ZeusAI and the two best open MCTS engines:** in the base game, nothing hidden is known to only one player. Face-down cards and removed cards are unknown to *both* players. So you can treat each reveal as a **chance event drawn at the moment of reveal**, and the game becomes perfect information with chance nodes. ISMCTS is not needed. ZeusAI's FAQ attributes its success to "symmetric information".
- **Engine speed:** a tuned C++ engine (indjev99) ran **~66,000 random games/s on one core** of this laptop. A straightforward pure-Python engine (JrCrespi) ran **~67 games/s**, about 1000× slower. This is stronger evidence for ADR 0001's C# choice than the ADR's own 50–100× estimate.
- **Reusable data:** we have three machine-readable card sets. The only clearly permissive ones are kilianmandon (MIT, JSON) and AlexisDeseure (BSD-3, JSON, French card names). The most authoritative source, including **Pantheon and Agora**, is the BGA implementation (PHP, BGA licence). Use it as a reference, not as code to copy.
- **Pantheon/Agora** are covered only by the BGA implementation (koenheltzel) and MC-freshman (TypeScript, no licence, Agora data partly approximated). No AI work covers them, apart from MC-freshman's heuristic bot.

## Catalogue

Language, licence and status are taken from the repo itself. "No licence" means all rights are reserved by default: we can read it for ideas but cannot copy code.

| Project | Lang | Licence | What it is | AI approach | Strength evidence | Exp. |
|---|---|---|---|---|---|---|
| **ZeusAI** (paper, [arXiv 2406.00741](https://arxiv.org/abs/2406.00741)) | n/a (unpublished) | closed | Research AI | AlphaZero-style MCTS + Transformer | 26/38 vs top BGA players; 2-1 vs a BGA champion (2026) | base only |
| [indjev99/7-Wonders-Duel-AI](https://github.com/indjev99/7-Wonders-Duel-AI) @01ed7a5f | C++17 | **MIT** | Fast engine + ImGui GUI + BGA bridge | UCT MCTS with heuristic rollouts; "blind reveals" variant | None published (built-in self-benchmark only) | base only |
| [kilianmandon/seven-wonders-rl](https://github.com/kilianmandon/seven-wonders-rl) @0816ece9 | Python (PyTorch, TorchRL) | **MIT** | RL playground (WIP) | AlphaZero-general port + PPO via PettingZoo | None published | base only |
| [marcotama/seven_wonders_duel](https://github.com/marcotama/seven_wonders_duel) @44fe3b59 | Kotlin | none | Multi-game MCTS research framework (2017–19) | Parallel UCT; goal-specific evaluators (military/science/civilian); DDA bot | None published | base only |
| [karthik24iyer/seven-wonders-duel-simulation](https://github.com/karthik24iyer/seven-wonders-duel-simulation) @4eae4768 | Kotlin | none | Derivative of marcotama (2026) with web UI, batch runner, rulebook tests | Same as above + "Opportunist" bot | None published | base only |
| [JrCrespi/AI-7-Wonders-Duel](https://github.com/JrCrespi/AI-7-Wonders-Duel) @ed767dc4 | Python | none | Engine + Pygame + explainable "AI Coach" (created 2026-10-09) | Alpha-beta over determinizations; eval weights tuned by self-play | Tuned weights 80.7% vs hand weights (800 games); Hard vs Easy only 56.2% (240 games) | base only |
| [MC-freshman/7-Wonders-Duel-game](https://github.com/MC-freshman/7-Wonders-Duel-game) @5bc68315 | TypeScript | none | Browser game: base + Pantheon + Agora + Solo, online play | Depth-limited alpha-beta + eval + determinization | 8–24-game benchmarks only | **base, Pantheon, Agora** |
| [koenheltzel/bga-sevenwondersduel](https://github.com/koenheltzel/bga-sevenwondersduel) @f023693f | PHP/JS | BGA licence (see below) | The production BGA implementation | none | n/a | **base, Pantheon, Agora** |
| [AlexisDeseure/7-Wonders-Duel](https://github.com/AlexisDeseure/7-Wonders-Duel) @e3bba710 | C++/Qt | **BSD-3** | Student game, data-driven JSON | Random choice only *(code)* | n/a | base |
| [7wd-io/engine](https://github.com/7wd-io/engine) @2874c010 | Go | none | Engine for the 7wd.io online site; includes promo wonders (Messe, Statue of Liberty) | none | n/a | base + promos |
| [Ridzhi/7wd](https://github.com/Ridzhi/7wd) @50db46af | Rust | none | Engine (README: "engine(ready)") + WIP server | none | n/a | base |
| [ygoduelistharry/7-wonders-duel](https://github.com/ygoduelistharry/7-wonders-duel) @faee5daf | Odin | none | Engine (~1.8k lines) + renderer; aims at AI | Random move selector only so far | n/a | base |
| [lucas-rx](https://github.com/lucas-rx/7-wonders-duel-rl), [fredriksoderberg](https://github.com/fredriksoderberg/7-wonders-duel-rl) "7-wonders-duel-rl" | — | MIT / none | Empty (README only) | — | — | — |

There are many more repos (about 40 C++ university projects, score sheets, solo-automa helpers). I skimmed them and none has a search or learning AI worth studying. One example is pterodactylstfw/7WondersDuel (C++/Qt, README says it has an "AI opponent").

Also out there but closed: the official Repos/Asmodee digital app, which has an AI opponent ([App Store](https://apps.apple.com/us/app/-/id1441651711)), and BGA's own implementation, which has no bots.

## The projects in more detail

### ZeusAI (Paolini, Moreschini, Veneziano, Iraci; CoG 2024 short paper)

Source: [arXiv 2406.00741 PDF](https://arxiv.org/pdf/2406.00741), [project site](https://sites.google.com/view/zeus-ai), [FAQ](https://sites.google.com/view/zeus-ai/faq), [BGA forum thread](https://forum.boardgamearena.com/viewtopic.php?t=36989).

- **Modelling:** the paper says "7WD is a game with no hidden information … However, unlike Go and Chess, 7WD has elements of randomness." Reveals happen in **afterstates** (chance nodes). To bound branching, each afterstate may have at most **11 child states**. Outside training, that limit "is gradually increased as the number of visits to an afterstate increases". A simulation that reaches a full afterstate continues with a random existing child. *(paper §III-B)*
- **Network:** a Transformer encoder with 12 layers, 12 heads, d=768 and FFN 3072, about **92M parameters**. The state is a **set of components** (cards, wonders, tokens). Each component is paired with a learned *position* embedding: a slot in the age structure, a player's city, the discard pile, or under a wonder. Extra tokens carry coins and the military track. **No card costs or effects are encoded**; the network learns them all. Outputs are a value in [−1, 1] and a policy over legal actions. *(paper §III-A)*
- **Search budget:** up to 1k MCTS simulations per move in self-play (visit-proportional sampling). Up to 5k in evaluation games, picking the most-visited move. *(paper)*
- **Training:** the value head was bootstrapped on **35k games from three rule-based policies**: random with a preference for not discarding, random-green and random-red. Then the network was retrained every ~3k self-play games on the most recent ~100k games, for **~420k self-play games in total**. The policy head is trained on root visit distributions. Hardware and wall-clock time are not stated. *(paper §III-C)*
- **Pitfall the paper reports:** science victory took ~100k games to be "discovered" because the reward is sparse. That is why they seeded with green and red biased policies. *(paper §IV-A)*
- **Strength measurement:** best-of-5 matches against 3 top BGA players (won 3–2 and 3–1, lost 2–3), plus an offline series leading 18–6. That is **26/38 (68.4%) overall** *(paper)*. The site later reports Play Festival results of 18–1 (2025) and 18–3 (2026), and a 2–1 win over a BGA champion *(site, unverified)*.
- **Useful reference numbers:** first-player win rate is **66.8%** in 10k self-play games, against 55.7% from 605 human BGA games. Self-play victory mix is 61.7% civilian, 21.4% science, 16.9% military; good BGA humans show 58.0%, 25.6% and 16.4%. Draws are about 0.1%. A 1-coin setup change is worth about 2.7% win rate. Wonder and token preferences are in Figs. 4–6. *(paper)* We can use these as sanity checks for our own Self-Play statistics.
- **Availability:** no code, weights or data. The FAQ says they won't release code, to prevent cheating. You can play it on BGA by request via their site. A full paper "has been submitted" *(site)*. I found no published version on arXiv as of 2026-10-09.

### indjev99/7-Wonders-Duel-AI (C++17, MIT)

This is the most useful open code for our Engine and search design. Everything below is *(code)*.

- **Engine design:** `GameStateT<bool CheckValid>` is a template, so the same code compiles a validated `GameState` and an unchecked `GameStateFast` for search. All state lives in fixed-size `std::array`s (deck locations, pyramid slots, object locations) and is cloned with `clone()`. Actions are a 3-int struct `{type, arg1, arg2}`.
- **Chance as an actor:** reveals are explicit actions by `ACTOR_GAME`, for example `ACT_REVEAL_PYRAMID_CARD (pos, id)`, `ACT_REVEAL_GUILD`, `ACT_REVEAL_WONDER`, `ACT_REVEAL_GAME_TOKEN`, `ACT_REVEAL_BOX_TOKEN` and `ACT_REVEAL_FIRST_PLAYER` (`src/game/action.h`). A pluggable `Revealer` supplies them: `RevealerUniform` for self-play, `RevealerReader` for a log or an external game. So the state **never contains not-yet-revealed identities**, and the agent cannot peek. This maps directly onto our Game State, Observation and Move Log split, and onto Adapters that feed in reveals.
- **Agents:** `AgentMcts` is UCT with explore factor 1.5, a fixed seconds-per-move budget (default 1 s), and chance nodes that pick the least-visited child. Rollouts stop at age boundaries. `AgentMctsBlindReveals` keys arms by (action, slot) and simulates reveals inside the tree with "available count" UCB, so it does not branch on chance outcomes. Rollouts (`game_simulator.cpp`) are **heuristic**: they use build, discard and wonder probabilities (0.35/0.15/0.5), science and military "modes", look-ahead for extra-turn wonders, and a bias toward Age I resources.
- **Real-game bridge:** `scripts/play_bga_game.py` scrapes the BGA DOM with Selenium and talks to the engine over a named pipe, which is effectively an Adapter. Our map rules BGA Adapters out of scope, but the pipe and reveal design is a working example of the pattern.
- **Speed (I measured this):** I built a headless benchmark against the engine sources (g++ -O3, i7-1280P, single thread). Uniform-random agents through the validated `GameRunner` ran **66,455 games/s**. Heuristic `simRandGame` rollouts from setup on `GameStateFast` ran **82,486 games/s**. This is the bar a "fast" engine can reach.
- **Strength:** the README's TODO is "Better AIs". No results are published. `main.cpp` has a `benchmark()` for agent-vs-agent win rates, but no recorded numbers.
- **Gaps:** no unit tests, a Windows-only Makefile (links opengl32 and gdi32), no expansions, card data in C++ tables (`src/game/objects.cpp`).

### kilianmandon/seven-wonders-rl (Python, MIT, WIP Aug 2025)

- **What it has *(code)*:** a Python engine (`game.py`, Pydantic state), a PettingZoo env with **PPO** via TorchRL (`training.py`), and an AlphaZero loop ported from suragnair/alpha-zero-general (`alpha_zero/`; `test_all_games.py` is a leftover from that repo). It also has a FastAPI game server, a React UI, OpenCV card-image segmentation (`img_proc/`), and Terraform for cloud runs.
- **Encodings, a concrete precedent for ticket 08 *(code)*:**
  - **Observation:** 1,966 floats. The player and opponent blocks come first, then discarded-card multi-hot, in-game and "additional" progress tokens, and finally 20 board slots × (one-hot of card identity + 2 for hidden/taken, plus one-hot of card group/back).
  - **Actions:** 386 = 20 slots × 14 (discard / build / wonder 1–4 … ) + 10 tokens + 4 grey-destroy + 9 brown-destroy + 10 box tokens + 73 discard-pile builds. It uses a legal-action mask (logits −1e8).
  - **Network:** separate 2×256 MLPs for actor and critic.
  - **AZ settings:** 100 MCTS sims, 800 episodes per iteration.
- **Pitfalls visible in the code:**
  1. **No wonder draft.** `start_game` deals 4 random wonders to each player, so there is no draft phase and no draft actions.
  2. **The search peeks at hidden cards.** All 20 age cards are dealt up front and a "reveal" only flips the mask. MCTS deep-copies that state, so lookahead reveals the *true* hidden cards. This is the leakage our "decide only from an Observation" rule forbids.
  3. **One generic hyper-parameter set**, with no reported results. The README says it is "rough and work-in-progress".
- **Data *(reusable, MIT)*:** `card_data/` is JSON. It has age 1/2/3 cards (23/23/20), 7 guilds, 12 wonders, chain `transitions.json`, and age layouts as `age_masks.json` (face-up/face-down per slot) plus `age_orders.json` (which slots cover which). The layout encoding is compact and directly usable. Progress tokens are a Python enum, not JSON.

### marcotama/seven_wonders_duel (Kotlin, no licence) and karthik24iyer's derivative

- **Modelling *(README)*:** "If no player knows such information, the information is not encoded in the game state, but its probability distribution is; … drawn from the distribution at run-time." This is the same chance-at-reveal idea.
- **Agents *(README/code)*:** parallel UCT on all but one core. The evaluators reward a win, or a specific victory type (`MctsMilitary`, `MctsScience`, `MctsCivilian`). There is also a dynamic-difficulty bot that aims for 50% (the README says "DDA agent does not work"). Origin: a research project on Dynamic Difficulty Adjustment.
- **Speed hint *(karthik code comment, `mcts/Manager.kt`)*:** "a fixed 1s gave ~1k playouts on the opening move and ~1.5M on the last". The state uses vavr persistent (immutable) collections. This is a cautionary data point: immutable state is convenient but costs one to two orders of magnitude against the C++ engine.
- **karthik's additions (2026) worth stealing as test ideas:** `RulebookTest.kt` cites rulebook pages. Its cases: looting 2 and 5 coins (p12, p14); a city never loses more coins than it has; Masonry and Architecture waive 2 resources; Agriculture pays 6; Lighthouse counts itself and Urbanism pays 4 on a chain build; Economy collects trade spending but not printed coin costs; Shipowners counts one city for brown and grey; ties go to most civilian (blue) points, else shared; wonder cap is 7 in total; the Great Library offers 3 of the 5 set-aside tokens. There is also `run_batch.sh` for N-game bot-vs-bot batches.

### JrCrespi/AI-7-Wonders-Duel (Python, no licence, created 2026-10-09)

- **Shape *(README)*:** a pure-Python engine with `get_legal_actions()`, JSON (de)serialisation of the whole state, and seeded reproducibility. The AI is an evaluation function with *named components* plus alpha-beta over determinizations of hidden cards. Weights are tuned by self-play, and the "Coach" explains moves by the win probability they lose. It has 162 pytest tests.
- **Results *(README)*:** Easy vs random 40–0. Tuned weights vs hand weights 80.7% over 800 games (Elo +248). **Hard vs Easy only 56.2% over 240 games (Elo +44, 95% CI 0 to +89).** Deeper search barely helps when it sits on a weak evaluation.
- **Speed (I measured this):** uniform-random self-play through its public API ran **66.8 games/s** with 69.5 decisions/game (Python 3.14, same laptop). That is about 1000× slower than indjev99. A tuned Python engine would do better, but this is a realistic "first write" number.
- **Rule interpretations it lists:** Lighthouse counts itself; Shipowners uses a single count; play-again on the last card of an Age is lost; a science pair always forces a token pick; **all face-down cards share one back, including guilds**. That last one is wrong for the physical game, where guilds have a distinct back. Our Observation must keep "face-down Age III vs face-down guild". kilianmandon's card-group one-hot does keep it.
- **Data:** `data/cards.json` (73), `wonders.json` (12), `tokens.json` (10). Typed effects, Portuguese display names, and the README says it was "checked against a physical copy". No licence, so it is a reference only.

### MC-freshman/7-Wonders-Duel-game (TypeScript, no licence)

- **Coverage *(README)*:** base, **Pantheon**, **Agora**, the official Solo variants, and online play. The rules engine is pure functions (`src/core/engine.ts`, ~2.6k lines). `visibility.ts` strips seed, RNG state, face-down cards, removed cards and the Great Library pool before sending state to clients. The README says that with the seed and the full log you could reconstruct hidden cards, so the seed is never sent. That is a useful Observation pitfall.
- **AI *(code, `src/ai/index.ts`)*:** a hand-written evaluation (score, military, science threat, resource self-sufficiency, coins, chain potential, wonder feasibility). Search is alpha-beta at depth 1, 2 or 3, using 1, 1 or 2 determinization samples. Lower levels add noise and blunders.
- **Strength:** `docs/ai-baseline/*.json`. Hard beats random 95.8% and beats medium 79.2%, over only 24 games (base). Agora runs used 8 games. These numbers are too small to rely on.
- **Agora caveat *(README)*:** "Agora 部分实体卡面数据仍采用近似" (some Agora card data is still approximate). The details are in `docs/rules/AGORA_DATA.md`. Its validation scripts are good ideas to copy as test categories: layout topology, card counts, chain closure, science-symbol distribution, a random-play soak with invariants, and replay-equals-live.

### koenheltzel/bga-sevenwondersduel (PHP/JS, BGA licence)

- This is the code behind BGA's 7 Wonders Duel, released by its author for maintenance *(README)*. Card, wonder, token, divinity, conspiracy and decree definitions are in `modules/php/Material.php` (~1k lines). There are full modules for **Pantheon** (Divinities, Offering and Mythology tokens) and **Agora** (Senate, Conspiracies, Decrees).
- `PaymentPlan.php` is a battle-tested cheapest-payment algorithm. It enumerates all combinations of "choice" resources (yellow cards and wonders). With Masonry or Architecture, it also enumerates which 2 resources to waive, then takes the minimum coin cost. Our Engine has to solve the same problem.
- **Licence:** `LICENCE_BGA` lets the code be reused "for other games on the BGA studio platform". It also says developers "can also publish the code … under any licence", but no OSS licence is attached here. **Treat it as a rules reference** (especially for Pantheon and Agora), not as copyable code. Names and images belong to Repos Production.

### AlexisDeseure/7-Wonders-Duel (C++/Qt, BSD-3)

- `data/cards.json` has buildings with chain send/receive and typed effects, plus wonders and tokens. It uses **French** names. `data/buildings_layout.json` gives the age pyramids as rows of nodes with `left_son` and `right_son` and a face-up flag. `data/game_parameters.json` has the military track (2/5/10 VP, 2/5 looting), the 7-wonder cap and other constants. It is permissively licensed and so can be reused with attribution. The README promises AI opponents, but the code only picks at random.

## Cross-cutting lessons for this project

1. **Model reveals as chance, not as hidden state.** In the base game, every face-down or removed card is unknown to both players. The one exception is the Great Library: the builder privately sees 3 of the 5 set-aside tokens. ZeusAI (afterstates), indjev99 (`ACTOR_GAME` reveal actions plus a `Revealer`) and marcotama (draw from the distribution at run time) all do this. It gives three things:
   - honest Agents for free, since nothing unrevealed sits in the state;
   - plain MCTS or AlphaZero with chance nodes instead of ISMCTS;
   - an Adapter contract that is just "Moves plus reveal events", which fits the Move Log.

   This feeds tickets 05, 06 and 09/10.
2. **Bound chance branching.** ZeusAI caps each afterstate at 11 children (the most a single reveal can produce), widening progressively in evaluation. indjev99's chance nodes use least-visited selection instead.
3. **Pre-dealt hidden cards plus a copied state means the search cheats.** kilianmandon is the cautionary example. MC-freshman and JrCrespi avoid it by re-sampling (determinizing) unknown cards before searching.
4. **Science is a sparse-reward trap for RL.** ZeusAI needed ~100k games to find science wins and seeded training with green- and red-biased rule-based policies. Our Agent Ladder's heuristic rungs can play that seeding role.
5. **Search on a weak evaluation plateaus quickly.** JrCrespi's depth-3 alpha-beta is only +44 Elo over depth-1. This argues for MCTS with rollouts or a learned value over deeper alpha-beta on hand weights.
6. **Engine speed is decisive, and fixed arrays win.** Measured: 66k random games/s for a C++ fixed-array engine, ~1k playouts/s for a Kotlin engine with immutable collections (from code comment), and ~67 games/s for pure Python. The C# Engine should copy indjev99's approach:
   - flat value-type state that is cheap to `Clone()`;
   - a validated mode and a fast mode;
   - small integer Moves.
7. **Encoding precedents for ticket 08:**
   - *Flat:* kilianmandon uses a 1,966-float observation and a 386-way masked action head. It shows the size of the action space: 20 slots × {build, discard, 4 wonders} plus pick and destroy sub-decisions.
   - *Set or Transformer:* ZeusAI embeds components and their positions, with no hand-coded effects, and uses about 92M parameters. That is far beyond a laptop and needs the Windows GPU box.
   - A small MLP over a flat encoding is the realistic first neural rung.
8. **Rule-interpretation hot spots to settle in ticket 01 and pin with tests.** These are places where the projects chose explicitly or disagree:
   - play-again gained on the last card of an Age;
   - whether taking a token on a science pair is optional;
   - Lighthouse/Arena-style self-counting, and Shipowners counting brown and grey together;
   - Economy only catching trade spending;
   - the tie-break (most civilian/blue VP, else shared);
   - the 7-wonder cap (8th wonder discarded);
   - looting floors at 0;
   - guild card backs are distinguishable;
   - wonder draft order 1-2-2-1 then 2-1-1-2.

   karthik's `RulebookTest.kt` and JrCrespi's README list are ready-made test-case seeds.
9. **Reference statistics for sanity-checking our Self-Play:**
   - ZeusAI: first player 66.8% in AI self-play; victory types roughly 62% civilian, 21% science, 17% military; draws about 0.1%.
   - BGA humans ([7WD in Numbers](https://forum.boardgamearena.com/viewtopic.php?p=112104), ~5k top-500 games, 2022): first player about 55%; Standard victory mix 58.0% civilian, 25.6% science, 16.4% military.

## What we can reuse

| Want | Best source | Licence | How |
|---|---|---|---|
| Base card/wonder JSON | kilianmandon `card_data/` | MIT | Copy with attribution, then cross-check against the rulebook (ticket 01) |
| Base card JSON (alt.) | AlexisDeseure `data/*.json` | BSD-3 | Copy with attribution; French names need mapping |
| Age layouts (who covers whom, face-up/down) | kilianmandon `age_masks.json` + `age_orders.json`; AlexisDeseure `buildings_layout.json` | MIT / BSD-3 | Copy |
| Pantheon/Agora rules data | koenheltzel `Material.php` (+ MC-freshman docs for cross-checking) | BGA licence / none | Read-only reference |
| Cheapest-payment algorithm | koenheltzel `PaymentPlan.php`; MC-freshman `resources.ts` | ref only | Reimplement |
| Engine architecture and chance-as-actor | indjev99 | MIT | Port ideas or code to C# |
| MCTS with heuristic rollouts (an Agent Ladder rung) | indjev99 `agents/` | MIT | Port |
| AlphaZero recipe | ZeusAI paper; kilianmandon `alpha_zero/` for a minimal loop | paper / MIT | Follow |
| Rule test cases | karthik `RulebookTest.kt`; JrCrespi test names; MC-freshman check scripts | none | Re-express as our own tests (ideas aren't copyrighted) |

No public dataset of human 7WD games exists. The BGA stats thread shares only charts, not raw data.

## Open uncertainties

- **ZeusAI internals** are known only from a 4-page short paper: no hardware, wall-clock time, engine language, exact action encoding or chance-sampling details. The "submitted" full paper may answer these. Re-check arXiv later.
- **Rule correctness of the open engines** is unverified. I read the code for approach, not for rule bugs. The card-data JSON files must be cross-checked against the official rulebook before use.
- **The indjev99 benchmark** measures uniform and heuristic random play only, with no MCTS-level numbers. It also ran on the laptop CPU, not the Windows box.
- **BoardGameGeek** returned HTTP 403 to automated fetches. I found BGG threads only through search snippets (for example, a ZeusAI paper thread, `thread/3312377`) and could not read them. There may be other BGG write-ups I missed.
- **The official app's AI** (Repos/Asmodee) is closed, and I found no public description of its approach or strength.
- **Licence status of the BGA code** for non-BGA reuse is ambiguous. I treated it as reference only.

## Sources

- Paolini et al., "Learning to Play 7 Wonders Duel Without Human Supervision", IEEE CoG 2024, [arXiv:2406.00741](https://arxiv.org/abs/2406.00741), DOI 10.1109/CoG60054.2024.10645555.
- ZeusAI [site](https://sites.google.com/view/zeus-ai) and [FAQ](https://sites.google.com/view/zeus-ai/faq). BGA forum [ZeusAI thread](https://forum.boardgamearena.com/viewtopic.php?t=36989).
- Le007n, ["7WD in Numbers"](https://forum.boardgamearena.com/viewtopic.php?p=112104), BGA forum, 2022.
- Repositories (code read at the commits listed in the catalogue): [indjev99](https://github.com/indjev99/7-Wonders-Duel-AI), [kilianmandon](https://github.com/kilianmandon/seven-wonders-rl), [marcotama](https://github.com/marcotama/seven_wonders_duel), [karthik24iyer](https://github.com/karthik24iyer/seven-wonders-duel-simulation), [JrCrespi](https://github.com/JrCrespi/AI-7-Wonders-Duel), [MC-freshman](https://github.com/MC-freshman/7-Wonders-Duel-game), [koenheltzel](https://github.com/koenheltzel/bga-sevenwondersduel), [AlexisDeseure](https://github.com/AlexisDeseure/7-Wonders-Duel), [7wd-io/engine](https://github.com/7wd-io/engine), [Ridzhi/7wd](https://github.com/Ridzhi/7wd), [ygoduelistharry](https://github.com/ygoduelistharry/7-wonders-duel).
- Related game, not Duel: Robilliard, Fonlupt, Teytaud, ["Monte-Carlo Tree Search for the Game of 7 Wonders"](https://link.springer.com/doi/10.1007/978-3-319-14923-3_5) (CGW 2014). Not read in detail.
