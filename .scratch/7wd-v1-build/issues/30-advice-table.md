# 30: Advice table

**What to build:** Advice on request from the MCTS Agent: badges, the compare table with whiskers and the 'about the same' marker, Think longer.

**Blocked by:** 17, 29

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Advice at 800 simulations in about 3 s on the desktop (measured: 15 ms mean, 37 ms worst over 30 positions; 80 ms in the real server incl. JIT; Think longer 3,200 in 0.2–0.3 s)
- [x] The 'about the same' marker appears for Moves within noise

## Notes

- **Code:** `Advisor/Advice.cs` (`AdviceSettings`, `AdviceSearch.Run` → `SearchOutcome`, `AdviceTable.Rank` → `AdviceRow`s, `Noise`/`WithinNoise`, Move labels) and `Advisor/AdviceService.cs` (background search, cache, auto, slow rule, `AdviceView` JSON). The server calls `AdviceService.Sync(session)` inside `Respond` after every change, so the TTS Adapters (32+) get cancel/auto for free as long as their changes go through the same path.
- **Search from the Observation only:** `session.State.Observe(session.Seat)`. Advice is only for the owner's decisions; otherwise the box says why ("it is P2's turn", "Name the revealed cards first").
- **Agent by config:** `7wd-advisor --agent mcts` (default) or `--agent neural:<onnx>[,mode=classic,...]`; `--sims N` fixes the budget (default 800, Think longer = 4×). The budget overrides any `sims=` in the spec. Other Agents are refused at startup. Ticket 35 just passes `--agent neural:<best.onnx>`.
- **Share of the search:** MCTS and classic neural use visit shares; Gumbel uses the improved policy (`PolicyTarget`), since Sequential Halving's visit counts don't rank Moves well. Win = the owner's mean result (draw ½), whiskers ±2 SE clamped to 0..1.
- **Rows:** the search's choice first (MCTS: most visited), then by share. Gap = best win − row win; "≈ same" when |gap| ≤ 2·√(se₁² + se₂²); otherwise "−x.x%" (or "+x.x%" if a less-visited Move has a higher mean). `AdviceRow`/`AdviceRowView` carry `Line` (the Move Log line), `Slot` (for card Moves) and `Visits`; ticket 31 adds the outcome bar and reason tags as new columns and the expected line (`Advice.Line` already holds the principal line; `LineStep.AfterReveal` marks chance steps).
- **Badges:** a card's badge = the sum of its Moves' shares (gold for the best Move's card); the card menu shows each action's share; decision tiles already get a badge by line lookup (`advice.shares[line]`), so ticket 31 only needs the dimmed board and the Statue pairs.
- **Background + responsiveness:** `MctsAgent.Search` / `NeuralAgent.Search` take an optional `CancellationToken` (checked between simulations; results and determinism unchanged). A new position cancels the running search. Results are cached per position (key = the saved Move Log) and budget, so undo/redo shows advice instantly; the seed is a hash of the key, so a position always gets the same advice. The page polls `GET /api/advice` every 200 ms while `status == "thinking"`.
- **"Advise by itself on my turns"** checkbox (remembered in `advice.json`): restarts the search whenever the owner is to decide. Off by default (spec: advice on request); useful with TTS.
- **Laptop rule:** two normal 800-simulation searches in a row over 3 s drop this computer's default to 400 (saved in `advice.json`, notice shown). `--sims` disables it. Every search's wall-clock time goes to the console and `advice.log` in the data folder.
- **Finding:** with MCTS at 800 simulations the whiskers are wide (±3–5%), so most of the top 5 are "≈ same" (77 of 82 rows over 23 test positions); at 3,200 they narrow and clear gaps show. Expect fewer ties with a trained network.
- **Verified in a browser:** headless Edge over the DevTools protocol on a saved Age II position: Ask → table with 4 "≈ same" rows and 3 card badges (best in gold) in 0.08 s; Think longer → 3,200 simulations, 1 "≈ same" and 3 "−x.x%"; card menu shows per-action %; entering the best Move switches the box to "it is P2's turn"; no page errors.
- Tests: Advisor 25 (10 new: noise rule; rows/ranking/≈ same consistency over 23 positions, every row's line enterable; same position → same advice; 800 sims under 3 s; tiny ONNX in Gumbel and classic and non-search Agents refused; service background/cache/Think longer; cancel on position change; owner-only + auto; slow rule + prefs + log; HTTP advice ask/poll/follow). Engine 342, Agents 94, Python 48. Determinism hashes and frozen C++ games untouched.
