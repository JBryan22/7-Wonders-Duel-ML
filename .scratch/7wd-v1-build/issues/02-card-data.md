# 02: Card data

**What to build:** The card data JSON (73 cards, 12 base + 2 promo Wonders, 10 Progress tokens, the three Age layouts with face-up/down pattern and coverage) with slugs and attribution, loaded once into flat integer arrays, with data tests.

**Blocked by:** 01

**Status:** ready-for-agent

Spec: `docs/spec-v1.md`.

- [ ] Card counts per Age and Guilds match the rules reference
- [ ] Every chain link resolves to a card
- [ ] Science symbol distribution matches the rules reference
- [ ] Layout shapes are 2-3-4-5-6 / 6-5-4-3-2 / 2-3-4-2-4-3-2 with the correct face-down slots and coverage
- [ ] Display-name aliases (Stonehenge, Sagrada Familia, Tribunal/Courthouse, ...) resolve to slugs
