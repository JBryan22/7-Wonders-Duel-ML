# 09: Rule-options header and move-log parsing

**What to build:** The Ruleset header (start-choice, promo, expansion rejected with a clear error), the finished Move Log grammar including slot-named Moves and redacted private Reveals, and the full rule-test suite including the 20 common mistakes.

**Blocked by:** 08

**Status:** ready-for-agent

Spec: `docs/spec-v1.md`.

- [ ] Logs with expansion= are rejected with 'not supported'
- [ ] Slot-named Moves and 'reveal P2 library ? ? ?' parse
- [ ] One test per common implementation mistake in the rules reference
