# Advice presentation: reasons and the principal line

Type: prototype
Status: open
Assignee: Jesse Bryan
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
