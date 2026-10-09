# 7 Wonders Duel: base game rules reference (for the Engine)

Researched 2026-10-09 for ticket `01-base-game-rules-reference`. Base game only. Pantheon, Agora and the promo Wonders (Messe, Statue of Liberty) are excluded.

## Sources

| Key | Source | Trust |
|---|---|---|
| **[RB]** | Official English rulebook, Repos Production / Sombreros Production 2015, 20 pp. A mirror of the publisher PDF (InDesign, created 2015-09-17) is at <https://aadl.org/files/catalog_guides/7%20Wonders%20Duel.pdf>. Page numbers below are the printed page numbers. | Primary. Card costs are printed as icons only, so the text layer has names, VP and coin values but no costs. |
| **[FAQ]** | Repos Production FAQ, <https://www.rprod.com/en/7-wonders-duel/faq> | Primary. It has no base-game errata. Its one base-game entry says the Law token's symbol is a new, 7th scientific symbol. |
| **[BGA]** | Board Game Arena implementation (licensed), `koenheltzel/bga-sevenwondersduel`, branch `develop`: `modules/php/Material.php`, `Draftpool.php`, `MilitaryTrack.php`, `Player.php` | Secondary. A licensed adaptation that follows the latest English edition (see [BGAF]). |
| **[7WD]** | `7wd-io/engine` (Go), branch `main`: `registry.go`, `deck.go`, `track.go`, `symbol.go` | Secondary. An independent open-source engine. |
| **[BGAF]** | BGA forum thread "Seven Wonders Duel Rules Are Not Correct", <https://forum.boardgamearena.com/viewtopic.php?t=21508> | Secondary. Documents how rulebook printings differ. |

**Cross-check result.** I diffed all 73 cards (66 Age cards and 7 Guilds) and all 12 Wonders between [BGA] and [7WD] with a script. Costs, VP, shields, science symbols and chain links are **identical** in both. All VP, coin and shield values that [RB] prints as text on pp. 14-19 also match. The only conflicts are card **names** (see §11).

---

## 1. Components and setup

Components [RB p.3]:
- 23 Age I cards, 23 Age II cards and 20 Age III cards.
- 7 Guilds and 12 Wonders.
- 4 Military tokens and 10 Progress tokens.
- 1 Conflict pawn.
- 31 coins (14×1, 10×3, 7×6).

Setup [RB p.6-7]:
1. Put the Conflict pawn on the centre (neutral) space. Place the 4 Military tokens face up.
2. Shuffle the 10 Progress tokens and put **5 face up** on the board. The other 5 go back to the box, but they stay relevant for the Great Library.
3. Each player takes **7 coins**.
4. **Wonder draft.** Choose a first player and shuffle the 12 Wonders.
   - Round 1: reveal 4 Wonders. The first player picks 1, the second player picks 2, and the first player takes the last one.
   - Round 2: reveal 4 more, and the **second** player picks first (P2 picks 1, P1 picks 2, P2 takes the last).
   - Each player ends with 4 Wonders. The remaining 4 Wonders are not used.
   - Fixed first-game Wonders: P1 gets the Pyramids, Great Lighthouse, Temple of Artemis and Statue of Zeus. P2 gets Circus Maximus, Piraeus, the Appian Way and the Colossus.
5. From each Age deck, **remove 3 cards unseen**, so each Age deck has 20 cards. Then add **3 random Guilds** (unseen) to the Age III deck (17 + 3 = 20). The other 4 Guilds go back to the box.
6. The draft's first player **starts Age I** [RB p.10].

Note: [RB] does not say how to pick the first player. Use a random choice.

## 2. Turn structure and moves

On your turn you take one **accessible** card: a card that is not even partly covered by another card [RB p.10]. You then do exactly one of these [RB p.10-11]:

1. **Construct the Building.** Pay its cost (§3) and add it to your city.
2. **Discard for coins.** Gain **2 + (number of yellow cards in your city)** coins. The card goes to the discard pile, which both players can look through.
3. **Construct a Wonder.** Pay the cost of one of your unbuilt Wonders, not the card's cost. The Age card is tucked face down under the Wonder. It has no effect and does **not** go to the discard pile.

After the move, flip face up any face-down card that is now accessible. A "play again" turn starts **after** that reveal [RB p.10].

A legal move always exists, because discarding is always allowed.

## 3. Costs, production and trading [RB p.8-9]

**Production**
- Brown and grey cards produce resources. A double card (Sawmill, Brickyard, Shelf Quarry) produces 2 of its resource.
- Choice producers give **one** of the listed resources per turn: Forum, Caravansery, the Great Lighthouse and Piraeus.
- Resources are **never consumed**. Production is reusable every turn.

**Trading.** For each missing resource, pay the bank:

> **COST = 2 + number of that resource produced by the opponent's brown and grey cards.**

- The count includes the ×2 on double cards. Production from the opponent's yellow cards and Wonders is **not** counted.
- There is no limit on how many resources you buy.
- Coins go to the bank, unless the opponent owns Economy (§7).
- The fixed-price yellow cards set the price of their resources to **1 coin**:
  - Stone Reserve: Stone.
  - Clay Reserve: Clay.
  - Wood Reserve: Wood.
  - Customs House: Glass and Papyrus.

**Coin costs** are paid to the bank and are added on top of any trading costs.

**Chains (free construction).** Some Age II and III cards show a white chain symbol under their cost. If your city already has the card that carries that symbol, you build the new card **for free**. Free means no resources and no coins, including coin costs such as Barracks.

**Example from [RB p.9].** Bruno builds the Fortifications (2 Stone, 1 Clay, 1 Papyrus) while producing 2 Stone himself. Antoine produces 1 Clay. Bruno pays 3 (Clay) + 2 (Papyrus) = 5 coins.

## 4. Age structures [RB p.10 and p.20 diagram; slot geometry from BGA Draftpool.php and 7WD registry.go]

- Each Age lays out exactly 20 cards. Rows alternate face up and face down, and the first (top) row is face up in every Age.
- **"Covered by"** lists the cards that must leave before a card becomes accessible.
- Slots are numbered from the **top row (farthest from the players) down to the bottom row (nearest)**, left to right.
- Column = horizontal offset in half-card units.

### Age I: an upward pyramid with rows of 2-3-4-5-6

The 6-card bottom row starts accessible.

| Row | Cards | Face | Columns | Slots |
|---|---|---|---|---|
| 1 | 2 | up | 4, 6 | 0-1 |
| 2 | 3 | down | 3, 5, 7 | 2-4 |
| 3 | 4 | up | 2, 4, 6, 8 | 5-8 |
| 4 | 5 | down | 1, 3, 5, 7, 9 | 9-13 |
| 5 | 6 | up | 0, 2, 4, 6, 8, 10 | 14-19 (start accessible) |

Covered by:
- 0←{2,3}, 1←{3,4}
- 2←{5,6}, 3←{6,7}, 4←{7,8}
- 5←{9,10}, 6←{10,11}, 7←{11,12}, 8←{12,13}
- 9←{14,15}, 10←{15,16}, 11←{16,17}, 12←{17,18}, 13←{18,19}

### Age II: an inverted pyramid with rows of 6-5-4-3-2

The 2-card bottom row starts accessible.

| Row | Cards | Face | Columns | Slots |
|---|---|---|---|---|
| 1 | 6 | up | 0, 2, 4, 6, 8, 10 | 0-5 |
| 2 | 5 | down | 1, 3, 5, 7, 9 | 6-10 |
| 3 | 4 | up | 2, 4, 6, 8 | 11-14 |
| 4 | 3 | down | 3, 5, 7 | 15-17 |
| 5 | 2 | up | 4, 6 | 18-19 (start accessible) |

Covered by:
- 0←{6}, 1←{6,7}, 2←{7,8}, 3←{8,9}, 4←{9,10}, 5←{10}
- 6←{11}, 7←{11,12}, 8←{12,13}, 9←{13,14}, 10←{14}
- 11←{15}, 12←{15,16}, 13←{16,17}, 14←{17}
- 15←{18}, 16←{18,19}, 17←{19}

Edge cards in Age II are covered by only **one** card.

### Age III: a "hourglass" with rows of 2-3-4-2-4-3-2

The 2-card bottom row starts accessible.

| Row | Cards | Face | Columns | Slots |
|---|---|---|---|---|
| 1 | 2 | up | 2, 4 | 0-1 |
| 2 | 3 | down | 1, 3, 5 | 2-4 |
| 3 | 4 | up | 0, 2, 4, 6 | 5-8 |
| 4 | 2 | down | 1, 5 | 9-10 |
| 5 | 4 | up | 0, 2, 4, 6 | 11-14 |
| 6 | 3 | down | 1, 3, 5 | 15-17 |
| 7 | 2 | up | 2, 4 | 18-19 (start accessible) |

Covered by:
- 0←{2,3}, 1←{3,4}
- 2←{5,6}, 3←{6,7}, 4←{7,8}
- 5←{9}, 6←{9}, 7←{10}, 8←{10}
- 9←{11,12}, 10←{13,14}
- 11←{15}, 12←{15,16}, 13←{16,17}, 14←{17}
- 15←{18}, 16←{18,19}, 17←{19}

The middle row of 2 is a bottleneck: taking slot 9 opens **two** cards (5 and 6) at once.

[BGA] and [7WD] give the same geometry. [BGA] Age III columns are `[5,7],[4,6,8],[3,5,7,9],[4,8],[3,5,7,9],[4,6,8],[5,7]`, which is the same as the table shifted by 3. Rule in both: a card at column *c* in row *r* is covered by the cards at *c±1* in row *r+1*. Face up = odd rows counted from 1 ([7WD] `deck.go`, [BGA] `row_index % 2 == 0`).

**Hidden information (affects the Observation).**
- Guild cards have a **purple back**, unlike other Age III cards. A face-down Guild is therefore known to be *a* Guild, though not which one. [7WD] `getLayout` reports face-down Guilds as a separate code (`-2`).
- The 3 cards removed from each Age are never revealed. Neither are the 4 unused Guilds or the 5 unused Progress tokens.
- Each Age deck has its own back, so a face-down card's Age is always known.

## 5. Card list

Notation:
- Resources: W = Wood, C = Clay, S = Stone, G = Glass, P = Papyrus. "$n" = n coins. "—" = free.
- **Chain→** means this card carries the chain symbol that makes the named later card free.
- **Free if** means this card is free when you own the named earlier card.
- Science symbols use the IDs in §6.

All rows below were confirmed identical in [BGA] Material.php and [7WD] registry.go. VP and coin numbers also match [RB pp.15,18-19].

### Age I (23 cards)

| Card | Colour | Cost | Effect | VP | Chain → |
|---|---|---|---|---|---|
| Lumber Yard | brown | — | W | | |
| Logging Camp | brown | $1 | W | | |
| Clay Pool | brown | — | C | | |
| Clay Pit | brown | $1 | C | | |
| Quarry | brown | — | S | | |
| Stone Pit | brown | $1 | S | | |
| Glassworks | grey | $1 | G | | |
| Press | grey | $1 | P | | |
| Guard Tower | red | — | 1 shield | | |
| Stable | red | W | 1 shield | | Horse Breeders (horseshoe) |
| Garrison | red | C | 1 shield | | Barracks (sword) |
| Palisade | red | $2 | 1 shield | | Fortifications (tower) |
| Workshop | green | P | science 5 | 1 | |
| Apothecary | green | G | science 7 | 1 | |
| Scriptorium | green | $2 | science 6 | | Library (book) |
| Pharmacist | green | $2 | science 4 | | Dispensary (gear) |
| Stone Reserve | yellow | $3 | Stone costs 1 to buy | | |
| Clay Reserve | yellow | $3 | Clay costs 1 to buy | | |
| Wood Reserve | yellow | $3 | Wood costs 1 to buy | | |
| Tavern | yellow | — | +4 coins | | Lighthouse (amphora) |
| Theater | blue | — | | 3 | Statue (mask) |
| Altar | blue | — | | 3 | Temple (moon) |
| Baths | blue | S | | 3 | Aqueduct (water drop) |

### Age II (23 cards)

| Card | Colour | Cost | Free if | Effect | VP | Chain → |
|---|---|---|---|---|---|---|
| Sawmill | brown | $2 | | WW | | |
| Brickyard | brown | $2 | | CC | | |
| Shelf Quarry | brown | $2 | | SS | | |
| Glass-Blower | grey | — | | G | | |
| Drying Room | grey | — | | P | | |
| Walls | red | SS | | 2 shields | | |
| Horse Breeders | red | C W | Stable | 1 shield | | |
| Barracks | red | $3 | Garrison | 1 shield | | |
| Archery Range | red | S W P | | 2 shields | | Siege Workshop (target) |
| Parade Ground | red | CC G | | 2 shields | | Circus (helmet) |
| Library | green | S W G | Scriptorium | science 6 | 2 | |
| Dispensary | green | CC S | Pharmacist | science 4 | 2 | |
| School | green | W PP | | science 7 | 1 | University (lyre) |
| Laboratory | green | W GG | | science 5 | 1 | Observatory (oil lamp) |
| Forum | yellow | $3 C | | produces G **or** P each turn | | |
| Caravansery | yellow | $2 G P | | produces W **or** C **or** S each turn | | |
| Customs House | yellow | $4 | | G and P cost 1 to buy | | |
| Brewery | yellow | — | | +6 coins | | Arena (barrel) |
| Courthouse / **Tribunal** (§11) | blue | WW G | | | 5 | |
| Statue | blue | CC | Theater | | 4 | Gardens (pillar) |
| Temple | blue | W P | Altar | | 4 | Pantheon (sun) |
| Aqueduct | blue | SSS | Baths | | 5 | |
| Rostrum | blue | S W | | | 4 | Senate (building) |

### Age III (20 cards; no brown or grey)

| Card | Colour | Cost | Free if | Effect | VP |
|---|---|---|---|---|---|
| Arsenal | red | CCC WW | | 3 shields | |
| Pretorium / **Courthouse** (§11) | red | $8 | | 3 shields | |
| Fortifications | red | SS C P | Palisade | 2 shields | |
| Siege Workshop | red | WWW G | Archery Range | 2 shields | |
| Circus | red | CC SS | Parade Ground | 2 shields | |
| Academy | green | S W GG | | science 3 | 3 |
| Study | green | WW G P | | science 3 | 3 |
| University | green | C G P | School | science 1 | 2 |
| Observatory | green | S PP | Laboratory | science 1 | 2 |
| Chamber of Commerce | yellow | PP | | +3 coins per **grey** card in your city | 3 |
| Port | yellow | W G P | | +2 coins per **brown** card in your city | 3 |
| Armory | yellow | SS G | | +1 coin per **red** card in your city | 3 |
| Lighthouse | yellow | CC G | Tavern | +1 coin per **yellow** card in your city (**including itself**) | 3 |
| Arena | yellow | C S W | Brewery | +2 coins per **Wonder built** in your city | 3 |
| Palace | blue | C S W GG | | | 7 |
| Town Hall | blue | SSS WW | | | 7 |
| Obelisk | blue | SS G | | | 5 |
| Gardens | blue | CC WW | Statue | | 6 |
| Pantheon | blue | C W PP | Temple | | 6 |
| Senate | blue | CC S P | Rostrum | | 5 |

Yellow coin effects pay **once**, when the card is built, and count the city at that moment [RB p.15].

### Guilds (7 cards; 3 per game, shuffled into Age III)

[RB p.16]:
- "The city with the most X" is **either** player's city, whichever has more.
- Coins are paid once, at construction.
- VP are counted at game end. The city used for VP may differ from the city used for coins.

| Guild | Cost | On build: coins | End of game: VP |
|---|---|---|---|
| Merchants Guild ("Traders Guild" on RB p.16, §11) | C W G P | 1 per yellow card in the city with the most yellow | 1 per yellow card in the city with the most yellow |
| Shipowners Guild | C S G P | 1 per brown+grey card in the city with the most brown+grey (one city for both colours) | same count, 1 VP each |
| Builders Guild | SS C W G | — | 2 per Wonder in the city with the most Wonders |
| Magistrates Guild | WW C P | 1 per blue card (city with the most blue) | 1 per blue card (same rule) |
| Scientists Guild | CC WW | 1 per green card (city with the most green) | 1 per green card (same rule) |
| Moneylenders Guild | SS WW | — | 1 per 3 coins in the **richest** city (at game end) |
| Tacticians Guild | SS C P | 1 per red card (city with the most red) | 1 per red card (same rule) |

## 6. Science and Progress [RB p.12, p.14; FAQ]

**Symbols.** There are 7 scientific symbols. Six appear on green cards, **exactly twice each**. The 7th (Law) exists only on the Law Progress token. Names differ by source, so use IDs (numbering from [BGA]; names from [7WD] `symbol.go`):

| ID | Name ([7WD]) | Cards |
|---|---|---|
| 1 | Astrology (armillary sphere / globe) | University, Observatory |
| 2 | Law (scales) | Law Progress token only |
| 3 | Sundial | Academy, Study |
| 4 | Mortar & pestle | Pharmacist, Dispensary |
| 5 | Compass / drafting instrument | Workshop, Laboratory |
| 6 | Writing (quill/inkwell) | Scriptorium, Library |
| 7 | Wheel | Apothecary, School |

**Pairs.** Whenever you collect a **pair** of identical symbols, you may immediately take one Progress token **from the board**. Because each symbol appears only twice, each symbol gives a pair at most once, and Law can never form a pair. If the board has no tokens left, the pair gives nothing.

**Scientific supremacy.** If you hold **6 different** symbols out of 7, you win immediately. Law counts toward this.

**Progress tokens** [RB p.14]:

| Token | Effect | VP |
|---|---|---|
| Agriculture | +6 coins immediately | 4 |
| Architecture | Your future Wonders cost **2 fewer resources** (you choose which, at each build) | |
| Economy | When your **opponent** pays coins **for trading**, those coins go to you instead of the bank. You get what they actually paid after their discounts. Coin costs printed on cards are not included. | |
| Law | Counts as scientific symbol 2 | |
| Masonry | Your future **blue** cards cost 2 fewer resources (you choose which) | |
| Mathematics | At game end, 3 VP per Progress token you own, **including itself** | 3×n |
| Philosophy | — | 7 |
| Strategy | Your **future red cards** get +1 shield. It does not apply to Wonders' shields or to red cards built before you took it. | |
| Theology | Your future Wonders all have "play again". A Wonder that already has it does not get it twice. | |
| Urbanism | +6 coins immediately. You gain **+4 coins each time** you build a card for free **through a chain**. | |

## 7. Wonders (12) [RB p.17; costs from BGA and 7WD, which agree]

| Wonder | Cost | Effect | VP |
|---|---|---|---|
| The Appian Way | P CC SS | +3 coins. Opponent loses 3 coins. Play again. | 3 |
| Circus Maximus | G W SS | Discard one **grey** card of your choice from the opponent's city to the discard pile. 1 shield. | 3 |
| The Colossus | G CCC | 2 shields | 3 |
| The Great Library | P G WWW | Draw **3 at random** from the 5 Progress tokens **left out at setup**. Keep 1 and return 2 to the box. The opponent does not see them. | 4 |
| The Great Lighthouse | PP S W | Produces W **or** C **or** S each turn (does not affect trading prices) | 4 |
| The Hanging Gardens | P G WW | +6 coins. Play again. | 3 |
| The Mausoleum | P GG CC | Look through the whole discard pile and **build one card for free**. Cards removed at setup are not part of the discard pile. | 2 |
| Piraeus | C S WW | Produces G **or** P each turn (does not affect trading prices). Play again. | 2 |
| The Pyramids | P SSS | — | 9 |
| The Sphinx | GG C S | Play again | 6 |
| The Statue of Zeus | PP C W S | Discard one **brown** card of your choice from the opponent's city to the discard pile. 1 shield. | 3 |
| The Temple of Artemis | P G S W | +12 coins. Play again. | 0 |

**"7 Wonders, not one more!" [RB p.11].** Only 7 Wonders may be built in a game. As soon as either player builds the 7th, the one remaining unbuilt Wonder (8 were drafted) goes back to the box. That player permanently has fewer Wonders.

## 8. Military [RB p.12, p.14; track geometry from 7WD track.go and BGA MilitaryTrack.php]

**Track.** Number the 19 spaces from **−9 to +9** from one player's view. 0 is the centre, ±9 is each capital, and the pawn starts at 0.

**Shields.** Each shield moves the pawn 1 space toward the opponent's capital. The pawn can move back and forth.

**Zones and tokens.** The zones on each side are:

| Spaces from centre | VP to the player the pawn is advanced toward the enemy for | Looting token on that side |
|---|---|---|
| 0 | 0 | — |
| 1-2 | 2 | — |
| 3-5 | 5 | 2 coins |
| 6-8 | 10 | 5 coins |
| 9 (capital) | military supremacy: immediate win | — |

**Looting.**
- The first time the pawn **enters** a zone that still holds a token, the opponent loses that many coins (to the bank). If they have fewer, they lose all their coins. The token then goes back to the box.
- A token triggers **at most once per game**.
- One large move can pass through two zones and trigger both tokens; [BGA] steps the pawn one space at a time.

**Scoring.** At game end, only the player toward whose opponent the pawn is displaced scores military VP (2, 5 or 10). The other player scores 0.

## 9. End of an Age [RB p.11; BGAF]

- An Age ends when all 20 cards have been taken.
- "Play again" that would happen when the structure is empty is **lost** [RB p.10].
- The player with the **weaker military** chooses who starts the next Age. Weaker military means the pawn is on their side.
- **If the pawn is at 0 (tie), the printings disagree:**
  - 2015 English printing [RB p.11]: the last active player (who took the last card) **begins** the next Age.
  - Later English and French printings, quoted in [BGAF] and implemented by BGA: the last active player **chooses** who begins.
- **Recommendation:** implement the later wording (last active player chooses), and make it a config flag.

## 10. End of game, scoring and tiebreak [RB p.13]

**Immediate wins.**
- Military supremacy: the pawn reaches the opponent's capital.
- Scientific supremacy: 6 different symbols.

Both can happen in any Age, at any point during a move, including on a Wonder's or the Mausoleum's free build. Check for them after every effect.

**Civilian victory.** After Age III, add up for each player:
- Military VP (0/2/5/10).
- VP from blue, green, yellow and purple cards (Guilds are computed at the end).
- VP from Wonders.
- VP from Progress tokens (Agriculture 4, Philosophy 7, Mathematics 3×n).
- **floor(coins / 3)**.

Highest total wins.

**Tiebreak.** The player with the most VP **from blue cards** wins. If that is also tied, it is a **shared victory** (a draw).

## 11. Conflicts and printing differences

1. **Card names, Age II blue (5 VP, WW G) and Age III red (3 shields, $8).**
   - [RB p.18] (2015 English) calls the Age II card **Tribunal** and the Age III card **Courthouse**.
   - [BGA] and [7WD] call them **Courthouse** (Age II) and **Pretorium** (Age III).
   - The same word "Courthouse" therefore names *different cards* depending on the source.
   - **Use stable IDs in the Engine**, and keep display names in a separate alias table.
2. **Merchants Guild vs. Traders Guild.** [RB] uses "Traders Guild" on p.16 and "Merchants Guild" on p.18. [BGA] and [7WD] use Merchants Guild.
3. **Who starts the next Age when military is tied:** see §9.
4. **Choice production as a "discount".** [7WD] models choice producers (Forum, Caravansery, Great Lighthouse, Piraeus) as "reduce one matching resource from the cost". [BGA] models them as produced resources. These are equivalent for cost purposes, because neither counts toward the opponent's trading price.

## 12. Edge cases implementers often get wrong

1. **Trading price uses only the opponent's brown and grey cards.** It ignores yellow and Wonder production and counts double cards as 2.
2. **Removal changes trading prices.** Statue of Zeus and Circus Maximus remove a card, which lowers prices from then on. The removed card goes to the **discard pile**, so a later Mausoleum can build it.
3. **Cost minimisation is an optimisation problem.** Choice producers, Architecture/Masonry's "2 fewer resources of your choice" and per-resource prices (1-coin reserves, opponent production) interact. The cheapest payment comes from assigning each choice producer and each discount to the resources that would be most expensive to buy. Do not use a fixed priority order. [BGA] says it "will calculate and choose the most advantageous resources".
4. **Chains make the card free, coin costs included.** Urbanism's +4 applies **only** to chain builds. It does not apply to cards that are free anyway, to Mausoleum builds or to Wonders.
5. **Discard value is 2 + yellow cards *already in your city*.** The discarded card is not counted, even when it is yellow.
6. **Building a Wonder does not put the Age card in the discard pile.** Only "discard for coins" and Zeus/Circus Maximus removals add to the pile. The cards removed at setup are never in it.
7. **The 7th Wonder rule.** After 7 Wonders are built, the 8th is removed. Never offer it as a move again.
8. **"Play again" disappears at the end of an Age.** Theology does not stack with existing "play again". The extra turn starts *after* newly accessible cards are revealed. This matters for hidden information and chance nodes.
9. **Strategy** adds +1 shield only to red **cards** built after you take it. It never applies to Wonders' shields.
10. **Economy:**
    - It redirects only **trading** payments, and only what was actually paid after reserves and Customs House.
    - Coins printed on a card's cost still go to the bank.
    - It does not affect your own purchases.
11. **Looting:**
    - Each token triggers once.
    - The loss is capped at the coins the opponent has.
    - The token goes to the **box**; the coins are not gained by the attacker.
    - Retreating and re-advancing never re-triggers it.
12. **Military VP depend on where the pawn ends.** Once looted, a zone's token is gone, but its VP still count if the pawn ends there.
13. **Science.**
    - A pair grants a token from the **board** only. The Great Library draws from the **5 set-aside** tokens.
    - Law (from the board or the Great Library) can complete scientific supremacy.
    - A pair is checked when a green card enters your city by **any** route, including the Mausoleum.
14. **Supremacy ends the game in the middle of a move.** For example, a Wonder with shields plus "play again": supremacy ends the game before the extra turn.
15. **Guild "most" comparisons are separate for coins (at build) and VP (at end).** Shipowners must use one city for brown and grey combined. Moneylenders and the Builders Guild pay no coins.
16. **Lighthouse** counts itself. **Arena** counts Wonders built *at that moment*. All yellow and Guild coin payouts are one-time.
17. **Accessibility is per slot.** Age II edge cards and Age III row-3 cards are covered by **one** card. Face-down cards flip when they become accessible, not before.
18. **Age III face-down Guild cards show a Guild back.** The Observation must expose "unknown Guild" separately from "unknown Age III card".
19. **Wonder draft order.** In round 2 the **second** player picks first (1-2-1 pattern reversed). The draft's first player starts Age I.
20. **Tiebreak** counts **blue-card VP only**, not total civilian VP. An exact tie is a draw.

## 13. Open uncertainties

- **Who starts the next Age on a military tie (§9).** I could not retrieve the newer English rulebook PDF directly; the change is attested only through [BGAF] and BGA's behaviour.
- **Coin supply.** [RB] lists 31 physical coins but says nothing about running out. I found no official ruling and did not check whether [BGA] or [7WD] cap the bank. Recommend an unlimited bank.
- **How the first player is chosen** at setup is unspecified. Use random.
- **Optional choices.** [RB] says "you **may** choose" a Progress token on a pair. Whether you may decline (it is never beneficial, except for hidden-information reasons) is not addressed. Model it as mandatory if a token is available, or offer a pass. Zeus and Circus Maximus removal against an opponent with no card of that colour simply does nothing; this is common practice ([BGA]) but not explicit in [RB].
- **Symbol and chain-icon names** are not official text. They come from [BGA] and [7WD] identifiers. Use numeric IDs.
