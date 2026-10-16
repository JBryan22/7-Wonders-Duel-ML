# 03: Activating the Pantheon and paying for it

**What to build:** The `activate <divinity|gate>` main action from Age II, its costs (per space, doubled for the Gate, −2 with the Sanctuary), the `offer <tokens>` decision (R8), Astarte's coins (R7), and the Divinities with no decision: Ishtar, Astarte, Tanit, Aphrodite, Mars. The Sanctuary Wonder.

**Blocked by:** 02

**Status:** done

Spec: `docs/spec-pantheon.md` (P-A4), on top of `docs/spec-v1.md`.

- [x] Rule tests: each cost case; affordability with coins + Astarte + Offering tokens; `offer` skipped when holding none; overpaying allowed; treasury spent before Astarte; military loss never touches Astarte coins; Ishtar completes a science pair; Mars moves the pawn and can win
- [x] Activating an impossible Divinity is allowed and does nothing
- [x] Base unchanged: determinism hashes, `FrozenGames/`, base 100k soak numbers, Encoding v1 and its manifest test (spec-pantheon P-A1)

## Notes

- **Moves and decision:** `MoveKind.Activate` (`Move.Activate(d)`, Divinity or Gate in `Card`, read with `.Divinity`) and `MoveKind.Offer` (`Move.Offer(mask)`, one bit per Offering index, `.Offerings`); `DecisionKind.Offer` (after `PlaceDivinity`; `PendingDecision.Wonder` = the Divinity being activated, which stays on its space until paid). Log: `P1 activate aphrodite`, `P1 activate gate`, `P1 offer 2+4` (values, smallest first; parsed in any order), `P1 offer none`.
- **Flow** (`PantheonModule`, "activating the Pantheon"): `AddTakeCardMoves` lists `activate` from Age II for every face-up card the player can pay with treasury + Astarte + all held Offering values. Applying it pushes `Offer` only if the cost > 0 and the player holds a token, else pays at once. Offers: every held subset whose value + spendable coins covers the cost (overpaying allowed, no change), in mask order. `Activate` spends the tokens, then `GameState.Spend` (treasury first, then Astarte, R7), empties the space (stays empty), sets `PantheonState.ActivatedBy`, applies the effect, then `GameState.FinishDecision` (now internal) ends the turn unless a decision (Ishtar's token pick) or a win followed. `PantheonModule.ActivationCost(s, player, d)` (public static): space cost, x2 for the Gate, then -2 with the Sanctuary built (`SanctuaryDiscount`), min 0; `OfferingValue(mask)`.
- **Effects:** Tanit +12 treasury; Astarte +7 to `PantheonState.Astarte` (`AstarteCoins(p)`); Mars `AddShields(2)` (no Strategy; can win; Poliorcetics applies); Ishtar `GainSymbol(Law)` (now internal); Aphrodite and Astarte's coins left score in new `ScoreCategory.Divinities` (after `Temples`). **Every other Divinity and the Gate can already be activated and paid for but does nothing** (`activate` of an impossible effect is legal and does nothing, per the FAQ); tickets 04/05 add their effects in `PantheonModule.Activate` after the no-decision effects, before `FinishDecision` (push the decision and let the queue drive the turn). Enki activated now leaves its tokens on `EnkiTokens` (04 should take one and box the other).
- **New seams** (`ExpansionModule`, all behind null checks): `ExtraSymbols(s, player, symbol)` (in `GameState.SymbolCount`: Ishtar), `ExtraCoins` / `SpendExtraCoins` (Astarte). `GameState.SpendableCoins(p)` = treasury + extra (= `Coins` in base); build/Wonder affordability and payment now use `SpendableCoins`/`Spend` (base arithmetic identical). Coin losses (military, Wonders, Poliorcetics) still touch only `_coins`. Agents/Heuristic still read `Coins` (ticket 08 should use `SpendableCoins`).
- `PantheonState` gained `ActivatedBy[2]` (`Activated(p)`) and `Astarte[2]`, copied/compared/written (Pantheon bytes only); the state printer shows `P1 activated: astarte (Astarte's coins: 7)`.
- **Base unchanged:** determinism hashes, `FrozenGames/`, Encoding v1 manifest pass untouched. Laptop base 100k soak: 96,502 civilian (152 draws) / 3,395 / 103; messe,liberty 96,140 (135) / 3,715 / 145; all invariants hold. Bench within noise of HEAD (~12-13k games/s state-only).
- **Tests:** 409 Engine (18 new in `PantheonActivationTests`: costs per player/space and Gate, the Sanctuary, Age I refusal and no card taken, offer skipped, covering subsets and overpaying, partial offering, affordability with coins + Astarte + tokens, Astarte spent after the treasury for activations and cards, military loss vs Astarte, Tanit/Aphrodite, Ishtar's pair and supremacy, Mars and its win, impossible Nisaba, log round trip, 150 random games with activations/offers replaying, state printer), 83 Advisor, 99 Agents; Python 73 passed, 3 skipped.
