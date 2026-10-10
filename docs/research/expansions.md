# 7 Wonders Duel: Pantheon and Agora rules for the Engine's extension points

Researched 2026-10-09 for ticket `17-pantheon-agora-rules-for-extension-points`. This is **not** a full rules write-up. It describes what each expansion adds, in the terms of our model (Game State, Observation, Move, Pending Decision, Reveal, Chance Source, Ruleset, Encoding; see [`GLOSSARY.md`](../../GLOSSARY.md)), so ticket 18 can decide the extension points.

It extends [rules-reference.md](rules-reference.md) (base game) and [promo-wonders.md](promo-wonders.md) (promos). "§N" with no file name refers to a section of this doc. "Slot" numbers use the rules-reference.md convention: numbered from the top row (farthest from the players) down, left to right, starting at 0.

Anything marked *(inferred)* is my reading, not something a source states.

## Sources

| Key | Source | Trust |
|---|---|---|
| **[PRB]** | Official Pantheon rulebook (English, 16 pp.), © Repos Production 2016, revised file on the Asmodee CDN: <https://cdn.svc.asmodee.net/production-rprod/storage/downloads/games/7wonders-duel-pantheon/en/7dpa-rules-en-1624535175WPszB.pdf> | Primary. Text extracted with `pdftotext`. Costs, board costs and the token-position diagrams are pictures only, so I could not read them (no PDF renderer on this machine). |
| **[PRB16]** | Older printing of the same rulebook (© Sombreros Production 2016), mirrored at <https://cdn.1j1ju.com/medias/db/df/24-7-wonders-duel-pantheon-rulebook.pdf> | Primary, older. I diffed it against [PRB]. Only three rule texts differ (§12). |
| **[ARB]** | Official Agora rulebook (English, 20 pp.), © Repos Production 2020, Asmodee CDN: <https://cdn.svc.asmodee.net/production-rprod/storage/downloads/games/7wonders-duel-agora/en/7dag-rules-en-1624535505VDrdL.pdf> | Primary. Same limitation: the Conspiracy, Decree and Senator descriptions on pp. 14-19 are icon + text, **without card names**, and the structure diagrams (p. 8) are pictures. |
| **[BGA]** | `koenheltzel/bga-sevenwondersduel` @ `f023693f` (2026-01-23): `modules/php/Material.php`, `Draftpool.php`, `PaymentPlan.php`, `MilitaryTrack.php`, `Item.php`, `Senate.php`, `Divinity.php`, `Conspiracy.php`, `States/*.php`, `sevenwondersduel.game.php` | Secondary, but the licensed BGA implementation. **Read only**: its `LICENCE_BGA` allows reuse on the BGA platform, not here (prior-art.md). I quote facts, not code. My only source for card **names** ↔ effects, costs and several numbers. |
| **[MC]** | `MC-freshman/7-Wonders-Duel-game` @ `5bc68315`: `docs/rules/PANTHEON_DATA.md`, `docs/rules/AGORA_DATA.md`, `src/core/data/{pantheon,agora,layouts}.ts`, `src/core/visibility.ts` | Secondary, **no licence** (all rights reserved): read only. Its data docs cite photos of the physical components. The author says some Agora data is approximated. |

BoardGameGeek was not read (it blocks scripts; see §13).

**Cross-check result.**
- **Rule text:** [BGA]'s card texts are near-verbatim copies of [PRB]/[ARB]. [MC] agrees on every rule I checked, except where listed in §12.
- **Numbers the rulebooks print only as pictures:** [BGA] and [MC] agree on the Pantheon board costs, the Grand Temple costs, the Sanctuary and Curia Julia costs, the Agora layouts, the Agora Military tokens and the Chamber VP. They **disagree** on the Divine Theater and Knossos costs, on how the 7 Politicians split between sections, on how Decrees are placed face up or face down, and on which structure slots hold the Pantheon tokens (§12).

---

# Part A: Pantheon

## 1. Components and setup [PRB pp.2-7]

| Component | Count | Where it sits | Notes |
|---|---|---|---|
| Pantheon board | 1 | Above the base board | **6 spaces** in a row. Each space prints two activation costs, one per player (§2). |
| Divinity cards | 15 | 5 face-down **Mythology decks** of 3 (Greek, Phoenician, Mesopotamian, Egyptian, Roman) | Each deck shuffled at setup. |
| Gate card | 1 | Beside the board in Age I; **face up in the one empty Pantheon space** at Age II setup | |
| Grand Temple cards | 5 | **Replace the Guilds** in Age III: 3 random ones are added unseen, 2 go back to the box unseen | One per Mythology. |
| Mythology tokens | 10 | 2 per Mythology. **5 random tokens** go face down on fixed **face-down slots of the Age I structure**, then are revealed. The other 5 go back to the box. | |
| Offering tokens | 3 | Values **2, 3 and 4** [BGA `OfferingToken`; MC]. Placed **randomly** face down on 3 fixed **face-down slots of the Age II structure**, then revealed. | |
| Snake token, Minerva pawn | 1 each | Beside the board | Used by Nisaba and Minerva. |
| New Wonders | 2 | Join the Wonder pool | The Sanctuary, The Divine Theater. |
| New Progress tokens | 3 | Join the Progress token pool | Mysticism, Poliorcetics, Engineering. |

**Setup, in order:**
1. **Age I:** set up the base game and the Age I structure. Place 5 random Mythology tokens face down on the indicated slots, then reveal them. Do the Wonder draft (now from 14 Wonders, or 16 with the promos). Shuffle the 5 Mythology decks.
2. **Age II:** put the Gate face up in the empty Pantheon space. **Reveal all Divinities in the Pantheon.** Build the Age II structure and place the 3 Offering tokens randomly, face down, on the indicated slots, then reveal them.
3. **Age III:** return **all 7 Guilds** and 3 random Age III cards to the box. Add 3 random Grand Temples unseen. So the Age III deck is still 17 + 3 = 20.

**Token slots** ([BGA] `Draftpool::$pantheonAgeTokens`; the rulebook diagram is a picture I could not read):
- **Age I:** slots **2, 4** (row 2, both ends) and **9, 11, 13** (row 4: left, middle, right). All are face-down rows.
- **Age II:** slots **6, 8, 10** (row 2: left, middle, right). A face-down row.
- [MC] uses Age I `2, 4, 10, 11, 12` and Age II `7, 9, 16`. This is a source disagreement (§12).

**The structure is unchanged:** still 20 slots per Age, same geometry.

## 2. Turn changes

**Taking a token.** When a face-down card that holds a token is flipped, the player whose turn caused the flip takes the token **before** anything else ("at the end of your turn … you must first place that token in front of yourself") [PRB p.8].

- **Mythology token** → a new **Pending Decision for that player**:
  1. draw the top **2** Divinities of that Mythology's deck;
  2. choose 1 and place it **face down** on **any empty Pantheon space**;
  3. put the other face down **on top of that deck**.
- The player keeps the token. It makes the matching Grand Temple free and scores with Mysticism.
- **Offering token:** no decision. It is a single-use discount of 2/3/4 coins on a Pantheon activation, discarded after use. Unused tokens score with Mysticism.

**All 5 Mythology tokens are always taken in Age I** *(inferred)*: every face-down card is flipped before the Age ends, and nothing can discard an Age I card from the structure (Zeus needs a Pantheon activation, which starts in Age II). So exactly 5 Divinities fill 5 of the 6 spaces, and the Gate always gets the last one.

**New main action, from Age II on: Activate a card from the Pantheon** [PRB p.9]. It does **not** use a card from the structure.
- **Cost:** pay your cost for that space, in coins and/or Offering tokens.
  - The costs per space are 3, 4, 5, 6, 7 and 8 coins: cheapest at the end nearest your own capital, dearest at the far end. Each space's two costs add up to 11.
  - [BGA] `PaymentPlan::calculate` and [MC] (from a photo) give the same numbers.
- **The Gate costs double** its space's cost (6 to 16).
- **The Sanctuary** takes 2 off your activation costs.
- **Effect:** take the card, put it in front of you, and apply its effect immediately.
- **Empty spaces stay empty.** Nothing refills them *(inferred: no refill rule exists)*.
- **FAQ:** you may activate a Divinity whose effect is impossible.
- **The turn ends as normal** after an activation (no "play again"), unless an effect says otherwise *(inferred)*.

**Grand Temples** [PRB p.8]:
- **How to build one:**
  - for free, if you hold a Mythology token of that Temple's Mythology (the token is **not** spent);
  - or by paying its cost.
- **End-of-game score:** 5, 12 or 21 VP for 1, 2 or 3 Temples built.

## 3. Divinities, Gate, Wonders and Progress tokens

The effects are from [PRB pp.11-15]. Names ↔ Mythology also come from [PRB]. The decision column is my mapping onto the model.

| Mythology | Divinity | Effect | New decision? |
|---|---|---|---|
| Mesopotamian | **Enki** | When **revealed**, draw 2 random Progress tokens from the box and place them **face up** on Enki. When activated, take one of the two; the other goes back to the box. | `enki <token>` (2 options). A **Reveal** at the moment Enki becomes face up. |
| | **Ishtar** | Grants the Law scientific symbol. | No. It can complete a science pair, which opens a `token` pick (FAQ). |
| | **Nisaba** | Put the Snake on one of the **opponent's** green cards. Nisaba counts as that card's scientific symbol. | `snake <card>` (≤ opponent's green cards). |
| Phoenician | **Astarte** | Put 7 bank coins on Astarte. They are protected from coin loss and can be spent. Each coin left at the end = 1 VP. | No *(see §5.4 on payment)*. |
| | **Baal** | Steal one of the opponent's brown or grey cards into your city. | `steal <card>`. |
| | **Tanit** | Take 12 coins. | No. |
| Greek | **Aphrodite** | 9 VP. | No. |
| | **Hades** | Build one card of your choice from the **discard pile** for free. | `hades <card>` (same shape as `mausoleum`). |
| | **Zeus** | Put any structure card, **face up or face down**, in the discard pile, and take any token on it. ([PRB16] says the token is discarded with the card instead; §12.) | `zeus <slot>`. It can target a **face-down** card, so the Move must name a **slot**, and the discarded card needs a **Reveal**. |
| Egyptian | **Anubis** | Discard the Age card under any built Wonder (either player's). The Wonder becomes unbuilt and can be rebuilt; instant effects already gained are kept. | `anubis <wonder>`. |
| | **Isis** | Build one of your Wonders for free using a card from the discard pile. | `isis <card> <wonder>`. |
| | **Ra** | Steal one of the opponent's **unbuilt** Wonders. | `ra <wonder>`. The FAQ allows 5 Wonders vs 2, but still only 7 built in total. |
| Roman | **Mars** | 2 shields. | No. |
| | **Minerva** | Place the Minerva pawn on any space of the Military track (capitals allowed). The Conflict pawn stops on entering it, and Minerva is then discarded. | `minerva <space>` (19 spaces). |
| | **Neptune** | Discard one Military token without effect, then apply another one (either side) and discard it. | `neptune-discard <token>`, then `neptune-apply <token>` (≤4 each). |
| — | **Gate** | **Reveal** the top card of each Mythology deck. Activate one of them for free, and put the others back face down on their decks. | `gate <divinity>` (≤5). A **Reveal** of up to 5 deck tops. |

| Wonder | Cost [BGA] | Effect | VP |
|---|---|---|---|
| **The Sanctuary** | P G SS ([MC] agrees) | Your Pantheon activation costs −2. Play again. | 0 |
| **The Divine Theater** | PP G WW ([MC]: PP G W; §12) | **Reveal** a whole Mythology deck of your choice. Activate one card for free, then stack the rest in the order of your choice. | 2 |

| Progress token | Effect |
|---|---|
| **Mysticism** | At the end, 2 VP per Mythology token and per Offering token you still hold. |
| **Poliorcetics** | Each time you move the Conflict pawn forward, the opponent loses 1 coin per space moved. |
| **Engineering** | You may build any card that shows a chain symbol under its cost for 1 coin, even without the chain. This is **not** a chain build, so Urbanism does not trigger (FAQ). |

| Grand Temple | Cost [BGA] ([MC] agrees) | Free with token |
|---|---|---|
| Mesopotamian | WWW G P | Mesopotamian |
| Phoenician | W S G PP | Phoenician |
| Greek | SSS G P | Greek |
| Egyptian | CCC G P | Egyptian |
| Roman | C S GG P | Roman |

**Pantheon FAQ rulings worth encoding** [PRB p.16]:
- The 8th Wonder stays in the box after Anubis.
- The Nisaba and Ishtar symbols count for pairs and supremacy.
- If Enki is revealed by the Gate or the Divine Theater but not chosen, its 2 tokens go back to the box.
- Neptune may pick a token on either side.

## 4. Pantheon chance and hidden information

### 4.1 New chance events (Reveals)

| Reveal | When it becomes known | Who learns it | Notes |
|---|---|---|---|
| Which 5 of the 10 Mythology tokens are used, and which token sits on each of the 5 Age I slots | Age I setup | Both | They are placed face down and **then revealed** [PRB p.6]. The 5 tokens left out of play are known by elimination. |
| The 2 Divinities drawn for a Mythology token | When the token is taken | **Only the drawing player** | A private Reveal (§4.2). |
| The identities of all 5 Pantheon Divinities | Age II setup | Both | The placer already knew theirs. |
| Which Offering token (2/3/4) sits on each of the 3 Age II slots | Age II setup | Both | 3! = 6 outcomes. |
| Enki's 2 Progress tokens from the box | When Enki becomes face up (Age II setup, Gate or Divine Theater) | Both: "placed face-up" [PRB p.11] | |
| A Zeus target that was face down | When it is discarded *(inferred: the discard pile is public)* | Both | [BGA] announces the discarded card by name. |
| Gate: the top card of each Mythology deck | On activation | Both, by [PRB]'s "Reveal"; **only the activator** in [BGA] and [MC] (§12) | |
| Divine Theater: a whole Mythology deck | On building the Wonder | Same disagreement as the Gate | |
| Which 3 Grand Temples are in Age III | As each one flips or is taken, like Guilds | Both | Whether Grand Temples have a distinct card back, like Guilds, is **unverified**. |
| The 3 extra Progress tokens | Part of the existing setup Reveal (5 of 13, or 5 of 15 with Agora) | Both | |

### 4.2 Private information

Pantheon creates **real private information**, though it is short-lived and narrow.

1. **The Divinity a player placed in Age I.**
   - The opponent sees the **Mythology** (the token is public) and the **space**, but not which of that Mythology's 3 Divinities it is.
   - It becomes public at Age II setup. [BGA] `Divinity::place` sends the id only to the placer.
2. **The Divinity returned to the top of a deck.** The drawing player knows it and the opponent does not. This knowledge lasts until the card leaves the deck:
   - a later Mythology token of the same Mythology draws it (and the other Mythology token of that type may be taken by either player);
   - a Gate or Divine Theater reveals it;
   - or it stays there until the end of the game.
3. **Gate and Divine Theater**, if the [BGA]/[MC] reading is used: the activator alone learns up to 5 deck tops, or a whole deck **and its new order**. Under [PRB]'s "Reveal" wording this is public. See §12.

Nothing else is private. The cards placed in the Pantheon and the Offering tokens are public from Age II. [MC] treats held Offering tokens as private, against [PRB]'s "then reveal the tokens" (§12).

**Does Pantheon break "perfect information with chance" ([algorithms.md](algorithms.md), Part 1)? Yes, but mildly.**
- The private part is mostly **Age I**: where your opponent's god is, and what is on top of a deck.
- It has a real strategic effect:
  - a player who knows the opponent's god or the deck top places their own god differently;
  - an opponent's choice of space leaks what they drew.
- It largely **disambiguates** at Age II setup. That is the regime where determinized search (PIMC) does well (algorithms.md, "disambiguation factor").
- The remaining hidden deck-top knowledge is one card per Mythology, at most.

## 5. Impact on the Game State and Move model (Pantheon)

### 5.1 New Pending Decisions and Moves

The syntax is a proposal in the style of ticket 06's Move Log. "Who decides" is always the player whose turn it is, unless stated.

| Pending Decision | Move | Options | Notes |
|---|---|---|---|
| **Place a drawn Divinity** (after taking a Mythology token) | `divinity <divinity> <space>` | 2 cards × ≤6 empty spaces (≤12) | Preceded by a **private** Reveal of the 2 cards. Two tokens flipped by one take make two queued decisions, before any "play again" turn. |
| **Take a card** (Age II/III), extended | adds `activate <divinity\|gate> [offerings]` | ≤6 Pantheon cards × which Offering tokens to spend (≤2³ = 8) | See §5.4: spending Offerings is a real choice. |
| Enki | `enki <token>` | 2 | |
| Nisaba | `snake <card>` | opponent's green cards | Skipped if there are none (FAQ: activation is allowed anyway). |
| Baal | `steal <card>` | opponent's brown + grey | |
| Hades | `hades <card>` | discard pile | Same shape as `mausoleum`. |
| Zeus | `zeus <slot>` | ≤20 structure slots | Named **by slot**, because a face-down card has no identity yet (ADR 0002). |
| Anubis | `anubis <wonder>` | built Wonders (≤7) | |
| Isis | `isis <card> <wonder>` | discard pile × your unbuilt Wonders | Same shape as `wonder <card> <wonder>`. |
| Ra | `ra <wonder>` | opponent's unbuilt Wonders (≤4) | |
| Minerva | `minerva <space>` | 19 | |
| Neptune | `neptune-discard <token>` → `neptune-apply <token>` | ≤4 → ≤3 | |
| Gate | `gate <divinity>` | ≤5 | Preceded by a Reveal of the deck tops. |
| Divine Theater | `theater-deck <mythology>` → `theater <divinity>` → `theater-order <divinity>` | 5 → ≤3 → ≤2 | The last decision picks which unused card goes on top. |

**Decisions during the other player's turn: none new** *(inferred)*. The Pantheon queue entries all belong to the active player. The promo-era cases (Statue of Liberty) are unchanged.

### 5.2 Game State additions

- **Pantheon:** 6 spaces, each empty, face-down (with the placer's Mythology), or face up with a Divinity/Gate id.
- **Mythology decks:** 5 ordered decks, with what each player knows about each position.
- **Tokens:** Mythology and Offering tokens per structure slot (Age I/II); per player, Mythology tokens and unused Offering tokens.
- **Per player:** activated Divinities, Astarte coins (separate from the treasury), and the Snake's target.
- **Minerva pawn position** (or off the track); Enki's two tokens; Grand Temples as Age III cards.
- **Ruleset flag:** `pantheon`. It changes:
  - setup (tokens, Gate, Grand Temples replacing Guilds);
  - the Wonder pool (+2) and the Progress pool (+3);
  - the new main action.

### 5.3 Victory and scoring

- **No new victory condition.**
- **Scoring:**
  - new categories are Divinity VP (Aphrodite 9, Astarte's coins), Grand Temples (5/12/21) and Mysticism;
  - the score pad has a Divinity line [PRB p.10];
  - the tie-break is unchanged.
- **Military:** Minerva can stop the Conflict pawn short. Mars and Neptune move it, or apply a token, outside a red card.

### 5.4 Things that break current base-game assumptions

1. **"Payment is never part of a Move" (ticket 06) no longer holds everywhere.**
   - Offering tokens are spent **by choice** and score 2 VP each with Mysticism. [BGA] `actionActivateDivinity($divinityId, $offeringTokens)` takes them as a player choice.
   - The Move must carry the Offering subset, or a separate `offering` decision must follow.
   - Astarte's coins (protected, worth VP) vs treasury coins is a similar choice. [BGA] always spends the treasury first. That is a simplification that the Engine could copy *(inferred)*.
2. **A Move must sometimes name a slot, not a card** (Zeus). The Encoding's "policy indexed by card, never by slot" rule needs an exception.
3. **Private Reveals.** The Move Log gets entries that only one player may see (the 2 drawn Divinities, and the Gate/Theater reveals under the [BGA] reading). `Observe(state, player)` must filter the **log-derived knowledge**, not just the board.
4. **The Wonder count per player is no longer 4** (Ra), and a built Wonder can become unbuilt (Anubis).

### 5.5 Rough Encoding size (ADR 0003: a new Encoding version)

**New identities:**
- 16 Pantheon cards (15 Divinities + Gate);
- 5 Grand Temples (Age III cards, so card-centric blocks go from 73 to 78);
- 2 Wonders (14 → 16 with promos);
- 3 Progress tokens (10 → 13);
- 5 Mythology token types, 3 Offering values, the Snake and Minerva.

**New input blocks (about 400-500 numbers):**

| Block | Size |
|---|---|
| Token on each structure slot (5 Mythology + 3 Offering kinds) | 20 × 8 = 160 |
| Divinity-centric location (Pantheon space 1-6, mine, opponent's, top of a deck known to me, unseen) | 16 × ~10 = 160 |
| Tokens held (Mythology per type, Offering by value; me and opponent) | ~16 |
| Minerva position | 19 |
| Enki tokens | 13 |
| Scalars (Astarte coins, deck sizes, Snake target flag) | ~8 |

Card, Wonder and Progress blocks also grow, in line with the new identities.

**New policy blocks:**
- If every multi-part choice is split into steps (as `liberty-keep`/`liberty-give` were), about **250 logits**: `divinity` placement, `activate`, the Offering choice, `enki`, `snake`, `steal`, `hades`, `zeus` (by slot), `anubis`, `isis`, `ra`, `minerva`, `neptune` ×2, `gate`, and the Theater steps.
- `isis <card> <wonder>` as one joint block would add **78 × 16 ≈ 1,250** logits.

---

# Part B: Agora

## 6. Components and setup [ARB pp.2-8]

| Component | Count | Where it sits | Notes |
|---|---|---|---|
| Senate board | 1 | Under the base board | **6 Chambers in a line**, 2 per section (left, middle, right). Each Chamber has an Influence zone, a Decree zone, a Control zone and a VP value. |
| Influence cubes | 12 per player | Player's supply → Chambers | A Chamber can hold any number. |
| Senator cards | 13: **7 Politicians, 6 Conspirators** | Shuffled into the Age decks: **5 in Age I, 5 in Age II, 3 in Age III** | They have their **own card back** (unlike Age cards), so a face-down Senator is visibly a Senator. Each Politician shows one Senate section. The 6 Conspirators are identical. |
| Conspiracy cards | 16, all different | One face-down deck | The back has a Preparation zone. |
| Decree tokens | 16, all different | **6 random ones**, one per Chamber; the other 10 go back to the box unseen | Some are placed face up and some face down, "according to the symbol shown" on the board (§12). |
| New Wonders | 2 | Join the Wonder pool | Curia Julia, Knossos. |
| New Progress tokens | 2 | Join the Progress pool | Corruption, Organized Crime. |
| Military tokens | 4 | **Replace** the base Military tokens | New effects (§7.5). |

**Layout:** the structures grow, and the card counts check out.

| Age | Rows (top to bottom) | Slots | Deck |
|---|---|---|---|
| I | 3-4-5-6-7 | 25 | 23 − 3 + 5 Senators |
| II | 7-6-5-4-3 | 25 | 23 − 3 + 5 |
| III | 2-3-4-5-4-3-2 | 23 | 20 − 3 + 3 Senators (incl. 3 Guilds or Grand Temples) |

- The layouts come from [BGA] `Draftpool::$agoraAges` and [MC] `AGORA_AGE_LAYOUTS`, which agree. [ARB] p.8 shows them only as a picture.
- Rows still alternate face up/face down, starting face up at the top [BGA `row_index % 2`].

**Decree visibility at setup:** [BGA] reveals 3 of the 6, those in Chambers 1, 3 and 5 (one per section). The others stay face down until someone places a cube in that Chamber [ARB p.10].

## 7. Turn changes

### 7.1 Turn order [ARB pp.11-13]

1. **Trigger one prepared Conspiracy** (optional; at most one per turn; only at the start of your turn).
2. **Take an accessible card** and do one of:
   - **build it**, or **recruit** it if it is a Senator;
   - discard it for coins;
   - build a Wonder with it;
   - **prepare a Conspiracy** with it: the card goes face down, half under one of your unprepared Conspiracies, at no cost, and your turn ends. Any card can be used.

With Pantheon as well, step 2 is "take a card **or** activate a Pantheon card".

### 7.2 Recruiting a Senator

- **Cost:** coins equal to the number of Senators already in your city (0, 1, 2…). It is free with Corruption.
- **Politician:** take 1, 2 or 3 **Senate actions** for 0-1, 2-3 or 4+ blue cards in your city. Decree 12 adds +2 to the blue count. Each action is one of:
  - **Place:** 1 cube from your supply into either Chamber of the Politician's section. Placing in a Chamber whose Decree is face down **flips the Decree**.
  - **Move:** 1 of your cubes to an **adjacent** Chamber. The same cube may move again, including one just placed.
  - [BGA] lets the player **skip the remaining actions** (`actionSkipMoveInfluence`). [ARB] says "you can take". So Senate actions are optional *(reading shared by [BGA])*.
- **Conspirator:** choose **one** of:
  - **Place** 1 cube in **any** Chamber;
  - **Conspire:** draw 2 Conspiracies, keep 1 face down, and put the other on the **top or bottom** of the deck (your choice). With Organized Crime you keep both.

### 7.3 Chamber control and Political Supremacy

- **Control:** you control a Chamber if you have **strictly more** cubes there. Then you benefit from its Decree. Ties mean no controller.
- **Political Supremacy:** you control **all 6 Chambers**. It is an **immediate win, at any moment** [ARB p.12].
- **End-of-game VP per controlled Chamber:** **1, 2, 3, 3, 2, 1** from left to right [BGA `NextPlayerTurnTrait`; MC from a photo].

### 7.4 Senators, Decrees and Conspiracies

**Politician sections:**
- [BGA] has **2 left, 3 middle and 2 right** (ids 74-80).
- [MC] approximates 3/2/2 and says it has no scan (§12).

**Decrees** [ARB pp.14-15]. The names are [BGA]'s descriptions; the tokens have no printed names.

| # | Effect for the Chamber's controller |
|---|---|
| 1-4 | Each time **either** player builds a **blue / green / yellow / red** card, take coins equal to the current Age. |
| 5-7 | Ignore 1 cost symbol (a resource or the coins) when building **yellow / red / green** cards. |
| 8 | Wonders cost 1 resource less (your choice). |
| 9 | +1 shield, and the pawn moves 1 toward the opponent at once. Losing control moves it 1 back. If the opponent **steals** control, it moves 2 toward you. |
| 10-11 | Buying a **brown / grey** resource costs 1 coin less (minimum 1). |
| 12 | +2 to your blue-card count for Senate actions. |
| 13 | +2 coins when discarding a card. |
| 14 | Each time **either** player builds a Wonder, take coins equal to the current Age. |
| 15 | You may use chain symbols on the **opponent's** cards. |
| 16 | Recruiting a Conspirator gives you a "play again". |

**Conspiracies** [ARB pp.17-19 for the effects; names and the optional "move 1 cube" suffix from [BGA] `Material.php`].
- [ARB] prints no names, so the name ↔ effect pairing is [BGA]'s.
- "+move" means "then you may move 1 of your cubes to an adjacent Chamber".

| [BGA] name | Effect | Decision(s) |
|---|---|---|
| Extortion | Take one of the opponent's **unbuilt** Wonders. +move | wonder; move |
| Blackmail | Take half the opponent's coins (rounded up). +move | move |
| Expropriation | Put one of the opponent's **blue** cards in the discard pile. +move | card; move |
| Swindle | Put one of the opponent's **yellow** cards in the discard pile. +move | card; move |
| Obscurantism | Choose a Progress token **on the board, the opponent's, or out of the game (box)** and lock it face down on this card; no one can use it. | token (box ones seen **privately**) |
| Coup | 2 shields [BGA `setMilitary(2)`; [ARB] shows only the icon] | — |
| Property Fraud | Build one card at **"the end of the structure"** for free; not a Senator. | card. [BGA]: the **top row**, like the Messe. [MC]: any accessible card (§12). |
| Treason | Look **secretly** at the cards removed at setup (Age I: 3; Age II: 6 from Ages I-II; Age III: 9 from Ages I-III) and "play" one for free. | card (private view). Guilds and Grand Temples are not included (they are not "removed cards"; [MC] agrees). "Play" = build, per [BGA] `ConstructBuildingFromBox` *(inferred)*. |
| Political Maneuver | Place 1 cube in any Chamber, remove 1 opponent cube, +move. | place; remove; move |
| Espionage | **Secretly** look at all Progress tokens in the box and take one. | token (private view) |
| Turn of Events | Discard 1 accessible structure card; you may repeat once. +move | card; card-or-stop; move |
| Embezzlement | Gain coins = your cubes in the Senate; the opponent loses coins = their cubes. | — |
| Foreclosure | Take one of the opponent's brown or grey cards. | card |
| Coercion | Take one of the opponent's blue or green cards and give them one of **yours** of the same colour. +move | take card; give card; move |
| Insider Influence | Move any Decree to another Chamber, **under** its Decree. One Chamber is then empty, but it still scores and counts for Supremacy. +move | decree × chamber; move |
| Sabotage | Return one of the opponent's **built** Wonders to the box. Its instant effects stay; [BGA] says its lasting effects (VP, resource choice) are lost. | wonder |

### 7.5 New Wonders, Progress tokens and Military tokens

| Wonder | Cost [BGA] | When **drafted** | When **built** | VP |
|---|---|---|---|---|
| **Curia Julia** | W C S G P ([MC] agrees) | Conspire: draw 2, keep 1, the other to top or bottom | Trigger one **unprepared** Conspiracy (optional); +6 coins; play again | 0 |
| **Knossos** | GG C W S ([MC]: G C W; §12) | Place 1 cube in any Chamber | Place 1 cube; optionally move 1 | 3 |

- **Drafting itself now has effects.** Picking Curia Julia or Knossos creates a Pending Decision **during the Wonder draft**.

| Progress token | Effect |
|---|---|
| **Corruption** | Recruit all Senators for free. |
| **Organized Crime** | When you Conspire, keep both cards. |

**Agora Military tokens** [ARB p.17 icons; [BGA] `Item.php`; [MC] from a photo]:
- They replace the coin-loss tokens. The token goes to the player **advancing** the pawn.
- **Small token** (the inner zone): place 1 cube in any Chamber.
- **Large token** (the outer zone): remove 1 opponent cube, then optionally move 1 of yours.
- Military VP and the military supremacy rule are unchanged.

## 8. Agora chance and hidden information

### 8.1 New chance events (Reveals)

| Reveal | When | Who learns it |
|---|---|---|
| Which 13 Senators go to which Age (5/5/3), and their slots | Senators are just more cards in the Age decks. A face-down slot now has **three** possible backs in Age III (Age III, Guild/Temple, Senator) and two in Ages I-II. The identity is revealed on flip. | Both |
| The 6 Decrees in play and their Chambers | 3 at setup ([BGA]); each of the others **when the first cube is placed in its Chamber** | Both |
| The 2 Conspiracies drawn when conspiring (including Curia Julia's draft draw) | On drawing | **Only the drawer** |
| A face-down Conspiracy's identity | When it is triggered | Both |
| Treason: the removed cards of Ages ≤ current | On trigger | **Only the triggering player**. The built card becomes public. |
| Espionage: the Progress tokens in the box | On trigger | **Only the triggering player**. The taken token becomes public. |
| Obscurantism choosing from the box | On trigger | Only the triggering player sees the box. The locked token stays face down ([BGA] sends its id only to that player). |

### 8.2 Private information

This is **substantial, persistent private information**:
1. **Each player's hand of unprepared and prepared Conspiracies.**
   - Up to 16 cards, held for many turns, possibly across Ages.
   - The opponent sees only the count and which ones are prepared ([BGA] `Conspiracy::choose` sends the id only to the owner).
2. **Deck-order knowledge.** The Conspiracy returned to the top or bottom of the deck is known to its returner. A later Conspire by the opponent may draw a card the first player knows.
3. **Knowledge of the unseen card pools.** Treason shows a player every removed Age card up to that Age.
   - That player then knows **exactly** which cards remain unseen in the face-down slots, while the opponent still sees a wider pool.
   - Espionage does the same for the box Progress tokens, which matter for the Great Library and Enki.
4. **A locked token's identity** after Obscurantism, if it came from the box.

**Not private:** the card used to prepare a Conspiracy. It was a face-up accessible card when taken, and [BGA] announces it by name. [MC] hides it, which is a stricter, non-rule reading (§12).

**Does Agora break "perfect information with chance"? Yes, clearly.**
- This is poker-style private information. The opponent's actions leak it: when they prepare, which card they sacrifice, and when they trigger.
- It is long-lived and strategically central: Conspiracies can swing military, coins, Wonders and the Senate.
- So the "nobody knows more than the other" argument in algorithms.md (Part 1) **fails for Agora**.
- algorithms.md's own fallback still applies: try determinized search first (Blüml et al. 2023), and consider ReBeL-style beliefs only if that measurably hurts.

## 9. Impact on the Game State and Move model (Agora)

### 9.1 New Pending Decisions and Moves

| Pending Decision | Move | Options | Who |
|---|---|---|---|
| **Start of turn: trigger a Conspiracy?** | `trigger <conspiracy>` / `no-trigger` | ≤ prepared + 1 | active. It could also be folded into "take a card" as an extra option *(design choice)*; [BGA] offers it in the same state as the card actions. |
| **Take a card**, extended | adds `recruit <senator>` (replaces `build` for Senators) and `prepare <card> <conspiracy>` | prepare: accessible cards × unprepared Conspiracies | active |
| **Senate action** (repeat 1-3×) | `place <chamber>` / `move <from> <to>` / `senate-done` | 2 places (section) + ≤10 adjacent moves + done | active |
| **Conspirator choice** | `conspirator place <chamber>` / `conspire` | 6 + 1 | active |
| **Conspire: keep one** | `conspire-keep <conspiracy>` | 2 (skipped with Organized Crime) | active. Preceded by a **private** Reveal. |
| **Conspire: where the other goes** | `conspire-return top\|bottom` | 2 | active |
| Draft-time: Curia Julia / Knossos | the same `conspire-*` / `place <chamber>` | as above | **the drafting player, inside the draft** |
| Curia Julia built: trigger an unprepared Conspiracy | `trigger <conspiracy>` / `no-trigger` | ≤ hand + 1 | active |
| Conspiracy sub-decisions | `extort <wonder>`, `expropriate <card>`, `swindle <card>`, `obscure <token>`, `fraud <card>`, `treason <card>`, `remove <chamber>`, `espionage <token>`, `turn <card>` (×1-2, then `stop`), `foreclose <card>`, `coerce-take <card>` → `coerce-give <card>`, `decree <decree> <chamber>`, `sabotage <wonder>`, optional `move <from> <to>` / `move-skip` | see §7.4 | active |
| Military token (small / large) | `place <chamber>` / `remove <chamber>` → optional `move` | 6 / 6 → ≤10 + skip | **the player who advanced the pawn. That can be the non-active player** (below). |

**Decisions during the other player's turn (new cases):**
1. **Decree 9 losing control.**
   - Suppose you move your own cube and lose control of Decree 9's Chamber. The pawn then steps toward **your** capital, which is the opponent advancing.
   - If it enters a Military token zone, the **opponent** takes that token's Senate action during your turn.
   - [BGA] handles this with a `STATE_PLAYER_SWITCH` in `Item.php` ("player !== active && Agora && military tokens").
2. **Coercion giving the opponent a green card** may complete their science pair, so the **opponent** picks a Progress token *(inferred, by analogy with the Statue of Liberty ruling)*.

**Immediate wins by the non-active player:**
- **Military:** the pawn can move toward the active player's capital (Decree 9). [BGA] has a comment: "With Agora, the pawn can go backwards, so the winner isn't always the active player."
- **Political:** moving or removing a cube can hand the opponent all 6 Chambers. **Science:** Coercion, as above.

### 9.2 Game State additions

- **Senate:** 6 Chambers × (cubes per player, controller, Decree stack with face up/down, Control-zone marker); cubes in each player's supply.
- **Conspiracies:**
  - deck order, with per-player knowledge of the top/bottom positions;
  - each player's Conspiracies (unprepared / prepared with the Age card under it / triggered);
  - a locked Progress token.
- **Senators:** they are cards with **duplicates**: 6 identical Conspirators and 3 Politician kinds.
  - Ticket 06 says "Moves name the card, since every base-game card is unique". That no longer holds.
  - The Engine needs instance ids (for example `conspirator#3`, fixed at the Reveal), or Moves that name the slot.
- **Layout:** 25/25/23 slots, and a third face-down back type (Senator).
- **Military tokens:** the token kind depends on the Ruleset.
- **Ruleset flag:** `agora`. It changes:
  - the layouts, the decks and the Military tokens;
  - the Wonder pool (+2) and the Progress pool (+2);
  - the new victory type.

### 9.3 Victory and scoring

- **New victory type: Political Supremacy**, which is immediate. The Result's victory types become military / science / **political** / civilian. The Encoding's 7-class outcome head becomes 9 classes.
- **New VP category:** Senate (Chamber VP 1-3 each, up to 12). Knossos is worth 3. The tie-break is unchanged (blue VP, then a shared win).

### 9.4 Rough Encoding size

**New identities:**
- 13 Senators (or 4 kinds); card-centric blocks go from 73 to 86, or 91 with Pantheon;
- 16 Conspiracies, 16 Decrees;
- 2 Wonders, 2 Progress tokens;
- 6 Chambers; 2 Military token kinds.

**New input blocks (about 1,000-1,300 numbers):**

| Block | Size |
|---|---|
| Layout grows from 20 to 25 slots, each with a +13-way card one-hot and a Senator back | ≈ +500 |
| Chambers: my cubes, opponent's cubes, controller, Decree one-hot + face-down + second Decree | 6 × ~38 ≈ 230 |
| Conspiracy-centric: mine unprepared, mine prepared, mine triggered, opponent's triggered, known top/bottom of the deck, locked-token holder | 16 × ~6 ≈ 100 |
| Counts of the opponent's hidden unprepared/prepared Conspiracies | ~4 |
| Decree-centric location | 16 × ~8 ≈ 130 |
| Cubes in supply, Senators owned | small |

**New policy blocks:**
- The card-indexed blocks grow from 73 to 86. `wonder <card> <wonder>` alone grows to 86 × 18 ≈ 1,550.
- New blocks, split into steps: `trigger` 17, `prepare` 86 + 16, `place`/`move`/`remove`/`done` ~23, `conspire-*` ~20, and the Conspiracy sub-decisions (mostly card-, Wonder- or token-indexed) ≈ 400.
- Total: roughly **+700-900 logits**.

---

# Part C: Both expansions together, and the base-game argument

## 10. Combined play [ARB p.13; BGA]

- **Turn order:** trigger a Conspiracy (optional), then take a structure card **or** activate a Pantheon card.
- **Layouts:** the Agora ones, from the "Player Aid card".
- **Pantheon token slots in the Agora layouts** ([BGA] `$agoraPantheonAgeTokens`):
  - Age I: slots 4, 6, 12, 14, 17;
  - Age II: slots 7, 9, 12.
- **Age III:** Grand Temples replace the Guilds, plus 3 Senators.
- **Pools:**
  - Wonders: 12 + 2 + 2, plus 2 promos = 18;
  - Progress tokens: 10 + 3 + 2 = 15 (5 on the board).
- **Interactions:**
  - Zeus or Turn of Events can discard a Senator;
  - Neptune applies an **Agora** Military token, as a Senate action for the activator [BGA `MilitaryTrack::movePawn`];
  - Mysticism counts tokens only;
  - Ra and Extortion both steal unbuilt Wonders.

## 11. Summary: does each expansion break "perfect information with chance"?

| | Private information | Lifetime | Verdict |
|---|---|---|---|
| Base (+ promos) | The Great Library's 2 discarded tokens | Irrelevant afterwards | Perfect information with chance (algorithms.md) |
| **Pantheon** | The Age I god placements; the returned Divinity on top of a deck; Gate/Theater views under the [BGA]/[MC] reading | Mostly until Age II setup; ≤1 card per deck afterwards | **Broken, mildly.** It disambiguates quickly, which suits determinized search. |
| **Agora** | Conspiracy hands; deck top/bottom; Treason/Espionage views of the box | Many turns, often the whole game | **Broken, substantially.** Actions leak information, so the opponent model matters. |

**What this means for the model** (input for ticket 18):
- **The Observation can no longer be "the Game State minus things nobody knows".** It must hold **per-player knowledge**, i.e. what each player has privately seen.
- **The Move Log needs private Reveals**, visible to one player only.
- **Search may only sample the opponent's private cards** from a pool consistent with the public history. In Advisor mode the owner never knows the opponent's hand.

---

## 12. Source disagreements

1. **Zeus and tokens.**
   - [PRB] (revised): the player **takes** any token on the discarded card.
   - [PRB16]: the tokens are **discarded with** the card. [BGA]'s tooltip text and [MC] (citing the Russian and Hungarian rulebooks) say the same.
   - [BGA]'s code (`DiscardAgeCardTrait`) gives the token to the player, matching [PRB].
   - Since only Age II Offering tokens can still be on cards when Zeus is available, this decides who gets an Offering token. **Follow [PRB].**
2. **Anubis wording.** [PRB16] and [BGA] list "shields" among the kept instant effects; [PRB] omits shields. *(Inferred: no difference in practice. Shields are a pawn move that has already happened, and nothing moves the pawn back.)*
3. **Gate / Divine Theater visibility.** [PRB] says "Reveal"; [BGA] and [MC] show the cards **only to the activator**. This changes Pantheon's private information (§4.2). **Unresolved.** The rulebook word suggests public.
4. **Offering tokens.** [PRB]: placed face down, "then reveal the tokens". [MC]: they stay private when held. **Follow [PRB]** (public).
5. **The Divine Theater's cost.** [BGA] PP G WW (5 resources); [MC] PP G W (4, "corrected from the physical card"). Unresolved; needs the card.
6. **Knossos's cost.** [BGA] GG C W S (5); [MC] G C W (3, "read from the card face"). Unresolved; needs the card.
7. **Pantheon token slots.** [BGA] Age I 2, 4, 9, 11, 13 and Age II 6, 8, 10; [MC] Age I 2, 4, 10, 11, 12 and Age II 7, 9, 16. Both are face-down slots. [PRB] pp.6-7 is a diagram I could not render. Unresolved.
8. **Politician sections.** [BGA] 2 / 3 / 2; [MC] 3 / 2 / 2 (an admitted approximation). Prefer [BGA].
9. **Decree face up/down.** [BGA]: Chambers 1, 3, 5 face up and 2, 4, 6 face down. [MC]: a token is face up only if it "matches" a printed symbol. The [ARB] text ("according to the symbol shown: Face up / Face down") reads like [BGA]'s fixed pattern *(inferred)*. Prefer [BGA].
10. **Property Fraud's "end of the structure".** [BGA]: the **top row** (the farthest, always face up, like the Messe). [MC]: any accessible card (it says this is a guess). Prefer [BGA].
11. **Conspiracy names.** [MC] calls the "discard an opponent's blue card" effect **Sabotage**. [BGA] calls that **Expropriation**, and gives "Sabotage" to "return an opponent's built Wonder to the box". [MC] has photos of only 6 cards. Prefer [BGA].
12. **The card used to prepare a Conspiracy.** [BGA] makes it public; [MC] hides it. [ARB] says it is placed face down, but it was face up when taken. Prefer [BGA] (public).
13. **Astarte payment order.** [BGA] spends treasury coins before Astarte coins automatically. [PRB] gives no order; it is a player choice. Not a source conflict, but a [BGA] simplification.

## 13. Open uncertainties

- **Pictures I could not read** (no PDF renderer on this machine):
  - [PRB] pp.6-7 token positions;
  - [ARB] p.8 layouts (two sources agree) and the Chamber symbols on p.6;
  - every printed cost in both rulebooks.
  - Costs therefore rest on [BGA] and [MC] only, and two of them disagree (§12, items 5-6).
- **BoardGameGeek and the Repos FAQ (www.7wondersduel.com/faq, cited by [ARB]) were not read**, since rprod.com returned 403 to scripts. Expansion rulings there are unchecked. Notable open questions:
  - whether Senate actions are optional ([BGA]: yes);
  - whether a Conspiracy can be triggered at the start of a play-again turn *(inferred: yes, it is "your turn")*;
  - whether a free Grand Temple build counts as a chain for Urbanism (no source);
  - whether Grand Temples have a distinct card back like Guilds;
  - whether Offering tokens can overpay (the FAQ only says no change is given).
- **Decisions by the non-active player** (Coercion → science pick) are inferred from the Statue of Liberty precedent, not stated.
- **Solo modes** and the TTS mod's handling of the expansions are out of scope. The mod supports both expansions (tts-mod-and-api.md §2).
