# 09: Rule-options header and move-log parsing

**What to build:** The Ruleset header (start-choice, promo, expansion rejected with a clear error), the finished Move Log grammar including slot-named Moves and redacted private Reveals, and the full rule-test suite including the 20 common mistakes.

**Blocked by:** 08

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Logs with expansion= are rejected with 'not supported'
- [x] Slot-named Moves and 'reveal P2 library ? ? ?' parse
- [x] One test per common implementation mistake in the rules reference

## Notes

- Ruleset grammar: `rules base [start-choice=new|2015] [promo=messe,liberty] [expansion=pantheon,agora]`. Options come in any order, each at most once; list names are checked. `Ruleset` gained `Promos` (flags `Messe`, `Liberty`; `HasPromo`). `ToString` always writes start-choice, then `promo=` (messe before liberty) only when on, so a header round-trips. `Expansions` exists only in the parser: any expansion throws `NotSupportedException` ("... is not supported by the v1 Engine"), so a Ruleset never holds one. `MoveLog.Parse` rethrows it as a `NotSupportedException` naming the line, not a `FormatException`.
- Promos: the grammar parses, but `GameState.New` throws `NotSupportedException` while `Promos != None`. Ticket 12 removes that guard and makes `WonderPool` follow `Rules.Promos` (12/13/14 Wonders).
- Slot-named Moves: only the take-card Moves accept a slot instead of a card (`P1 build 4.2`, `P1 discard 4.2`, `P1 wonder 4.2 sphinx`). The parser resolves the slot in the current Age to its face-up card, so the `MoveEntry` holds the card and the log is written by card name. A face-down or empty slot, or a slot before Age I, is a `FormatException`. No `Move` field records that a slot was named; an expansion with a Move on an unknown card (Agora) will need one.
- Redacted Reveals: `LibraryReveal.Hidden(player)` has no tokens (`Redacted`), is written `reveal P2 library ? ? ?`, and only sets `LibraryDrawHidden`. It is checked like a real draw (right drawer, after the flips). A partly redacted draw (`? law ?`) or the wrong count is an error. Either player's draw may be redacted: the log does not know whose seat it is kept from.
- Common mistakes: `CommonMistakesTests` holds one test per item in rules-reference §12, numbered `Mistake_01` to `Mistake_20`. Some repeat checks that the topic test classes already make, so each mistake has one place to look. The reference counts rows from 1, so its "Age III row 3" is row 2 in code (slots 5-8).
- Tests: 275 Engine (47 new: the Ruleset grammar, the slot-named and redacted forms, and the 20 mistakes) and 11 Agents. The Python suite is untouched; `uv` was not on this shell's PATH, so it was not rerun here.
