# 10: Network surgery and Python for Encoding v2

**What to build:** Python reads the v2 manifest; `sevenwd-train surgery` makes a v2 network from a v1 checkpoint (P-A7); ONNX export stamped v2; C# loads it.

**Blocked by:** 08, 09

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A7), on top of `docs/spec-v1.md`.

- [ ] Exact check: in base-game positions the operated network's value, outcome and v1 policy outputs equal its parent's
- [ ] Overfit test passes on Pantheon positions
- [ ] `7wd match neural:<v2 onnx> random --expansion pantheon` runs
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes
