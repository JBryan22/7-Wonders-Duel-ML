# 05: Military

**What to build:** The Conflict Pawn, military tokens and coin loss, and immediate military victory.

**Blocked by:** 04

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Rule tests for each military token and coin loss (never below zero)
- [x] Military victory ends the game immediately with victory type military

## Notes

- Code: `GameState.AddShields(player, shields)` (internal) moves the pawn one space at a time; red cards call it from `Build` after their coins. Wonders' shields (ticket 07) and Strategy's +1 on future red cards (ticket 06) should feed it too; it does not apply Strategy itself.
- Military tokens: a 4-bit mask in the state, index `GameState.MilitaryToken(victim, large)` = victim * 2 + large (P1:2, P1:5, P2:2, P2:5). `MilitaryTokenLeft(t)`, `MilitaryTokensLeft`, `MilitaryTokenCoins(t)` (2/5), `MilitaryTokenZone(t)` (attacker lead 3/6). Looting is an automatic consequence, never logged; the loss is capped at the victim's coins and the attacker gains nothing. Ticket 18's "military tokens left (4)" scalars read these bits.
- Military supremacy: the pawn reaching ±9 (`CapitalDistance`) sets `Result` at once (`Scoring.Supremacy(s, winner, VictoryType.Military)`, score sheets filled for reporting), drops extra shields, and clears the Pending Decision queue, so nothing after it in the move runs. Ticket 06 can reuse `Scoring.Supremacy` with `VictoryType.Science`; ticket 07 must check `IsOver` before granting play-again.
- `SetConflictPawn` is test setup only (no looting); a log replay does not know about it.
- `7wd replay` prints the pawn and the tokens left.
- Random play: some games now end by military; under the 2015 flag an untied military still gives the weaker side a start choice.
- Tests: 130 Engine (adds 20 military rule tests) and 8 Agents (random games end in a civilian or military Result; pawn stays on the track and tokens never return over 2k games).
