# 13: TTS Adapter: Pantheon pieces and reconciliation

**What to build:** From the mod's save JSON: the name → slug table and places for every Pantheon piece; snapshot filtering of private zones; the expected-table projection and reconciliation for the new Moves and Reveals; anything that can only be confirmed live goes into `docs/tts-live-checks.md` for ticket 14.

**Blocked by:** 12

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A9), on top of `docs/spec-v1.md`.

- [ ] Every Pantheon piece in the mod maps to a slug or is listed as unknown
- [ ] Reconciliation tests from recorded snapshots for each new Move
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes
