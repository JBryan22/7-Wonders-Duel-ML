# 04: All three Ages and civilian scoring

**What to build:** Full games through Age III: Age II/III layouts, the Guild back pattern Reveal, yellow and Guild effects, chains, start-of-next-Age choice (new rule and the 2015 flag), the Result with civilian scoring and the blue-VP tiebreak.

**Blocked by:** 03

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Random full games always end with a Result
- [x] Hand-traced scoring scenarios give the expected scores
- [x] Start-player rule tests for both flags
- [x] Draw only on equal VP and equal blue VP

## Notes

- Code: `Rules/Scoring.cs` (`Result`, `ScoreSheet`, `VictoryType`, `ScoreCategory`, civilian scoring, Guild coins and VP), plus Age II/III flow in `GameState`. `GameState.LastAge` is gone; `AgeCount = 3`. The game ends with `Result` set (`IsOver` is `Result is not null`); `7wd replay` prints it.
- Slot backs: `Hidden` (-2) = unknown card of the current Age, `HiddenGuild` (-3) = unknown Guild. `UnseenPoolFor(slot)` gives the right pool per back.
- Guild back pattern: at the start of Age III the state waits for `reveal guilds 0.1 3.0 5.2` (3 distinct Age III slots, before the opening Reveal). The random Chance Source picks 3 of 20 slots uniformly.
- Start of Ages II and III: the new Age is laid out and its Reveals (pattern, face-up cards) come first, then the `ChooseStarter` Pending Decision (`P2 start me` / `P2 start opponent`), so the chooser sees the new layout. The weaker military (`ConflictPawn` != 0) always chooses; on a tie the last taker chooses (`start-choice=new`) or simply starts (`2015`, no Move logged). `Move.Card` is -1 for start Moves; `Move.TakesCard` tells them apart.
- `ConflictPawn` (positive = toward P2's capital) and `MilitaryLead(player)` exist now and feed both the start rule and military VP (0/2/5/10); it stays 0 until ticket 05 moves it (tests use `SetConflictPawn`). `WondersBuilt(player)` returns 0 until ticket 07 (Arena and Builders Guild read it); Wonder and Progress score categories are 0 until tickets 07/06.
- One-time yellow/Guild coins are counted with the new card already in the city (the Lighthouse counts itself). `Scoring.Score` can score any position, for future derived features.
- Tests: 110 Engine (adds Age II/III layouts, Guild backs, start rule under both flags, yellow/Guild coins, chains, hand-traced scores, tiebreak/draw) and 7 Agents (random invariants now over full games under both flags).
