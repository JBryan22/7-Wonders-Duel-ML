# 36: Play against the AI

**What to build:** A "Play against the AI" mode in the Advisor Window, so the owner can test the AI's strength themselves. The new-game screen picks the opponent (a Run's best network or a given Generation with the classic search, the MCTS Anchor, Heuristic, Random, or any Agent spec), its simulations, the seat (or random) and the promo boxes. The program deals every Reveal from a seeded Chance Source; the owner sees only what their seat may see; the AI plays its own decisions automatically with a short visible pause and an "AI played: …" line; advice is off by default (toggle); the result and a per-opponent tally are kept outside the repo; save/resume as for manual games; Undo takes back the owner's last Move with the AI's reply.

**Blocked by:** 29, 30

**Status:** done

Spec: `docs/spec-v1.md` (A8 Advisor Window; A1 honesty rule).

- [x] Owner's view never contains hidden information (the AI's Great Library draw, face-down cards)
- [x] AI Moves are legal and automatic, including draft picks, tokens, who starts, Wonder powers and the Library
- [x] A full game against Random completes and records the result; the tally shows it
- [x] Advice off by default, with a toggle
- [x] Save/resume; Undo = the owner's last Move with the AI's reply; rewind refused
- [x] Checked in a headless browser against pilot-1-small's best network

## Notes

- **How the owner plays:** double-click `tools/advisor.cmd` (Linux: `tools/advisor.sh`); in the browser click **New game** → **Play against the AI** → Opponent (the first entry is the Run whose best network changed last, e.g. "pilot-1-small: best network (Gen 3)"; also each Generation, MCTS Anchor, Heuristic, Random, or "Other: type an Agent spec…") → Think time (default 800 simulations) → seat P1/P2/Random → promo boxes → **Start**. Click your Moves as in manual entry; the AI answers by itself ("AI played: …" banner and in the panel, everything in the Move Log). Runs are listed from `--runs DIR`, else `SEVENWD_RUNS`, else `C:\7wd-runs` (Windows) / `~/7wd-runs` (Linux); only read. Network opponents use `neural:<onnx>,mode=classic` (ticket 26: classic beat Gumbel in Matches), CPU, 4 ORT threads, one search at a time.
- **Code:** `Advisor/AiPlay.cs` (`AiOpponent` saved as a `# advisor ai {json}` comment in the log; `AiPlayer.Choose/Describe`; `Opponents.List`; `AiGameRecord`, `TallyView`), `AdvisorSession` (`Deal`, `AiObservation`, `AiSeed`, `EnterAiMove`, `EnterOwn`, `TakeBack`), `AdvisorServer` (`NewAiGame`, `KickAi` background search, `AfterAiChange` records results, `PlayView`; `GET /api/play/opponents`, `POST /api/play/advice {on}`, `POST /api/play/retry`), `AdvisorStore` (`vs-ai.json`, `Tally`), page (`playPanel`, banner, new-game form).
- **Dealing and hidden information:** each Reveal comes from `RandomChanceSource(Mix(seed, log position))`, the AI's search seed likewise, so a saved game resumes exactly and the same Moves deal the same cards. The engine already reveals face-down cards only when they flip, so the only private Reveal is a Great Library draw: the owner's is named in the log; the AI's is logged `? ? ?` and drawn only inside `AiObservation` (a replay with the real draw, never stored). The AI decides from `Observe(aiSeat)` of that true state.
- **Undo decision:** Undo takes back the owner's last Move plus the AI's reply and the cards dealt after it (misclick fix). Each takeback is counted and stored with the result. Rewind is refused (it would let the owner peek at flips and re-try), and nothing can be undone once the game is over (the result is recorded). Because chance is seeded by log position, retaking the same Move deals the same flip: Undo can't be used to reroll luck.
- **Results:** `vs-ai.json` in the Advisor data folder (`%LOCALAPPDATA%\7wd\advisor`), next to `games/`, outside the repo; one record per finished game (date, opponent, spec, sims, seat, winner you/ai/draw, victory, both VP, takebacks, seed; deduplicated by seed). The panel and result tray show "You 2 – 1 AI (pilot-1-small Gen 3)". Abandoned games aren't counted.
- **Advice** is off at the start of every AI game ("Show advice (spoils the test)" toggle in the panel). The TTS box is hidden and TTS following is ignored during an AI game.
- **Verified in a browser:** `7wd-advisor --port 7711 --data <scratch>` driven in headless Edge over the DevTools protocol: the new-game form listed `C:\7wd-runs` (default pilot-1-small best Gen 3), started with 800 sims and random seat, 4 draft picks + 4 card Moves by clicking tiles and card menus; the AI drafted and built by itself each time ("AI played: Draft The Hanging Gardens", "Build Clay Reserve", …), advice box said off, Undo went from 23 to 21 log lines with the notice; no page errors.
- Tests: Advisor 76 (6 new `PlayAiTests`: 40 games vs random/heuristic in all rule sets and both seats with legality, owner-state = own Observation, AI's discarded Library tokens never in the view, replay/resume; seed determinism incl. resume mid-game against MCTS; own-Moves-only and takeback; opponent list from a fake runs folder; full HTTP games vs Random recorded twice with tally, advice toggle, rewind refused, undo; a Run's (tiny) network over HTTP and resume after restart), Engine 342, Agents 98, Python 66. Engine untouched: determinism hashes and frozen C++ games unchanged.
