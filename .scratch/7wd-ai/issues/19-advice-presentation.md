# Advice presentation: reasons and the principal line

Type: prototype
Status: resolved
Assignee: Jesse Bryan
Prototype: branch `prototype/advice-presentation` → `prototype/advice-presentation.html` (open it, switch variants with ?variant=A|B|C|D and scenarios with the tabs)
Blocked by:

## Question

The Advisor Window shows advice as % badges on cards, plus a top-3 panel with each Move's win % and a "short reason" (see "Manual-entry Adapter: how it looks and behaves"). How should the reasons and the extra detail look and behave?
- **What a short reason is built from:** the expected victory type (the outcome-class head), the change in win % against the next-best Move, derived features the Heuristic already computes (e.g. "denies opponent's 2nd science pair", "+3 shields"), or some combination of these.
- **The principal line:** show the search's expected next few Moves or not, how deep, and where (inline, or on hover/expand).
- **Uncertainty:** how a close call between two Moves (e.g. 51% vs 50%) is shown, so the owner doesn't over-trust a tiny difference.
- **Pending Decisions other than taking a card:** how advice looks for a token pick, a draft pick, a Statue keep/give pair or a destroy.

Prototype rough variants on top of the Board variant (branch `prototype/manual-entry-adapter`) for the owner to react to.

## Context

- The Gumbel AlphaZero Agent can expose per-Move visit shares, a value (win %), the outcome-class distribution and a principal line ("Agent Ladder and target algorithm", "Neural network input and output encoding").
- Advice is given on request, not continuously.

## Answer

Resolved by prototype on 2026-10-09. The owner compared four variants on a rebuilt Board (an Age II pyramid) across six kinds of decision: Badges + reason chips, Coach sentence, **Compare table**, and Tiers with no numbers. **Variant C (Compare table) wins.**

- **Advice panel:** a table of the top Moves (up to 5), best first. Each row shows:
  - the Move and its share of the search;
  - its win % as a bar with ±2-standard-error whiskers and a 50% guide;
  - the gap to the best Move, written as "≈ same" when it is inside the combined noise (2·√(se₁² + se₂²)), otherwise as "−x.x%";
  - a stacked bar of the 7-class outcome head (how the game is likely to end; the colours carry a legend);
  - every reason tag for that Move.
- **Short reasons are tags built from derived-feature changes** (e.g. "completes your ✒ pair: you pick a Progress token", "+2 shields: pawn moves to 3", "uncovers School for them"), plus the outcome bar. The win % and the gap carry the "how much better". The reasons are computed by the same derived-feature code the Heuristic uses.
- **The expected line:** shown for the hovered or clicked row (the best Move by default), both as a list under the table and as numbered markers on the board's cards (filled for you, outlined for them). It runs through chance steps, which are shown as "face-down card revealed". Moves without enough search depth show "no line" rather than a guess.
- **Close calls:** shown by the error whiskers plus "≈ same". There is no separate tie bracket or verdict word.
- **Other Pending Decisions** (Progress token, Wonder draft, Statue keep/give, destroy): the options appear as tiles above the dimmed board with % badges, and they are ranked in the same table. The Statue is shown as 6 keep/give pairs, with the pair's share equal to the product of its two steps.
- **The % badges on the board's cards stay** (as in "Manual-entry Adapter: how it looks and behaves"), with the top card in gold.
- **Layout:** the table needs a wider side panel (~560 px) than the original 380 px.

**Rejected variants:**
- A (Badges + reason chips): close to C but hides detail behind expanders.
- B (Coach sentence): reads well but hides the numbers and the alternatives.
- D (Tiers, no numbers): safest against over-trusting a tiny gap, but too coarse.

**Accepted as shown, not debated:** the line continues past chance steps; the Statue is shown as pairs; up to 5 rows. These are easy to change once the Advisor Window exists.
