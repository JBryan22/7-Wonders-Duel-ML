# Manual-entry Adapter: how it looks and behaves

Type: prototype
Status: resolved
Assignee: Jesse Bryan
Blocked by: 06
Prototype: branch `prototype/manual-entry-adapter` → `prototype/manual-entry-adapter.html` (open it, switch variants with ?variant=A|B|C|D)

## Question

How should the manual-entry Adapter look and behave — a terminal UI, a local web page, or something else — for entering setup reveals and each Move quickly, seeing the current Game State, asking the Advisor for a recommendation, and fixing mistakes? Prototype rough versions to react to.

## Answer

Resolved by prototype on 2026-10-09. The owner compared four variants: Terminal, **Board**, Log-first and Wizard. New glossary term: Advisor Window.

**Variant B (Board) wins, and it becomes the Advisor Window: one local web page shared by every Adapter.** Moves can come from Tabletop Simulator automatically or from the owner's clicks. Either way, the owner sees recommendations in this one format.

- **Board:** the current Age's layout as a clickable pyramid. Accessible cards are outlined. Each city and its Wonders are shown below, with coins.
- **Entering a Move:** click an accessible card, then Build / Discard / Wonder X (only the player's unbuilt Wonders are listed).
- **Reveals:** a card that becomes accessible while face down pulses. Nothing else can be entered until it is named from a filterable picker of the unseen pool. Revealing a card already on the table is rejected.
- **Advice:** shown on request.
  - Each card gets a % badge (its share of the search), with the top choice highlighted.
  - A side panel shows the overall win %, then the top 3 Moves, each with its share and a short reason.
- **Fixing mistakes:** ↶ Undo (removes the last Move and any Reveals after it), or click any Move Log line to rewind to it. Both truncate the Move Log and replay it.
- **The Move Log is visible** in the side panel and is the source of truth (state = replay of the log).

**Relationship to Tabletop Simulator.** TTS is expected to feed the Advisor Window: setup Reveals and Moves arrive automatically, and the board updates by itself. If TTS misses or garbles something, the owner corrects it on the same board by clicking. Manual entry is the fallback and the corrector, not a separate app.

**Seeding comes from TTS, not screen capture.** The External Editor API reads the board exactly by object name (see "Reading the game from Tabletop Simulator"). Screen capture was considered and rejected: a free camera, zoom, occlusion and stacked coins make it unreliable, and it would mean weeks of computer-vision work.

**Rejected variants:**
- Terminal: keyboard-only, with no visual board for advice.
- Log-first: editing raw log lines is powerful but error-prone during a live game.
- Wizard: a phone-sized step flow. Its "Recommended … I did that" framing is worth borrowing as a detail.

**Handed on to "Tabletop Simulator Adapter architecture":**
- how TTS pushes into the Advisor Window: live events vs snapshots, and the transport;
- what happens when TTS and a manual correction disagree;
- joining a game mid-way;
- whether any advice is also shown inside TTS (optional; the Advisor Window is primary).
