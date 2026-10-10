# 08: Wonders that ask for a choice, and the Observation

**What to build:** Statue of Zeus / Circus Maximus destroy, the Mausoleum, the Great Library private draw (a Reveal with an audience), and Observe(state, player) hiding Reveals by audience.

**Blocked by:** 05, 06, 07

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Rule tests for destroy, Mausoleum (incl. what reaches the discard pile) and Great Library
- [x] Observation never holds the opponent's Library draw
- [x] Decisions with no options are skipped silently

## Notes

- Code: choice powers in `GameState.BuildWonder` (queued after coins/shields, only if the game goes on, before `EndTurn`, so flips come first and a Theology play-again after); new Moves `destroy <card>`, `mausoleum <card>`, `library <token>` (`MoveKind.Destroy/Mausoleum/Library`; the Library token sits in `Move.Card`, read `Move.Token`). `DecisionKind.Destroy/Mausoleum/Library`; `PendingDecision` gained a third field `Wonder` (the Wonder that asked, -1 otherwise). `GameState.DestroyColor` gives the target colour (encoding's "destroy target colour").
- Destroy: compulsory; options = opponent's city cards of the colour; the card goes to the discard pile and the cached production drops (`RemoveFromCity`). Skipped silently (no decision pushed) when there is none; the Mausoleum likewise on an empty pile. Library always has options.
- Mausoleum: `Build(player, card, free: true)`: no payment, no Urbanism, all card effects (coins, shields + Strategy, science pair/win). Only discards and destroys reach the pile, never Wonder cards.
- Great Library: building it queues the `Library` decision, then `Step` asks for a private `LibraryReveal` (`reveal P1 library a b c`, `Audience` = drawer), after any flips (the reverse order is rejected). `LibraryDraw` (mask) holds it until the pick, which goes through `TakeToken`; the other 2 are forgotten. `RandomChanceSource` draws 3 from `TokensSetAside`.
- Observation: `state.Observe(player)` returns an `Observation` (`Player`, `State` = a private copy, `LegalMoves`, `SameAs`). The only redaction is the opponent's Library draw: `LibraryDraw` = 0 and `LibraryDrawHidden` = true, and then the drawer's legal picks are all box tokens nobody owns. Ticket 09's redacted `reveal P2 library ? ? ?` should simply set that hidden state. Everything else is public by construction (ADR 0002); the Determinizer (16) fills a hidden draw with 3 of `TokensSetAside`, and draft-time `UnseenWonders` / unseen card pools from the observation's `State`.
- Agents: `IAgent.ChooseMove(Observation)`; `GameRunner` passes `state.Observe(PlayerToDecide)` (one clone per decision; ticket 10 can measure it).
- Test hooks: `AddToDiscard`; `Setup.DraftFor` moved to the shared test setup.
- Tests: 228 Engine (new `ChoiceWonderTests`, `ObservationTests`) and 11 Agents (random games: every choice power occurs, options are never empty, an Observation never holds the opponent's draw and otherwise equals the state, production matches the city after destroys; token conservation now allows the one Library token from the box).
