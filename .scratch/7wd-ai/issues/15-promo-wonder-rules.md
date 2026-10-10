# Promo Wonder rules: Statue of Liberty and The Messe

Type: research
Status: resolved
Assignee: Jesse Bryan
Research: `docs/research/promo-wonders.md` (on main; branch `research/promo-wonders`)
Blocked by:

## Question

What exactly do the official promo Wonders **Statue of Liberty** and **The Messe** do in 7 Wonders Duel? For each one, find:
- its cost, its effects and any special timing or choice it creates;
- how it enters setup: does it simply join the Wonder pool for the draft?
- any rulings or FAQ entries.

The research must also settle:
- what the TTS mod's "Activate The Statue Of Liberty" button does;
- how the open-source engine 7wd-io/engine (Go) models both Wonders (see "Prior art: existing 7 Wonders Duel engines and AIs");
- which other official promo content exists for the *base* game. The TTS mod claims "3 official promos".

Deliverable: `docs/research/promo-wonders.md` written in the style of `docs/research/rules-reference.md`, including card data shaped like the existing Wonder JSON, so the Engine can add them as data.

## Answer

Full findings are in `docs/research/promo-wonders.md`.

**Statue of Liberty**
- **Sources:** official Repos rule sheets from 2016 and 2020, with identical wording.
- **Cost:** glass, papyrus, clay, stone and wood. **Worth:** 5 VP. **Play again:** no.
- **Effect:** reveal the 3 cards of the current Age that were removed at setup. The builder builds one for free, discards one, and the opponent builds the third for free. The builder makes all of these choices.
- **Age III:** the cards are the removed Age III cards, not the unused Guilds.
- **Order:** the builder's card resolves first, then the opponent's. Each can trigger a science-pair token pick, and the opponent can even win by supremacy during the builder's turn.

**The Messe**
- **Sources:** the 2015 Essen promo. No official rule sheet was found; the text comes from the BoardGameGeek description, and the card icon and the Go engine agree with it.
- **Cost:** glass, papyrus, wood, clay and clay. **Worth:** 2 VP. **Play again:** no.
- **Effect:** build for free any card from the top row (the row farthest from the players) of the current structure, even if it is covered.
- **Empty top row:** the effect does nothing.

**Setup:** each promo just joins the Wonder pool for the draft (12 + promos, still 8 drafted).

**Source disagreements**
- The Go engine (7wd-io) loses the Statue's discarded card instead of discarding it. That is a bug; follow the rule sheet.
- The TTS mod's Messe text omits "even if covered". Treat it as a shortened copy.

**Model impact**
- **New Pending Decisions and Moves:**
  - Messe: `messe <card>`, with 2 to 6 options;
  - Statue: `liberty <keep> <give>`, with 6 options.
- **New Reveal:** `reveal returned age<N> a b c`. It is the only base-game effect that reveals the cards removed at setup, so search treats it as a chance event.
- **Game State:** the player who must decide can differ from the player whose turn it is, Pending Decisions can belong to either player, and the non-active player can win.
- **Ruleset:** one flag per promo.

**TTS mod**
- The "Activate The Statue Of Liberty" button only deals 3 cards into the clicker's hand.
- Its code looks wrong in all Ages (it points at the emptied Age deck, and in Age III at an undefined variable). It needs a live test, and the Adapter must not rely on it.

**Other base-game promos**
- Stonehenge (art for the Pyramids) and Sagrada Familia (art for the Great Library) change no rules; they only need display-name aliases.

**Unchecked:** rulings on the BoardGameGeek forums, which blocked scripted access.

**Owner confirmation (2026-10-09):** at the owner's table, the Statue works as the sources say: the opponent receives a card, and the Statue's builder chooses which one.
The Messe also works as the sources say: any card in the farthest row can be taken, even while covered. The mod's shorter card text is incomplete, not a different rule.
