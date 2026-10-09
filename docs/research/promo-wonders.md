# 7 Wonders Duel: promo Wonders (Statue of Liberty, The Messe) and other base-game promos

Researched 2026-10-09 for ticket `15-promo-wonder-rules`. This doc extends [rules-reference.md](rules-reference.md), which covers the base game only. Section numbers like "§7" refer to that doc.

## Sources

| Key | Source | Trust |
|---|---|---|
| **[SoL16]** | Official Statue of Liberty rule sheet, © Repos Production 2016, 1 p.: <https://cdn.svc.asmodee.net/production-rprod/storage/downloads/games/7wonders-duel/en/7w-duel-libertystatut-rules-en-1614249972HFy9u.pdf> | Primary. Text extracted with `pdftotext`. |
| **[SoL20]** | Official Statue of Liberty rule sheet, reprint, © Repos Production 2020, 1 p.: <https://cdn.svc.asmodee.net/production-rprod/storage/games/7-wonders-duel/goodies/7du-goodies-rules-statue-of-liberty-wonder-card-en-compressed-1684146187ZzRVF.pdf> | Primary. Its wording is identical to [SoL16] except "Gain 5 victory points". |
| **[BGG-M]** | BoardGameGeek item 186069, "7 Wonders Duel: The Messe Promo Card" (2015, Repos Production / Asterion Press). BGG returns 403 to scripts, so I read its description through two mirrors that quote it word for word: <https://boardgamematcher.com/game/7-wonders-duel-the-messe-promo-card> and <https://boardmatch.app/en/game/186069>. | Secondary, but it is the only full English rules text I found for the Messe. **I found no official Messe rule sheet** (see §6). |
| **[BGG-S]** | BoardGameGeek items 196339 (Statue of Liberty, 2016), 228690 (Stonehenge alternate-art promo, 2017) and 255878 (Sagrada Familia alternate-art promo, 2018). Read through search-result summaries and retailer mirrors, because BGG blocks scripted access. | Secondary. |
| **[7WD]** | `7wd-io/engine` (Go), commit `2874c010`: `registry.go`, `effects.go`, `moves.go`, `deck.go`, `state.go`, `state_test.go` | Secondary. An independent open-source engine, already used as [7WD] in rules-reference.md. |
| **[TTS]** | The TTS mod "7 Wonders Duel [ULTIMATE ed.]" (Workshop 3368311210, last updated 2024-12-15). I downloaded the save through the Steam Web API and decoded it, as described in [tts-mod-and-api.md](tts-mod-and-api.md) §2.2. I used the card Descriptions, the card face sheet image and the Lua scripts. | Secondary. The card text is a fan copy of the official text. |
| **[IMG]** | The card faces in the mod's sheet `…/ugc/1011563587905737276/1C05D5B4…/` (cells 0 and 1 of a 10×7 sheet). I cropped and read them. | A scan of the printed cards. This is my only source for the costs besides [7WD], because the official sheets print costs as icons. |
| **[BGAF2]** | BGA forum thread "Règles des cartes goodies pour 7 wonders duel", <https://forum.boardgamearena.com/viewtopic.php?t=25352> (2022-2024, French) | Community. Used only to confirm who picks the opponent's card. |

**Cross-check result.**
- **Costs:** [7WD] and the printed cards [IMG] agree for both Wonders.
- **VP:** the official sheets, [7WD], [TTS] (Description and score table) and [BGG] all agree.
- **Statue of Liberty effect:** [SoL16], [SoL20] and [TTS] agree word for word. **[7WD] differs:** it never puts the third card in the discard pile (§3.4).
- **Messe effect:** [BGG-M], [7WD] and the card icon [IMG] agree. [TTS]'s text leaves out "even if the card is still covered", but it does not contradict it.

---

## 1. How promo Wonders enter the game

- The Statue of Liberty "is added to the Wonders of the 7 Wonders DUEL game" [SoL16]. The Messe is likewise described as "a Wonder for the 7 Wonders: Duel game" [BGG-M]. Neither source gives any special setup step.
- **Model:** each promo Wonder simply joins the Wonder pool before the draft is shuffled. With both promos the pool has **14 Wonders**. The draft is unchanged: 2 rounds × 4 revealed, 8 drafted, and now **6 unused** (§1 step 4).
- [7WD] does exactly this. `Options.PromoWonders` (`game.go`) keeps or removes `Messe` and `StatueOfLiberty` from the 14 ids before `shuffleWonders`, and then takes the first 8 (`NewMovePrepare`, `moves.go`). It is all-or-nothing: you cannot enable one promo without the other.
- [TTS] keeps both promo cards loose on the table, outside the 12-card base Wonder deck `ce2da4`. Players add them to the draft by hand.
- **Recommendation:** add a ruleset flag per promo Wonder (default off) rather than a single switch. The Engine's draft code then shuffles whatever pool the flags produce.
- Nothing in any source changes the "7 Wonders, not one more!" rule (§7). It still applies, and still counts promo Wonders.

## 2. The Messe

| Wonder | Cost | Effect | VP |
|---|---|---|---|
| The Messe | G P W CC | Take one card from the **top row** of the current Age structure, **even if it is covered**, and construct it for free. | 2 |

**Text.** [BGG-M]: when you build the Messe you "take one card from the topmost row of the drafting structure (the line lying directly on the table), even if the card is still covered, and build it in their own city for free - only paying the price of the wonder." [TTS] Description: "Take one card from the topmost row of the drafting structure, and build it for free." The card's icon [IMG] shows a top-row card, partly hidden by the cards below it, crossed out.

**Release.** It was an Essen 2015 promo (BGG 186069), later sold on the Repos webshop (BGG thread 1462996, "7 Wonders Duel - Messe available on webshop").

**Rules for the Engine:**
1. **"Top row"** means the row **farthest from the players**: rows numbered 1 in §4, which are slots 0-1 in Age I, 0-5 in Age II and 0-1 in Age III. This row lies directly on the table, and every other row overlaps it.
   - [7WD] `deck.topLineCards()` takes the cards of the first line of each Age's layout string, and those layouts start with the 2-card row in Age I and the 6-card row in Age II.
   - The top row is face up in every Age (§4). So every candidate is a **known** card, and the Messe needs **no Reveal**.
2. **Accessibility is ignored.** Any card still in the top row can be taken. [7WD] offers every top-row card still present and never checks whether it is covered.
3. **Removing a top-row card exposes nothing**, because no card is covered by a top-row card. No card flips over. The cards that covered the taken card stay where they are, and they are now covering nothing on that side.
4. **"For free"** means no resources and no coins. Like the Mausoleum, this is not a chain build, so **Urbanism's +4 does not trigger** (§12.4). This is an inference: no source rules on it directly. [7WD] applies only the card's own effects (`R.Cards[...].Mutate`), which gives the same result.
5. **All the card's normal consequences apply**, in the builder's city:
   - shields, including Strategy's +1 on a red card;
   - one-time coins from a yellow card or a Guild;
   - a science pair, which opens a Progress-token pick;
   - an immediate military or science victory.
   - A Guild in the Age III top row is allowed.
6. **Empty top row:** the effect does nothing, and [7WD] skips it. The official text does not cover this.
7. **No "play again"** on the Messe itself. Theology still grants play again as usual, after the Messe choice is resolved.

## 3. The Statue of Liberty

| Wonder | Cost | Effect | VP |
|---|---|---|---|
| Statue of Liberty | G P C S W | Take the 3 cards of the current Age that were removed at setup. **Construct one for free, discard one, and your opponent constructs the third for free.** | 5 |

**Text.** [SoL16]: "Take the three cards of the current Age which were returned to the box during the game's setup. Choose and construct one for free, place one in the discard pile, and your opponent constructs the third for free." [SoL20] has the same text, with "Gain 5 victory points." [TTS] has the [SoL16] text word for word.

**Release.** It was a 2016 event promo (BGG 196339; retailers list "v1"/"v2" and "artwork A/B" printings). The 2016 and 2020 sheets have the same rules.

### 3.1 Rules for the Engine

1. **The three cards** are the 3 cards removed unseen from the **current Age's** deck at setup (§1 step 5).
   - **Age III:** these are the 3 removed Age III cards, **not** Guilds, even though the 4 unused Guilds also went back to the box. The text says "the three cards". [7WD] (`deck.returnedCards()` builds its list from non-Guild Age cards) and [TTS] (it deals from the removed Age III pile, which never contains Guilds) both read it this way.
2. **The builder makes every choice:** which card they build, which goes to the discard pile, and therefore which card the opponent must build.
   - The opponent gets no choice.
   - [BGAF2] confirms the builder gives the opponent "the one of their choice".
   - [7WD]'s Move carries both `pick` and `give`.
3. **"Construct for free" applies to both builds**: no resources, no coins and no Urbanism bonus (same reasoning as the Messe, §2.4). Each card takes effect in **its owner's** city. For example, the opponent's red card moves the pawn toward **you**, the opponent's Tavern gives **them** 4 coins, and the opponent's Strategy token adds +1 shield to their red card.
4. **The discarded card goes to the discard pile.** It becomes public and can later be built with the Mausoleum.
5. **All three cards become public.** Two are in cities and one is in the discard pile.
6. **Order of resolution.** The sheet gives the order as "construct one … place one in the discard pile … your opponent constructs the third". The Engine should resolve **the builder's card fully first, then the opponent's card**.
   - [7WD] does this (`PickReturnedCardsMove.Mutate`).
   - Its comment in `state.go` says: "we can handle two units for both players at the same time (Statue of Liberty) … whoever called first wins".
   - So if the builder's card gives the builder supremacy, the builder wins before the opponent's card is placed.
7. **Science pairs can give two token picks, one for each player.** First the builder picks, then the **opponent** picks, during the builder's turn. [7WD] queues them FIFO (`pushDialog` / `pullDialog`). If the board has no tokens left, a pick is skipped (§6).
8. **The opponent can win on your turn.** The forced card can give the opponent military supremacy (shields) or scientific supremacy (a 6th symbol). Since the builder chooses the card, a sensible builder avoids this. The Engine must still handle it.
9. **No "play again"** on the Statue of Liberty itself. Theology applies as usual, after all the sub-decisions.
10. **It is always possible.** You need an Age card to build a Wonder, so the current Age always has its 3 removed cards when the effect resolves. The effect never needs to be skipped.

### 3.2 Chance and hidden information (ADR 0002)

- Under lazy chance resolution, the removed cards have **no identity** until they are revealed. The Statue of Liberty is the **only** base-game effect that reveals them.
- Building it needs a **Reveal of 3 cards** drawn from the current Age's unseen non-Guild pool. That pool holds the removed cards plus any face-down slots not yet flipped, and these are exchangeable, so a uniform draw is correct.
  - In Self-Play, the Chance Source draws them at random.
  - In Advisor mode, the human or TTS reports them.
- After the Reveal, that Age's unseen pool is 3 cards smaller.
- **Effect on search and the Agent:** before the Statue of Liberty is built, its value depends on cards nobody has seen. Search must treat building it as a chance node over C(n,3) outcomes, which is the same pattern as the Great Library draw.

### 3.3 What the TTS "Activate The Statue Of Liberty" button does

The script on object `cae875` ("Activate The Statue Of Liberty Button") does very little:

1. It calls `Global.IsCardOnTheTable("Statue Of Liberty")`. That only checks whether a card with that name is anywhere in the left or right **player zone**. It does **not** check that the Wonder was **built**, so a drafted but unbuilt Statue passes the check.
2. It reads `current_age` and calls `getObj("ageNDeck").deal(3, playerClicked)`. This deals 3 cards from the object that `guidLookup` names as the current Age deck into the **hand of whoever clicked**.
3. That is all. Building, discarding and giving the cards are done **by hand**. Nothing is enforced or recorded.
4. Otherwise it prints "The Statue Of Liberty is not among the player wonders."

**Where the removed cards are and whether the button finds them.**
- Every Age setup button moves the 3 removed cards to a pile at `(deck.x, 2, -25.25)` before it deals the 20-card structure.
- **Age III only:** after dealing, the setup script runs a `Physics.cast` to find that pile and re-points `guidLookup.age3Deck` at it. The comment says this is "for Statue Of Liberty". However, the cast's `origin = age3DiscardsPos` is a variable defined **nowhere** in the save, so it is `nil`.
- **Ages I and II:** nothing re-points `age1Deck` / `age2Deck`. In classic mode the setup deals all 20 remaining cards, so the original deck object (`689066` / `5fd30c`) is emptied. The removed pile is a different object with a new GUID.
- **So whether the button actually deals the right 3 cards in any Age is unverified, and doubtful from the code.** This needs a live test in TTS (ticket 03 / smoke tests). Either way, the Adapter should **not** rely on the button. It should read the 3 cards wherever they appear: in a hand, in the player zones, or in the discard area.

[TTS]'s score table credits `"Statue Of Liberty"` with 5 VP and `"The Messe"` with 2 VP (`Score Game Button` and `Take Turn` scripts). The Messe has no script at all.

### 3.4 How [7WD] models both Wonders

| | Messe | Statue of Liberty |
|---|---|---|
| Id | `Messe` (13) | `StatueOfLiberty` (14) |
| Cost (`registry.go`) | glass 1, papyrus 1, wood 1, clay 2 | glass 1, papyrus 1, clay 1, stone 1, wood 1 |
| Effects | `newEffectPickTopLineCard()`, `newEffectPoints(2)` | `newEffectPickReturnedCards()`, `newEffectPoints(5)` |
| Phase (`phase.go`) | `phasePickTopLineCard` | `phasePickReturnedCards` |
| Move (`moves.go`) | `MovePickTopLineCard {card}` (id 10) | `MovePickReturnedCards {pick, give}` (id 12) |
| Options offered | `deck.topLineCards()`: every card still in the layout's first line, whether or not it is covered | `deck.returnedCards()`: the cards of this Age **not** in the structure's 20, Guilds excluded |
| Skip | if the top line is empty | never |

**[7WD] deviation (a bug against the official text).**
- `PickReturnedCardsMove.Mutate` builds `pick` for the builder and `give` for the opponent, but it **never adds the third card to the discard pile**. Compare `DiscardCardMove`, which calls `CardItems.addDiscarded`.
- So in [7WD] the third card vanishes. A later Mausoleum cannot build it, and it is missing from the public discard pile.
- Follow [SoL16] here, not [7WD].

[7WD]'s `state_test.go` plays a full game with both promos. It builds the Messe in Age II and takes `Dispensary` from the top line, and it builds the Statue of Liberty in Age III with `pick Study, give Circus`.

## 4. Card data

### 4.1 kilianmandon `card_data/wonders.json` shape (prior-art.md)

These entries use the same keys as the 12 existing entries. Each `action` value is **new**: no existing entry has it, and an Engine reading this file must add a handler for it.

```json
[
    {
        "name": "messe",
        "cost_resources": ["glass", "papyrus", "wood", "clay", "clay"],
        "resources_provided": [],
        "military_points_provided": 0,
        "victory_points_provided": 2,
        "money_provided": 0,
        "money_opponent_lost": 0,
        "action": "build_top_row",
        "second_turn": false
    },
    {
        "name": "statue_of_liberty",
        "cost_resources": ["glass", "papyrus", "clay", "stone", "wood"],
        "resources_provided": [],
        "military_points_provided": 0,
        "victory_points_provided": 5,
        "money_provided": 0,
        "money_opponent_lost": 0,
        "action": "returned_cards_keep_discard_give",
        "second_turn": false
    }
]
```

### 4.2 Table row, in the style of rules-reference.md §7

| Wonder | Cost | Effect | VP |
|---|---|---|---|
| The Messe | G P W CC | Take one card from the **top row** of the current structure (even if covered) and construct it for free | 2 |
| Statue of Liberty | G P C S W | Reveal the current Age's 3 setup-removed cards. Construct one for free, discard one, and the opponent constructs the third for free (builder chooses all) | 5 |

Cost check:
- [IMG] Messe cost icons, top to bottom: glass, papyrus, wood, clay, clay.
- [IMG] Statue of Liberty cost icons: glass, papyrus, clay, stone, wood.
- Both match [7WD] `registry.go`.

## 5. Impact on the Game State and Move model (ticket 06)

New **Pending Decisions** and **Moves**. Proposed syntax follows the Move Log example in ticket 06.

| Pending Decision | Move | Options | Notes |
|---|---|---|---|
| **Messe: take a top-row card** | `messe <card>` | ≤2 (Age I/III) or ≤6 (Age II) | No Reveal is needed because the top row is face up. It is skipped if the top row is empty. |
| **Statue of Liberty: keep / give** | `liberty <keep> <give>` | exactly 6 (3 × 2) | The remaining card goes to the discard pile. This is one Move, matching [7WD]. It could be split into `liberty-keep` then `liberty-give` if the Encoding prefers smaller heads. |

New **Reveal**:
- `reveal returned age<N> <a> <b> <c>`. It is logged when the Statue of Liberty is built, before the keep/give decision.
- It is the only base-game Reveal of setup-removed cards.

**Changes to existing machinery**

1. **The player who decides is not always the player whose turn it is.**
   - After `liberty`, the **opponent** may owe a `token` pick, and possibly a Great-Library-style chain of follow-ups, in the middle of the builder's turn.
   - The Game State needs "player to decide" separate from "player whose turn it is". Otherwise the opponent's pick becomes ambiguous.
   - After the queue empties, the turn returns to the builder (play again) or passes as normal.
   - [7WD] solves this with `FallbackTurn` plus a FIFO dialog queue.
2. **A queue of Pending Decisions.**
   - One Move can create two follow-ups: the builder's token pick, then the opponent's token pick.
   - Ticket 06 already implies a queue, because Theology plus a science pair from a Wonder needs one. The Statue of Liberty is the first case where entries belong to **different** players.
3. **Immediate victory for the non-active player.** The opponent's forced build can give **them** military or science supremacy on your turn. The Result logic must not assume that the active player is the winner.
4. **Ruleset header flags.** For example: `rules base start-choice=new promo=messe,liberty`. The Wonder pool depends on these flags.
5. **Encoding (ticket 08).**
   - Two more Wonder ids.
   - Action heads for `messe` (up to 6 slot choices, which can reuse the 20-slot head) and `liberty` (6 options).
   - An Observation of the 3 revealed cards while the keep/give decision is pending.
   - The face-down/unseen pool counts must shrink after the Reveal.

## 6. Other official promos for the base game

| Promo | Year | Type | Rules effect |
|---|---|---|---|
| **The Messe** | 2015 | New Wonder | §2 |
| **Statue of Liberty** | 2016 (reprinted 2020) | New Wonder | §3 |
| **Stonehenge** | 2017 | **Alternate art** of The Pyramids (P SSS, 9 VP) [BGG-S, BGG 228690] | None. A Repos reply quoted on BGG (thread 1804672, "same to pyramids?") says it "must replace the pyramids", so the two are never both in the pool. |
| **Sagrada Familia** | 2018 | **Alternate art** of The Great Library (P G WWW, 4 VP), sold through the BGG Geek Store [BGG-S, BGG 255878] | None. By analogy with Stonehenge it replaces the Great Library (an inference; I found no ruling). |

- **Engine:** Stonehenge and Sagrada Familia need only display-name aliases for `pyramids` and `great-library`. They are not new Wonders.
- **Adapter:** the TTS and Advisor-Window Adapters must map these names to the same ids.

**Promos for the expansions (out of scope here):**
- **Tu Quoque Fili** for Agora.
- Metal Minerva and Conflict pawns and metal coins. These are accessories with no rules.

**The mod's "3 official promos".**
- The [TTS] save contains only **two** promo Wonders: `Statue Of Liberty` and `The Messe`.
- I found no Stonehenge, Sagrada Familia or Tu Quoque Fili object, and no object named or described as a promo.
- The third promo the Workshop page counts is **unidentified**. It may be an expansion promo mixed into the Pantheon or Agora decks under its normal name.
- `Abu Simbel` and `Rome` (deck `a2608b`) belong to the **fan-made Leaders** expansion. They are not official.

## 7. Source disagreements

1. **[7WD] drops the Statue of Liberty's discarded card** instead of putting it in the discard pile (§3.4). The official text [SoL16]/[SoL20] wins.
2. **[TTS]'s Messe text omits "even if the card is still covered".** [BGG-M], [7WD] and the card icon all say covered cards are allowed. Treat the TTS text as a paraphrase that shortened the original.
3. **[BGAF2]'s fan-made reference card** shortened the Statue of Liberty so much that it lost the "give the opponent one" part. Its author later corrected it. The corrected reading matches the official sheet.

## 8. Open uncertainties

- **No official Messe rule sheet or FAQ entry.** Repos's FAQ (<https://www.rprod.com/en/7-wonders-duel/faq>) mentions neither promo. The Messe's full text comes from the BGG description, via mirrors, plus the card icon.
- **BGG forums were not read.** Scripted access is blocked, and the browser route needed a user choice I could not ask for. Rulings that may exist there are unchecked. Open questions:
  - whether Urbanism triggers on a Messe or Statue of Liberty build (inferred: no);
  - the Age III Guild question (§3.1.1);
  - the order of the two token picks;
  - whether Sagrada Familia replaces the Great Library.
- **The TTS button's behaviour in Ages I-III** needs a live test (§3.3).
- **The mod's third promo** is unidentified (§6).
- **Empty top row for the Messe:** no source covers it. Skipping is the only sensible reading.
