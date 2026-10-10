# 15: Board-reading features and the rule-based player

**What to build:** The shared derived-features module and the Heuristic with presets science, military, civilian and adaptive balanced, valuing promo Wonders.

**Blocked by:** 13, 14

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Adaptive balanced passes Random
- [x] Each style preset's victory-type mix skews as intended

## Notes

- Derived features: `Engine/Features/DerivedFeatures.cs` (static, allocation-free, reads a `GameState` directly so search can use it on `CopyFrom` states): `Production`, `ChoiceProducers` (cards + built Wonders), `TradingPrice`, `ScienceSymbols`, `SciencePairs`, `IsNewSymbol`, `Vp`, `DistanceToCapital(s, p)` (9 + p's military lead; 0 = p lost by military), `OpenChains` (chain targets not built, discarded or under a Wonder). `Write(s, me, Span<int>)` fills the 32-number derived block (me, opp, then both capital distances) as raw ints in the order of `Names`; scaling is the Encoding's job (ticket 18). `Scoring.Total(s, p)` is the new non-allocating VP total (`Score` now shares its `Fill`); results, determinism hashes and frozen C++ games are unchanged.
- Heuristic: `Agents/HeuristicAgent.cs`. Spec `heuristic` (= balanced) or `heuristic:<preset>[,weight=value...]`, presets `science`, `military`, `civilian`, `balanced`; weights `points, military, science, economy, coins, denial, adaptive` (overrides are for tuning). For each Move it copies the Observation's state, applies the Move, follows its own immediate follow-up choices (token, destroy, Mausoleum, Messe, Liberty keep/give; best of each, averaged over 3 Chance-Source draws when a Reveal comes first) and evaluates (VP-equivalents, me minus opponent; a win = 1000). A card take also scores what it leaves the opponent: their best accessible known card (`CardValue`, which flags a card that wins them the game), and a play-again bonus. Draft: fixed per-Wonder values; promo Wonders = VP + best card grab (Messe +4, Liberty +3.5 at draft; at build time the follow-up search values the actual grab). `ChooseStarter`: always `start me`.
- Adaptive balanced (`HeuristicAgent.Adapt`) reweights each decision: science up with distinct symbols (opponent's ×0.45, own ×0.3 above 2), military up as the pawn nears either capital, both supremacy weights up when behind on VP, points up when ahead.
- Tuning finding: production/economy was worth ~6-8× my first guess (an economy-weight sweep moved head-to-head scores from 12% to 65%), hence `ProductionScale = 8`. Presets: science (points 0.6, military 0.3, science 4), military (0.8, 2.6, 0.5), civilian (1.4, 0.5, 0.5), balanced (0.8, 0.7, 1, adaptive).
- Measured (Windows desktop, 400 games each, base): balanced vs Random 400-0 (Elo +1161, the cap of a 400-0 Match; CI low 99.0%), with both promos 399-1. Victory mix vs Random: science preset 288 civ / 43 mil / 66 sci; military 121 / 279 / 0; civilian 332 / 68 / 0; balanced 287 / 113 / 0. Balanced vs the presets head to head: 54.7% science, 50.6% military, 49.1% civilian, so balanced is not clearly the strongest rung-2 player: the Ladder only needs it to pass Random. ~0.09 ms per Move.
- Science wins stay rare unless the preset chases them; this matters for the warm-start trigger (spec A7) and for PIMC/MCTS Matches (ticket 16/17 must pass `heuristic:balanced`).
- Tests: 327 Engine (new `DerivedFeaturesTests`), 53 Agents (new `HeuristicTests`: specs and overrides, balanced passes Random base and with both promos, preset victory-mix skew, always takes an immediate win (incl. through its own follow-up picks) and never changes the Observation, seeded determinism, Messe and Liberty drafted/built in heuristic games).
