# 17: A Mythology token's Divinity is placed before the uncovered cards are revealed

**What to build:** Owner ruling 2026-10-26 (spec-pantheon R11): when a turn uncovers a face-down Age I card holding a Mythology token, that player takes the token, draws the top 2 Divinities and places one face down (the other back on top of its deck) **before** the uncovered card(s) are flipped. Until now the Engine flipped first (`reveal 3.2 quarry`, then `reveal P2 divinities greek ? ?`, `P2 divinity ? 4`), so the AI chose its Divinity after seeing the new cards. Rulebook p. 8: "at the end of your turn … you must first place that token in front of yourself"; the token lies on top of the face-down card, so which one it is shows without flipping. Engine (Pantheon module only), log grammar, Advisor (manual entry, advice, Play against the AI), TTS following, and old logs (the owner's game in progress) all follow the new order.

**Blocked by:** none

**Status:** done

Spec: `docs/spec-pantheon.md` (R11, P-A3 step 2, P-A5), `docs/research/expansions.md` §2 "Taking a token".

- [x] Spec: R11 owner ruling row, setup step 2 and the P-A5 note; expansions.md "Taking a token"; `docs/tts-live-checks.md` step 21
- [x] Engine: the token's draw Reveal and `divinity` decision come before the flips; base game unchanged (base determinism hashes, FrozenGames, C++ cross-check identical)
- [x] Pantheon determinism hashes re-pinned
- [x] Observation, Determinizer, legal Moves and every Agent (heuristic, MCTS, PIMC, neural, including the trained `gen-0017.onnx`) handle a placement while the uncovered cards are still face down
- [x] Advisor: manual entry, advice tiles, Play against the AI and TTS following in the new order; fixtures and tests updated
- [x] Old-order logs still load (the owner's `current.log` verified, backed up first)
- [x] Training code / Encoding v2: nothing to change
- [x] Whole C# suite, Pantheon soak and Python tests pass

## Notes

- **Engine.** New module hook `ExpansionModule.HoldsFlips` (Pantheon: the front Pending Decision is `PlaceDivinity`). `GameState.SlotsToReveal` is empty while it holds, and the new `GameState.FlipsHeld` says the flips are waiting behind it; `ApplySlotReveal` refuses a flip then ("The Mythology token's Divinity is placed before the uncovered cards are revealed"). Everything that already asked "SlotsToReveal == 0?" (Step, `RandomChanceSource`, the Advisor's pickers, redaction, the reconciler) now does the right thing unchanged. Base game: no module, so not one line of base behaviour changes (base pinned hashes, FrozenGames and the C++ cross-check pass untouched).
- **Orderings decided:**
  - *Two tokens from one turn:* both draws and placements (slot order), then the flips.
  - *Play-again Wonders:* the token is still taken "before anything else (including play again)"; the play-again turn starts after the placement(s) and the flips.
  - *Offering tokens (Age II):* no decision; already taken at end of turn, so also held before the flips (now asserted in a test).
  - *Zeus on a face-down card holding a token:* `reveal discarded <card>` (the discarded card itself), the token (an Offering token is just held; a Mythology token via the Divine Theater in Age I gets its draw and placement), then the flips of the cards the discard uncovered.
  - *A Wonder whose power the base game resolves mid-turn seeing the uncovered cards* (Great Library, Statue of Zeus, Mausoleum, Statue of Liberty …): the base game flips them before that decision, which is unchanged; the token is still taken as the turn ends, with those cards already face up. Rare, and changing it would mean changing the base game's order.
- **Hash re-pins (Pantheon only, `DeterminismTests`):** logs `69AFB330…EAEB`, states `288D98AE…1B78`, the pinned seed-0 game now 126 lines, "P2 wins (civilian)". The Random Chance Source now draws the Divinities before the flipped cards, so random Pantheon games differ from before.
- **Old logs.** `MoveLog.Parse(text, out order)`: in a Pantheon log, a flip line that arrives while a placement holds the flips is held and applied right after the placement, so the game's log comes out in the new order; `order` maps each entry to its old position so `AdvisorSession.Load` moves the AI's secrets (`# advisor ai-secret`) with their lines. A held flip still pending when the log ends is dropped (it is asked again after the placement). New-order logs are never changed. Loading does not rewrite `current.log`; the Advisor saves the new order with the next entry. The 4 `PantheonScenarios/*.log` and `tts/fixtures/pantheon-age2.log` were rewritten in the new order.
- **Owner's game in progress:** backed up to `%LOCALAPPDATA%\7wd\advisor\current.log.before-token-order` (identical copy). A copy loads with this build: 45 lines, last line `P1 discard guard-tower`, flips upgraded at 4 tokens. The game now waits for **your** Roman draw (you uncovered 1.0 with the Roman token): name your 2 Divinities and place one *before* flipping 1.0 and 1.1. If the cards are already flipped on the TTS table, the Advisor waits for the placement and then reads both.
- **Advisor.** Placing tiles say "Do this before flipping the uncovered cards."; a flip entered too early is refused with a plain reason ("Place your Divinity first …" / "P2 places their Divinity first ('P2 divinity ? <space>') …"). Play against the AI: the AI places its Divinity while the uncovered cards are still face down (also in the true game).
- **TTS.** The reconciler reads "placement, then flip" as one chain. If the table shows the uncovered card flipped but no Divinity placed yet, it now **waits** ("Place your Divinity on the Pantheon board …" / "Waiting for P2 to place their Divinity …") instead of a Desync. Also fixed a gap the new random games exposed: Anubis unbuilding a Wonder that is rebuilt before the table is read (Anubis is plausible when the Wonder's old card lies on the discard pile). `TtsPantheonTests`: the recorded snapshot re-recorded at 53 Moves (50 crossed a Gate the 8-Move catch-up can't read in one snapshot), and 3 more one-Move games so the Divine Theater's Reveal is still followed from the table.
- **Training / networks.** Nothing in `training/` or Encoding v2 changes. `C:\7wd-runs\pantheon\net\gen-0017.onnx` (trained with the old order) was checked read-only: it places Divinities in held positions (it just sees the uncovered cards still face down, which Encoding v2 already shows as face-down accessible slots).
- **Tests (all pass):** Engine 475 (was 472; new: random games never flip between a draw and its turn's placements; old-order logs load, redacted and named, with a cut log and a bad flip), Agents 130 (was 126; new `PantheonTokenOrderTests`: heuristic, MCTS, PIMC and a v2 network at held placements), Advisor 115 (was 108; new `TokenOrderTests`: hand entry for both seats/placers, TTS waiting and following, an AI game saved in the old order resumes with its secrets). Pantheon soak 20,000 games: all invariants hold. Python 89 passed.
- **For the orchestrator:** restart the owner's Advisor (Release build) so it runs this code; until then the running Advisor still expects the old order.
