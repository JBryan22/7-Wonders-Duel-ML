# 21: Gumbel search

**What to build:** The Gumbel root mode and its improved policy target.

**Blocked by:** 20

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Gumbel search returns a valid improved policy
- [x] Same seed gives same choice

## Notes

- Code: `Agents/Neural/NeuralAgent.cs`. **Gumbel is now the default mode** (`NeuralMode.Gumbel`); classic needs `mode=classic`. Spec `neural:<onnx>[,sims=800,m=16,cvisit=50,cscale=0.1,gumbel=1,device=cpu,threads=4]`; `noise`/`temp` are refused unless `mode=classic` (Gumbel noise is the exploration, spec A7). `gumbel=0` switches the root noise off.
- Follows DeepMind mctx `gumbel_muzero_policy` exactly, with mctx's defaults (m = 16 considered Moves, c_visit 50, c_scale 0.1, Gumbel scale 1; `qtransform_completed_by_mix_value` with min-max rescaling, here over the legal Moves only). Root: Gumbel per legal Move from the Agent's seeded stream, then Sequential Halving via mctx's considered-visit table (`NeuralAgent.SequentialHalving`, tested against hand-worked mctx sequences). Interior: deterministic argmax of π′ − n/(1+N). Played Move: best g + logit + σ(q̂) among the most-visited survivors. Same open-loop, chance-at-the-Reveal tree as classic; selection only weighs Moves legal in the current world; each node stores its raw network value (and for whom) for the mixed value.
- `NeuralResult.PolicyTarget` in Gumbel mode = the improved policy softmax(log prior + σ(completed Q)) over the legal Moves in legal-Move order (what ticket 22 stores as the sparse target). Classic still gives visit shares. Stats/line/outcome unchanged.
- Measured (desktop, base, seed 0): random gen-0 256×4 net, Gumbel 800 sims vs Random 67.9 % (63.1–72.3), +130 Elo (classic was 71.8 %); 64 sims Gumbel 57.5 % vs classic 62.0 %; Gumbel vs classic head to head at 800 sims 50.0 % (50 pairs). Speed about the same as classic (292 vs 277 ms/Move at 800 sims, 16 games in parallel).
- **Gumbel with `neural:playout` (uniform prior, one random playout as the value) is weak:** vs `mcts:sims=300,margin=0` 25 % (m=64: 24 %, gumbel=0: 30 %, cscale=1: 26 %, cscale=0.02: 4.5 %), whereas classic gets 45 %. That's expected: Gumbel's min-max-scaled σ trusts noisy values greedily below the root, and Sequential Halving drops root Moves on a few noisy playouts; with an uninformative prior it can't recover. So the classic-vs-MCTS search-bug check stays a classic-mode check. Gumbel's real test is learning (ticket 25 runs classic then Gumbel); the classic-vs-Gumbel pilot (ticket 27) settles which trains better.
- With more than m legal Moves, only m root Moves (top by g + logit) are searched at all: an immediate win outside them is missed by a uniform-prior search. Fine with a trained prior; raise `m` if it matters for the Advisor.
- Tests: Engine 342, Agents 82 (5 new Gumbel tests: Sequential Halving table, improved policy valid and equal to an independent recomputation from the reported stats + visits obey the top-m/most-visited rule, immediate wins found and >90 % of the target on them, same seed → same game/choice/target and a different seed changes the game, playout-valued Gumbel beats Random; the zero-network ≡ uniform-reference test now covers both modes; classic tests now say `mode=classic`). Python 23. Determinism hashes and frozen C++ games untouched.
