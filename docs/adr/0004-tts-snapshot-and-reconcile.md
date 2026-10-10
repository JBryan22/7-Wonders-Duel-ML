# The TTS Adapter reconciles Table Snapshots against the Move Log

The Tabletop Simulator Adapter does not interpret table events. About once a second it pulls a Table Snapshot over the External Editor API, filtered in Lua so hidden card names never leave TTS. It compares the snapshot with where the current Game State says every piece should be. A difference explained by exactly one short chain of legal Moves and Reveals is appended to the Move Log. Anything else is a Desync, which is flagged in the Advisor Window and appends nothing. The Move Log stays the single source of truth, and TTS is checked evidence.

## Considered Options

- **Interpret drop and zone events from a script object added to the table.** Real play is messy: cards get picked up and put back, dragged through several zones, or flipped late. One misread event desyncs us for good, and the added object risks interfering with the mod's own scripts.
- **Push snapshots from an added object via Lua `WebRequest`.** This is slightly faster, but it changes every save, and a one-second delay doesn't matter for advice.

## Consequences

- Reconciliation is idempotent and recovers by itself: fixing the table, or correcting the Move Log in the Advisor Window, clears a Desync.
- Snapshots are reconciled only when the table is settled: nothing held, nothing moving, and two identical snapshots in a row.
- Coins, the Conflict Pawn and looting tokens are soft checks, never inputs, because the Engine computes them exactly and players handle coins sloppily.
- The "expected table" projection is the seam for a later AI-controlled seat, which would move pieces to match it and let reconciliation confirm the result.
- There is no cold mid-game join. The Advisor must run from setup, and it persists its Move Log so a restart resumes and catches up through reconciliation.
