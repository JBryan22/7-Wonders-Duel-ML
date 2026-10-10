# Pantheon and Agora rules that affect the Engine's design

Type: research
Status: open
Assignee: Jesse Bryan
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
