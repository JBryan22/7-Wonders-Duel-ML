# 11: Cross-check against the C++ engine

**What to build:** An opt-in Linux harness driving indjev99's C++ engine with our Moves and Reveals, comparing legal Moves and final scores; about 10 full games frozen as scripted tests.

**Blocked by:** 09

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Zero disagreements over 10k random games
- [x] Frozen games cover military, science, civilian wins and a draw

## Notes

- **Harness:** `tools/crosscheck/driver.cpp` is our line-protocol wrapper (`names`, `new`, `do <type> <a1> <a2>`; one reply line each) around indjev99's `src/game/` only. `tools/crosscheck/build.sh` clones indjev99/7-Wonders-Duel-AI at pinned commit `01ed7a5f` into `tools/crosscheck/.cache/` (gitignored, never vendored), checks the licence is still MIT, and builds `7wd-cpp-driver` with g++ -std=c++17. `7wd crosscheck [--games N] [--seed S] [--driver PATH] [--freeze DIR]` (`Agents/CrossCheck.cs`) plays seeded random games in our Engine (`start-choice=new`, which is the C++ start-of-Age rule; base only), then feeds the same Moves and Reveals to C++, serving Reveals in whatever order C++ asks (it resolves a Wonder's choice before the flips; we flip first). Before every Move it compares the full legal-Move sets (translated to our Move words), the player to move, both players' coins and the Conflict Pawn; at the end the winner, victory type, both VP totals, blue VP, coins and pawn. One driver process per core.
- **Where it runs:** Linux only, never in push CI. This Windows desktop has no C++ compiler and Docker Desktop's VM does not start (WSL is not installed), so it ran on GitHub: `.github/workflows/crosscheck.yml` is opt-in, run from the Actions tab or by pushing a branch named `crosscheck/<anything>`; a branch run commits its report and frozen games back to that branch (`crosscheck-out/`) so the result can be read with git alone. On the Linux laptop: `tools/crosscheck/build.sh && dotnet run --project engine/src/Cli -c Release -- crosscheck`.
- **Result:** 10,000 games (seeds 0-9999) in 12.7 s on ubuntu-latest: 9,609 civilian, 365 military, 11 science, 15 draws; **zero disagreements**, so no rule fix was needed and the determinism hashes are unchanged. Negative control: with Theology's replay and the Moneylenders Guild deliberately broken, the same run reported 901 disagreements (wrong player to move, wrong final scores), so the comparison does bite.
- **Frozen games:** `engine/tests/Engine.Tests/FrozenGames/` holds 10 Move Logs written by `--freeze` (3 military, 3 science, 3 civilian including one won on blue points, 1 draw): `7wd random --seed N` games with the C++ result as a `# expect <P1|P2|draw> <victory> <VP1> <VP2>` comment. `FrozenGamesTests` replays each and checks coverage. Ticket 12/13 must keep them passing (base games are unchanged by promos).
- Random play reaches science rarely (~0.1%), so the science rules (pairs, tokens, Law) are mostly cross-checked inside civilian games through the legal-Move sets and coins. Rerun the cross-check after any rules change.
- Tests: 288 Engine (11 new: frozen games), 31 Agents (5 new: `CrossCheckTests`, the parts that need no driver: name mapping, action wording, status parsing).
