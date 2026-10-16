# 02: Pantheon setup, Age I tokens and Grand Temples

**What to build:** Setup as Reveals (P-A3): `reveal myth-tokens`, the 14–16 Wonder pool and 13 Progress pool, token taking on flip, Mythology decks (known tops over an unknown remainder), the private `reveal P<n> divinities` and the `divinity <divinity> <space>` decision; Age II setup (Gate, `reveal pantheon`, `reveal offerings`, `reveal enki`); Age III with 3 Grand Temples and `temple` backs; Grand Temple building (free with the token, R9) and scoring; the 3 new Progress tokens. Log writer/parser/replayer for all of it.

**Blocked by:** 01

**Status:** open

Spec: `docs/spec-pantheon.md` (P-A2, P-A3, P-A4), on top of `docs/spec-v1.md`.

- [ ] Rule tests: token before play again; two tokens from one flip queue two decisions; the returned card is drawn first by a later token of that Mythology; Gate fills the empty space; Temple free with token, not a chain; 5/12/21 VP; Mysticism, Poliorcetics, Engineering (incl. the Urbanism FAQ)
- [ ] Random games reach the end of Age III with Pantheon on (activation comes in 03)
- [ ] Replaying any log reproduces the state
- [ ] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes
