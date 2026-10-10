# 02: Card data

**What to build:** The card data JSON (73 cards, 12 base + 2 promo Wonders, 10 Progress tokens, the three Age layouts with face-up/down pattern and coverage) with slugs and attribution, loaded once into flat integer arrays, with data tests.

**Blocked by:** 01

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Card counts per Age and Guilds match the rules reference
- [x] Every chain link resolves to a card
- [x] Science symbol distribution matches the rules reference
- [x] Layout shapes are 2-3-4-5-6 / 6-5-4-3-2 / 2-3-4-2-4-3-2 with the correct face-down slots and coverage
- [x] Display-name aliases (Stonehenge, Sagrada Familia, Tribunal/Courthouse, ...) resolve to slugs

## Notes

- Data lives in `data/base/`: `cards.json`, `wonders.json` (including the 2 promos, flagged `promo`), `tokens.json` and `layouts.json`. The format is our own compact one: costs are strings like `"$3 C"`, and named keys (`guild`, `effect`, `coinsPer`) mark the behaviour the Engine implements. It is transcribed from `docs/research/rules-reference.md`, with source attribution in each file.
- The JSON is embedded in the Engine assembly, so neither OS needs a path lookup. `GameData` loads it once into arrays of immutable definitions, indexed by small integers. If `7wd bench` (ticket 10) shows the definitions are too slow, the hot paths can be flattened further.
- Layout coverage is derived from the rule "column c, row r is covered by columns c±1 in row r+1", and tests check it against the reference's tables. Move Log slot names are `row.position`, both counted from 0 at the top row.
- Name lookup: real names win over aliases. "Courthouse" therefore resolves to the Age II card (BGA naming), and "Tribunal" is its alias. The 2015 rulebook's "Courthouse" (our `pretorium`) has no alias, because it would be ambiguous.
- There are 17 chained cards, each chain symbol printed on exactly one earlier card.
