# 13: Statue of Liberty

**What to build:** The returned-cards Reveal, liberty-keep and liberty-give, the resolution order, the opponent's token pick, and the non-active player winning.

**Blocked by:** 12

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Statue in each Age (Age III uses removed Age III cards)
- [x] Opponent picks the token from the given card's science pair
- [x] Opponent can win from the given card
- [x] Invariants hold with both promos

## Notes

- `promo=liberty` and `promo=messe,liberty` now run; `GameState.New` refuses nothing. `7wd soak` / `7wd random` take `--promo liberty` or `--promo messe,liberty`.
- Flow: `wonder <card> statue-of-liberty` queues `DecisionKind.LibertyKeep` (never skipped) → after the flips its card caused, a public Reveal `reveal returned age<N> a b c` (`ReturnedReveal`, `GameState.AwaitingReturnedCards`; drawn uniformly from `UnseenPool(Age)`, so never Guilds in Age III; the 3 cards join `Seen`) → `P1 liberty-keep <card>` → `P1 liberty-give <card>` (both owned by the Statue's owner). On the give, the third card goes to the discard pile, then the owner's card is built free.
- Resolution order (spec A4 "the owner's card fully first, including any token pick or win"): the given card is built only once the owner's card has fully resolved, including its token pick, in `FinishDecision` (`_libertyGiven`). So the owner's Law pick can win before the given card exists, and when the owner takes the last token the opponent's pair earns none. This differs from [7WD]'s FIFO, where both cards are placed before either pick. If the owner wins, the given card is never built (it is in no city or pile; the soak's card count allows for this).
- The given card is built with `Build(opponent, free: true)`: no cost, no Urbanism, the opponent's own Strategy/Guild coins apply, and its science pair queues the opponent's `ChooseToken`. This is the one decision made during the other player's turn (`PlayerToDecide != CurrentPlayer`). Supremacy from it gives `Result.Winner` = the non-active player.
- State: `LibertyOffer` (3 cards, then 2), `LibertyKept`, plus the private `_libertyGiven`. They are written to `ToBytes` only when the Liberty promo is on, so base-game determinism hashes and frozen C++ games are unchanged. Nothing is hidden: `Observe` needs no redaction.
- Windows desktop, 100k soaks: base 9.2 s (ticket 10's numbers exactly), messe 9.1 s (ticket 12's numbers), liberty 8.7 s (96,134 civilian incl. 118 draws, 3,735 military, 131 science), messe,liberty 8.5 s (96,140 / 135 / 3,715 / 145); all invariants hold. A new soak invariant: cards on offer are revealed and not yet placed. CI soaks 3k liberty and 3k messe,liberty games.
- Encoding (18): the "offered by the Statue" card location = `LibertyOffer`; `liberty-keep` / `liberty-give` heads; the Statue's Reveal is a chance node over C(n,3) unseen cards (like the Great Library). Heuristic (15) needs a Statue value.
- Tests: 321 Engine (new `LibertyTests`: the full sequence and log lines, Reveal after flips, Reveal validation, illegal keep/give, Observations, each Age with Age III never returning Guilds, the opponent's token pick, the owner's pick before the opponent's card, the last token, the opponent winning by military and by science, the owner winning first by card and by Law, Theology, no Urbanism, log write/replay over 300 two-promo games) and 36 Agents (3k soaks with liberty and both promos; the Statue drafted, built and resolved in random games, including opponent token picks).
