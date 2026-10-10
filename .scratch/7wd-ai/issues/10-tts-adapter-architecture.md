# Tabletop Simulator Adapter architecture

Type: grilling
Status: resolved
Assignee: Jesse Bryan
Blocked by: 02, 03, 06

## Question

How does the TTS Adapter get the Game State out of the "ULTIMATE ed." mod and turn table changes into Moves — what runs inside TTS (Lua), what runs outside (C#), how they talk, how recommendations are shown back to the player in TTS, how it copes with unscripted/manual piece movement and desyncs, and what it must expose so an AI-controlled seat can be added later?

## Context from "Manual-entry Adapter: how it looks and behaves"

The Advisor Window (variant B, a local web page with a clickable board) is where the owner sees recommendations, whatever the source of the Moves. The TTS Adapter's job is to feed it: setup Reveals and Moves arrive automatically, and the owner corrects any misses by clicking. Advice inside TTS itself is optional.

Still to settle here:
- the transport between TTS and the Advisor Window;
- live events vs snapshots;
- reconciling a manual correction with TTS;
- joining a game mid-way.

## Context from "Solution structure and Engine testing strategy"

- The Lua scripts for the mod live in a top-level `tts/` folder.
- The C# side of the Adapter plugs into the `Advisor` project, which serves the Advisor Window as plain HTML/JS.

## Context from "Capture a mid-game TTS save and verify unknowns on Windows"

- **Both transports work:** Lua `WebRequest` reaches a localhost server, and External Editor API pulls work, but send results back through `sendExternalMessage` as JSON strings (no Execute Lua return value without `returnID`, and nested tables are dropped).
- **Placement:** the board layout (pyramid grid with covering shown by height, city columns, Wonder rows with face-down tucked cards, the discard deck on a tile, the removed-card decks) is recorded in that ticket's answer, with the save `docs/research/tts-mod/ml-practice.json` and `probe.ps1` available for testing.
- **Autosaves** every 5 minutes are too slow to serve as a live feed.

## Answer

Resolved by grilling on 2026-10-09. Recorded in [ADR 0004](../../../docs/adr/0004-tts-snapshot-and-reconcile.md). New glossary terms: **Table Snapshot** and **Desync**.

**Snapshot and reconcile, not event interpretation.** The Move Log is the single source of truth, and TTS is checked evidence.
- The Adapter computes the **expected table** from the Game State: where each card, Wonder and progress token should be, by place (pyramid slot, city, Wonder, discard pile) and whether it is face up.
- It compares that with the Table Snapshot by place, not by coordinates.
  - **No difference:** nothing happens.
  - **Explained by exactly one chain** of up to about 4 legal Moves and Reveals (for example, construct a card and then pick the progress token it earned): the chain is appended.
  - **Explained by several chains:** the Advisor Window offers them as one-click choices.
  - **Otherwise:** a **Desync**. It is flagged in the Advisor Window with a list of mismatches, and nothing is appended. It clears itself once the table is tidied or the Move Log is corrected by clicking.
- A Reveal the table can't show is asked for in the Advisor Window's picker, the same as in manual entry. Examples are a card nobody flipped, or the owner's Great Library draw kept face down.

**Transport: pull, with zero changes to the mod.** About once a second, the C# Advisor sends a read-only Lua script through the External Editor API (`:39999`). The script returns the Table Snapshot as a JSON string through `sendExternalMessage` (`:39998`). No script object is added to the table and the save is untouched. Side effect: TTS code-editor plugins can't run while the Advisor is connected. The Lua lives in `tts/`, and a data table maps TTS object names to card slugs.

**When to reconcile:** only when the table is settled: nobody is holding an object, nothing is moving, and two consecutive snapshots are identical. Otherwise the Adapter waits silently.

**Hidden information is filtered inside TTS.** The Lua reports a card's name only if the card is face up or in the discard pile (discards are public by the rules, though the mod stacks them face down). Face-down pyramid cards, removed cards, unused Wonders and undealt decks are reported only as nameless pieces. This also handles the Statue of Liberty: once the players flip the 3 removed cards face up, they appear in the snapshot, so the mod's broken button doesn't matter.

**Inputs and checks:**
- Cards, Wonders and progress tokens are inputs.
- Coins, the Conflict Pawn and the looting tokens are **soft checks only**: a mismatch shows a gentle note ("TTS shows 7 coins, expected 9"), never a Desync, because the Engine computes them exactly.

**Advice inside TTS: none in v1.** TTS highlights are visible to every player, and the Advisor Window already shows everything (second monitor or alt-tab).

**No cold mid-game join in v1.** The Advisor must be running from setup. It saves the Move Log to disk after every entry, so a restart reloads the log and catches up with the table through normal reconciliation if the gap is unambiguous. "Start from a position" was rejected: it would be a second way to create a Game State that every test would have to cover.

**Owner's seat** is auto-detected from the host's TTS player colour. If the host isn't seated, the Advisor Window asks, and the owner can always override it there. Both players' Moves are tracked either way, and advice is given for the owner's seat.

**AI-controlled seat seam:**
- The expected-table projection is a named, tested part of the TTS Adapter.
- The Adapter contract has two directions: **observe** (table → Move Log, built in v1) and **act** (execute a Move at the table), which is declared but not implemented.
- A later AI seat would move the pieces to match the expected table through the same External Editor API channel and let reconciliation confirm the result.

**Defaults assumed, not separately discussed:** a fresh setup on the table (a new Age I layout with the Wonder draft out) is offered in the Advisor Window as "new game detected, start?" rather than started automatically. Execute Lua with `returnID`, held-object detection and the cost of 1 Hz polling are implementation checks for the build plan, not open decisions.
