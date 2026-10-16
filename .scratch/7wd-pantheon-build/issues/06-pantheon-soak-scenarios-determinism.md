# 06: Pantheon soak, scenarios and determinism

**What to build:** `7wd soak/random --expansion pantheon` (with each promo setting), the invariants of P-A10, hand-traced Pantheon scenarios as scripted games, pinned Pantheon determinism hashes in CI, `StatePrinter` and `7wd replay` support, and a Pantheon line in `7wd bench`.

**Blocked by:** 05

**Status:** done

Spec: `docs/spec-pantheon.md` (P-A10), on top of `docs/spec-v1.md`.

- [x] 100k Pantheon games per promo setting, all invariants hold; victory-type mix recorded in Notes
- [x] At least 3 hand-traced scenarios with hand-worked scores pass
- [x] Pantheon hashes pinned and equal on Linux and Windows (CI)
- [x] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

- **Soak** (`Agents/Soak.cs`): `RulesFor`/`Run`/`CheckGame` take `Expansions` (base calls unchanged); `7wd soak --expansion pantheon` and `7wd random --expansion pantheon` (with any `--promo`). Pantheon games also check `CheckPantheon` after every entry: each Mythology's 3 Divinities are in its deck, on a space, in the draw, waiting for `theater-order`, or activated, never in two places; the Gate in play exactly from Age II; 5 Mythology tokens (all held after Age I), <= 3 Offering tokens, none held twice or both held and on a slot; face-down spaces only until `reveal pantheon`; Astarte 0..7; Enki's tokens owned by nobody; Minerva on the track; the Snake on a green card in a city. Card bookkeeping now counts Zeus's discards and Isis-tucked given cards. **The P-A10 Observation invariant (no private Pantheon knowledge in the Observation) is left to ticket 07** (Observe is not Pantheon-aware yet, so the soak's existing check "Observation equals the state" still holds and passes).
- **Laptop 100k Pantheon soaks, all invariants hold** (civilian incl. draws / military / science): none 96,472 (142) / 3,234 / 294; messe 96,509 (131) / 3,204 / 287; liberty 95,862 (118) / 3,741 / 397; messe,liberty 95,986 (115) / 3,646 / 368. Science wins are ~3x the base rate (Ishtar, Nisaba, Enki). Wall time is noisy on the laptop (6-21 s per 20k games).
- **Engine bugs the soak found (fixed, with rule tests):** (1) the Divine Theater removed the chosen card from the wrong deck position in every deck but the Greek one (`RemoveFromDeck` used an absolute index as a relative one); `The_theater_takes_the_chosen_card_out_of_any_deck` (5 cases). (2) With the Statue of Liberty, Anubis then rebuilding the Statue in the same Age asked for 3 more "returned" cards and crashed the chance source when the Age had too few unseen cards. Decision: only 3 cards are removed per Age, so a same-Age rebuild returns nothing (new hook `ExpansionModule.TakeReturnedCards`, `PantheonState.ReturnedCardsAge`, Pantheon bytes only; spec P-A4 Anubis bullet updated); `A_statue_of_liberty_rebuilt_after_anubis_in_the_same_age_has_no_cards_left_to_return`. Also `StatePrinter` no longer crashes on a one-card draw, and prints the Gate/Theater waiting for their Reveal and Zeus's face-down target.
- **Hand-traced scenarios** (`Engine.Tests/PantheonScenarios/*.log`, annotated with running coin totals; `PantheonScenarioTests`, the working in comments): scripted games where every other turn discards (2 coins, no yellow cards, nobody produces, so trading is 2/resource). `divinity-points` (Aphrodite, Astarte's 7 coins, Tanit, free Temples: P1 39 - P2 34), `war-minerva-mars` (Minerva stops Mars before her space, looting 2 and 5, chains, P1 military win; sheets 18 / 15), `gate-hades-enki-zeus` (Gate at double cost taking Aphrodite, Hades building the Theater, Enki's Philosophy, Zeus: 31 - 24), `offerings-astarte-neptune` (Offering tokens paying, Neptune's coin loss hitting the treasury not Astarte: 26 - 20). Scores were worked by hand from the logs before comparing; all four matched the Engine first time. Plus a printer test over 150 random Pantheon games (all promo settings) and replay-to-same-text.
- **Determinism:** `DeterminismTests.Random_pantheon_games_give_pinned_move_logs_and_game_states` (200 games, seeds cycle promo settings via `(Promos)(seed / 2 % 4)` and both start rules) and `One_pinned_pantheon_game_reads_the_same_on_every_os`; hashes taken on Linux, CI checks Windows. Base pinned hashes untouched.
- **Bench:** `7wd bench` has a 4th line, "Game State only (Pantheon)". Laptop, 3 s: GameRunner 16.8k, +Observe 19.0k, state only 23.5k, Pantheon state only 15.0k games/s (0.73 us/decision vs 0.60 base).
- **Base unchanged:** determinism hashes, `FrozenGames/`, Encoding v1 manifest pass untouched; base 100k soak 96,502 civilian (152 draws) / 3,395 / 103; messe,liberty 96,140 (135) / 3,715 / 145; all invariants hold.
- **Tests:** 451 Engine (12 new: 6 scenario/printer, 5 Theater decks, 1 Statue rebuild), 105 Agents (4 Pantheon soak settings at 2k games, 2 Pantheon determinism), 83 Advisor; Python 73 passed, 3 skipped.
- **For 07+:** a Theater-chosen Divinity sits in `PendingDecision.Wonder` (not in a deck or `ActivatedBy`) until `theater-order`; the Determinizer must keep it. `Soak.CheckGame`'s Observation check should become Pantheon-aware in 07.

