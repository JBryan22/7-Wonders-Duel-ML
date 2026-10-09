# Base game rules reference

Type: research
Status: resolved
Blocked by:
Research: `docs/research/rules-reference.md` (on main)

## Question

What are the complete, precise rules of 7 Wonders Duel (base game, current English rulebook incl. official errata/FAQ) needed to implement an Engine — setup, the three age card structures (face-up/face-down layout), the full card list (cost, chains, effects), all 12 Wonders, the 10 progress tokens, trading/coin costs, the military track and its tokens, science victory and pairs, the "7 Wonders max" rule, end-of-age and end-of-game conditions, scoring and tiebreaks — and which edge cases do implementers commonly get wrong?

Deliverable: a rules reference doc in the repo (`docs/research/`) with structured card/wonder/token data suitable for transcribing into the Engine, sources cited.

## Answer

Full findings: `docs/research/rules-reference.md`.

- **Sources:** rules wording comes from the official 2015 English rulebook. Card, Wonder and token data come from the BGA implementation and the 7wd-io/engine (Go), diffed by script. All 73 cards and 12 Wonders agree, and they match every number the rulebook prints as text. The publisher's FAQ has no base-game errata; its one note says the Law token counts as a 7th science symbol.
- **Age layouts:** rows of 2-3-4-5-6 (Age I), 6-5-4-3-2 (Age II) and 2-3-4-2-4-3-2 (Age III). The doc spells out the face-up/face-down pattern and which cards cover which.
- **Names:** the rulebook's "Tribunal"/"Courthouse" are "Courthouse"/"Pretorium" in the engines, and "Traders" vs "Merchants Guild" also differs. **The Engine should use stable IDs, not names.**
- **Printing difference:** if military is tied at the end of an Age, the 2015 rulebook has the player who took the last card start. Newer printings and BGA let that player *choose* who starts. Recommendation: use the newer rule, with a config flag.
- **Visible card backs:** Age III Guild cards have a distinct purple back, so a face-down Guild can be told apart from other face-down cards. The Observation must model this.
- **20 common implementation mistakes are listed.** The main ones: work out the cheapest payment (choice cards and the "2 fewer resources" tokens go on the most expensive missing resources), trading prices count only the opponent's brown and grey cards, what reaches the discard pile matters for the Mausoleum, the 8th Wonder is removed once 7 are built, a "play again" is lost at the end of an Age, and the narrow conditions on the Strategy and Urbanism tokens.
- **Rules the book leaves open:** whether the coin bank is limited (treat as unlimited), how the first player is chosen (random), and whether a player may decline a progress token after a science pair. The newer printing's wording rests on a forum thread, not the PDF itself.

**Owner ruling (2026-10-09):** a progress token can't be declined after matching two science symbols. Taking one is compulsory whenever any remain on the board, so the Move model has no "decline" option.
