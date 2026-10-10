# Pantheon and Agora rules that affect the Engine's design

Type: research
Status: resolved
Assignee: Jesse Bryan
Research: `docs/research/expansions.md` (on main; branch `research/expansions`)
Blocked by:

## Question

What do the Pantheon and Agora expansions add to 7 Wonders Duel, described in the terms of our model? This is not a full rules write-up. For each expansion, find:
- **New components and where they sit:** Pantheon has gods/Divinities, Mythology and Offering tokens and a Pantheon board. Agora has Senators, the Senate chambers, Conspiracies and Decrees. Note any changes to the Age structure, setup or Wonders.
- **New Pending Decisions and Moves:** every new kind of choice, how many options each has, and who makes it (including choices during the other player's turn).
- **New chance events (Reveals):** what is shuffled or drawn, and when it becomes known.
- **New hidden information:** what one player knows and the other doesn't, especially Agora's Conspiracies and anything private in Pantheon. Does it break the "perfect information with chance" view of the base game (see "Learning algorithms for a hidden-information, chancy two-player game")?
- **New victory conditions and scoring** (e.g. Agora's political supremacy).
- **Rough size for the Encoding:** the number of new card or token identities and the new policy blocks they imply.

Sources: the official rulebooks first, then koenheltzel/bga-sevenwondersduel `modules/php/Material.php` and MC-freshman as cross-checks (see "Prior art: existing 7 Wonders Duel engines and AIs"; read-only, check the licences).

Deliverable: `docs/research/expansions.md` in the style of `docs/research/rules-reference.md`.

## Answer

Full findings are in `docs/research/expansions.md`. Sources: the official Repos rulebooks (Pantheon revised, checked against the 2016 printing; Agora 2020), cross-checked read-only against the BGA code and MC-freshman for values printed only as pictures.

**Pantheon**
- **Components:** a Pantheon board with 6 spaces and 15 Divinities in 5 Mythology decks. A Gate fills the empty space at Age II. 3 of 5 Grand Temples replace the Guilds in Age III. Mythology tokens sit on face-down Age I slots and Offering tokens (2/3/4) on face-down Age II slots. Also 2 Wonders and 3 Progress tokens.
- **New choices:** flipping a Mythology token means drawing 2 Divinities, placing 1 face down and returning 1 to the deck top (up to 12 options). From Age II, activating a Pantheon card is a new main action (cost 3–8, Gate doubled). About 14 sub-decisions come from the god effects.
- **Model breaks:** spending Offering tokens is a real choice, so payment is no longer always automatic. Zeus can destroy a face-down card, so that Move must name a slot.
- **Hidden information:** mild. The 2 drawn Divinities, the placed god and the deck-top card are private until Age II setup makes most of them public.
- **Scoring:** no new victory condition.
- **Encoding:** about +450 inputs and +250 policy outputs if choices are split into steps.

**Agora**
- **Components:** a Senate of 6 Chambers (worth 1,2,3,3,2,1 VP) and cubes. 13 Senators are mixed into the Age decks, so the structures grow to 25/25/23 slots. Also 16 Conspiracies, 16 Decrees (6 used), 2 Wonders, 2 Progress tokens and new Military tokens.
- **New choices:** an optional Conspiracy trigger at the start of each turn; recruiting a Senator; using a card to prepare a Conspiracy; 1–3 Senate actions; keeping 1 of 2 Conspiracies; draft-time choices; about 15 Conspiracy sub-decisions. Decisions also come up on the other player's turn (e.g. Decree 9 can give the opponent a Senate action).
- **Model breaks:** the non-active player can win, and duplicate cards (6 identical Conspirators) mean a Move can no longer just name a unique card.
- **Hidden information:** clearly breaks "perfect information with chance". Conspiracy hands, deck top/bottom knowledge and peeks at removed cards stay private for a long time and are leaked through play.
- **Victory:** new Political Supremacy (control all 6 Chambers). The outcome head would go from 7 to 9 classes.
- **Encoding:** about +1,000–1,300 inputs and +700–900 policy outputs.

**Unresolved between sources** (see the doc): whether Zeus takes or discards a token; whether Gate and Theater reveals are public; whether Offering tokens are public; the costs of Divine Theater and Knossos; Pantheon token slot positions. Printed diagrams couldn't be rendered, and BoardGameGeek and the Repos FAQ were blocked.
