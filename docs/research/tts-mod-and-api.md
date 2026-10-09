# Reading 7 Wonders Duel state from Tabletop Simulator

Research for ticket `02-tts-mod-and-api`. Researched 2026-10-09.

**Question:** How can an external program read the state of a 7 Wonders Duel game in Tabletop Simulator (TTS), and later act on it? This covers (1) what TTS offers, (2) what is publicly knowable about the "7 Wonders Duel [ULTIMATE ed.]" mod (Workshop id 3368311210), and (3) whether we can attach our own script without breaking the mod.

## TL;DR

- **The mod's file is public.** The Steam Web API `GetPublishedFileDetails` returns a public `file_url` for the Workshop item. The file is a TTS save in **BSON** (binary JSON). I downloaded and decoded it, so most of part (2) below comes from the **mod's actual scripts and objects**, not from guesswork. That may also make ticket 03 (export the save) mostly unnecessary for the *initial* state. A **mid-game** save is still only obtainable on Windows.
- **The mod is scripted, but it does not track game state.** It automates setup (version choice, wonder deal, Age I/II/III layout) and scores the game on a button press by scanning zones. It defines **no** zone, drop, or turn events, and TTS turns are disabled. Nothing records whose turn it is or which Moves were made.
- **Pieces are identified by name, not by ID.** Each age card, wonder, and progress token is a TTS object whose **Nickname is the card name** (e.g. `"Glassworks"`), with **tags** for its colour class (`brownNgrey`, `yellow`, `wonder`, `progress token`, …). The mod's own scoring identifies cards by `getName()`. Coins are physical `1 Gold` / `3 Gold` / `6 Gold` tokens. The military pawn is one model (`Conflict Pawn`, GUID `7ac61a`).
- **The cheapest read path needs no changes to the mod.** An external program sends **"Execute Lua Code"** (External Editor API, TCP `localhost:39999`) against the Global script (guid `"-1"`) and gets the result back on `localhost:39998`. **Event-driven** reading (onObjectEnterZone/onObjectDrop) needs our own script. That script can live on a **separate object we add**, because these events fire in object scripts too, so the mod's Global script stays untouched.
- **Big pitfall: hidden information.** Lua can read the name of a **face-down** card. Our Lua must filter on `is_face_down` (and ignore the 3 removed cards per Age), or the Advisor will cheat. See GLOSSARY "Observation".

---

## 1. What TTS offers

### 1.1 External Editor API (TCP, localhost)

Source: [External Editor API](https://api.tabletopsimulator.com/externaleditorapi/) ([doc source on GitHub](https://github.com/Berserk-Games/Tabletop-Simulator-API/blob/master/docs/externaleditorapi.md)).

- It uses **two localhost TCP connections, carrying JSON messages.** TTS **listens on 39999**. The editor (our program) **listens on 39998**, and TTS connects to it to push messages.
- Every message has a `messageID`. The same IDs mean different things in each direction.

| Direction | ID | Meaning | Fields |
|---|---|---|---|
| us → TTS (39999) | 0 | Get Lua Scripts | none. TTS replies with ID 1 **on port 39998**, not on the same socket. |
| us → TTS | 1 | Save & Play | `scriptStates[]` (`name`, `guid`, `script`, `ui`). Updates scripts **and reloads the game**. |
| us → TTS | 2 | Custom Message | `customMessage` (must be a **table**). Delivered to Lua `onExternalMessage(data)`. |
| us → TTS | 3 | Execute Lua Code | `guid`, `script`. Runs `script` in that object's context. **Global is guid `"-1"`**. |
| TTS → us (39998) | 0 | Pushing new object | `scriptStates` |
| TTS → us | 1 | Loading a new game (also the reply to "Get Lua Scripts") | `scriptStates[]` incl. `ui` |
| TTS → us | 2 | Print/Debug | `message` |
| TTS → us | 3 | Error | `error`, `guid`, `errorMessagePrefix` |
| TTS → us | 4 | Custom message from Lua `sendExternalMessage(table)` | `customMessage` |
| TTS → us | 5 | Return value of Execute Lua Code | `returnValue` |
| TTS → us | 6 | Game saved | none |
| TTS → us | 7 | Object created | `guid` |

Caveats (from the same page):
- Execute Lua Code fails ("Object reference not set…") if the target object **has no script**. Global and the mod's button objects do have scripts.
- With **Save & Play**, an omitted or empty `script`/`ui` field **deletes** that script or UI. It also reloads the table, so it must **never** be used for live reading. Use it only for deliberate installs, and even then carefully.
- If no external editor is listening on 39998, TTS falls back to its in-game editor.

What this gives us:
- **Pull:** Execute Lua with guid `"-1"` and a script like `return JSON.encode(snapshot())`. The answer arrives as message ID 5. (`JSON` is TTS's built-in helper.)
- **Push:** our Lua calls `sendExternalMessage({...})`, and we receive message ID 4.
- **Commands:** we send ID 2 custom messages and our Lua handles them in `onExternalMessage`.

### 1.2 Lua scripting inside the mod

- **Runtime.** The runtime is "based on Lua 5.2" in a "safe sandbox". The available libraries are Basic, Bit32, Coroutine, Dynamic, ErrorHandling, Math, Metatables, OS_Time (only `clock`/`difftime`/`date`/`time`), String, Table and TableIterators. There is **no `io`, no sockets, and no `require`** (the `_MOONSHARP` constant suggests MoonSharp). So Lua can reach the outside world only through `WebRequest` or `sendExternalMessage`. ([Lua in TTS](https://api.tabletopsimulator.com/lua-in-tabletop-simulator/))
- **Events** ([Events](https://api.tabletopsimulator.com/events/)):
  - These are **universal** handlers, meaning they run in Global **and** in every object script that defines them:
    - Zone events: `onObjectEnterZone(zone, obj)`, `onObjectLeaveZone`.
    - Player actions: `onObjectDrop(color, obj)`, `onObjectPickUp`, `onObjectRotate` (player-initiated only, not physics).
    - Container and lifecycle events: `onObjectEnterContainer` / `onObjectLeaveContainer` (decks count as containers), `onObjectSpawn`, `onObjectDestroy`.
    - Other: `onPlayerTurn` (requires TTS Turns to be enabled), `onExternalMessage`, `onSave` / `onLoad`, `onPlayerAction`, `onUpdate`.
  - `onObjectEnterScriptingZone` is **deprecated** in favour of `onObjectEnterZone`.
  - **Short-circuit rule:** "If a handler returns a value that triggers an optional side effect, later handlers for that same event occurrence do not run." Our handlers must therefore **return nothing**, so they don't interfere with the mod's handlers (or the other way round).
  - `onSave` "can run every ~10 seconds". Keep it fast.
  - Global-only handlers (`tryObject*`, `onZoneGroupSort`) are ignored in object scripts.
- **Objects** ([Object](https://api.tabletopsimulator.com/object/)):
  - **Identity:** `guid` / `getGUID()` is a 6-character id, "unique" (the docs don't say whether it can change). `getName()` returns the tooltip name (Nickname). There are also `getDescription()`, `getGMNotes()` (visible only to the Black/GM seat) and `memo` (free user data).
  - **Tags:** `getTags()` / `hasTag()` (tags are case-insensitive).
  - **Physical state:** `is_face_down`, `getPosition()`, `getRotation()`, `getStateId()`, `interactable`, `locked`.
  - **Containers:** `getObjects()` on a deck or bag returns sub-tables (`guid`, `name`, `index`, `memo`, `tags`, …) without spawning the contents. On a zone it returns the objects inside.
  - **Scripting:** `getData()` / `getJSON()` serialise an object. `call(fn, params)`, `getVar` / `getTable` reach into another script.
  - **Visibility:** `setHiddenFrom` / `attachHider` hide an object from particular players.
- **Zones.** Scripting zones (`ScriptingTrigger`), layout zones and hand zones exist. Per the [object tags KB](https://kb.tabletopsimulator.com/game-tools/object-tags/), a feature (zone) with no tags applies to all objects. A **tagged** zone only interacts with objects sharing a tag. The events page says "objects with tags only enter compatible zones."

### 1.3 WebRequest (Lua → our local HTTP server)

Source: [WebRequest](https://api.tabletopsimulator.com/webrequest/manager/).

- The functions are `WebRequest.get/post/put/delete/head/custom(...)`. They are callback-based (asynchronous). The callback gets an instance with `is_error`, `error`, `text`, `response_code` and `getResponseHeader()`.
- Requests go out "from the **game host's** computer only". That's fine, because the owner hosts.
- `post` sends form-urlencoded data. `put` sends UTF-8 data as `application/octet-stream`. `custom(url, method, download=true, data, headers, cb)` allows `application/json`.
- **Unverified:** whether `http://127.0.0.1:<port>` / `http://localhost` works. The docs say nothing. One community thread reports "Cannot resolve destination host" / "Invalid URI" errors against localhost ([thread](https://steamcommunity.com/workshop/filedetails/discussion/2973518625/3826415117583524358)), but the cause there is unclear. **Needs a 5-minute smoke test on Windows** before we design around it. The External Editor API needs no such test, because it is localhost by design.

### 1.4 Showing info to the player (UI/XML)

- Custom UI is XML, either Global (`UI.*`) or per-object (`obj.UI.*`). The Lua API has `UI.setXml`, `setXmlTable`, `setAttribute`, `setValue`, `show` and `hide` ([UI](https://api.tabletopsimulator.com/ui/), [intro](https://api.tabletopsimulator.com/ui/introUI/)).
- UI changes are **not immediate**. Queries return stale results until `UI.loading` is false.
- The `visibility="White|Red"` attribute restricts an element to particular seats ([attributes](https://api.tabletopsimulator.com/ui/attributes/)). We can use it so only the owner's seat sees recommendations.
- **Pitfall:** the mod already has Global XML (a `versionSelectPanel`). `UI.setXml` **replaces** the whole Global UI. Either append to `UI.getXml()` or put our panel on **our own object's UI**, which is safer.
- Simpler alternatives: `broadcastToColor(text, color)`, or highlighting the recommended card (`obj.highlightOn(color, seconds)`).

### 1.5 Limits and pitfalls (Windows)

- **Port 39998 is exclusive.** Only one program can listen there, so our Adapter and the VS Code TTS extension ([rolandostar/tabletopsimulator-lua-vscode](https://github.com/rolandostar/tabletopsimulator-lua-vscode)) or the Atom plugin **cannot run at the same time**. Options: keep the editor closed during play, or don't depend on 39998 (WebRequest instead).
- **Hidden information.** Lua sees every object's name, including face-down cards in the Age pyramid and the 3 cards removed per Age. Our snapshot code must apply Observation rules (`is_face_down`, location).
- **GUIDs are not reliable identifiers for spawned pieces.** In the mod file, the `1 Gold` template inside the left and right infinite bags has the **same GUID (`8297c8`)** in both. TTS therefore must re-assign GUIDs on spawn. Identify pieces by **name + tags + zone**. Use GUIDs only for the mod's fixed fixtures (zones, decks, pawn).
- **The mod is laid out by coordinates.** Card positions in the pyramid are hard-coded in the Age setup scripts (e.g. Age I rows at z = 10, 7.5, 5, 2.5, 0). So we can map pyramid slots by position. Players move cards by hand, though, so positions are only approximate after play.
- **Save & Play reloads the table.** Never send it during a game.
- **Saves on disk.** Saves are plain JSON in `Documents\My Games\Tabletop Simulator\Saves\`. A file-watching fallback (reading autosaves) is possible but laggy; the autosave cadence is unverified.

---

## 2. The "7 Wonders Duel [ULTIMATE ed.]" mod

### 2.1 Public facts (Workshop page + Steam Web API)

Sources: [Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=3368311210), [`ISteamRemoteStorage/GetPublishedFileDetails`](https://partner.steamgames.com/doc/webapi/ISteamRemoteStorage#GetPublishedFileDetails) for id 3368311210 (queried 2026-10-09).

| | |
|---|---|
| Author | Steam persona **`z\|fn@b`** (creator SteamID 76561197998719424) |
| Created / updated | 2024-11-18 / **2024-12-15** (last update; the save inside reports TTS `v13.3`) |
| Size | 620,973 bytes |
| Subscribers | 2,805 current (3,135 lifetime) |
| Tags | Scripting, User Interfaces, 2 players |
| Description | "**FULLY SCRIPTED**". "this started as a re-upload of a mod that got lost in the workshop". Supports the base game, **Pantheon, Agora**, the solo automa, the fan-made **Leaders**, and 3 official promos. Has working Agora + Pantheon scoring, optional **layout zones that auto-sort** built cards, a random first-player tool, and 3D Conflict/Minerva pawns. |
| Source repo | **None linked.** No public GitHub repo found (GitHub repository search for "7 wonders duel tabletop simulator" returns 0). |

**Lineage.** The script contains `#include SevenWondersDuel/...` markers (Atom-plugin style) and a `luabundle` 1.6.0 bundle (VS Code extension style). The same include paths and the `guidLookup` table also appear in the older mod **"[Scripted + Auto Score] 7 Wonders Duel + Pantheon + Agora + (Automated) Solo Mode"** (Workshop [2115173088](https://steamcommunity.com/sharedfiles/filedetails/?id=2115173088), 25k subscribers, last updated 2022-08-21). I confirmed this by downloading that file too. ULTIMATE ed. is a derivative of that mod's code. Its new features include layout zones; the string `layoutZones` is absent from the original.

### 2.2 What the mod file shows

How I got it: `POST https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/` with `itemcount=1&publishedfileids[0]=3368311210` returns `file_url` (a `cdn.steamusercontent.com/ugc/...` URL). That file is a BSON-encoded TTS save, which I decoded with a ~25-line Python BSON reader. This is the **initial (pre-setup) state** of the mod as uploaded.

**Global script**
- About 60 KB of Lua. Global XML is a version-select panel: Classic / Pantheon / Agora / Agora + Pantheon / Leaders / Solo.
- `onSave` persists `{guidLookup, takeTurnSet, currentAge, layoutZones}`.
  - `guidLookup` maps ~110 logical names to GUIDs, for example:
    - Decks: `age1Deck`=689066, `age2Deck`=5fd30c, `age3Deck`=d9d2f2, `guildDeck`=95f6a0, `wondersDeck`=ce2da4, `progressBag`=f8ce41.
    - Zones and the pawn: `leftPlayerZone`=0df52c, `rightPlayerZone`=8b840b, `gameCardsZone`=0eb93a (the pyramid area), `warToken`=7ac61a (Conflict Pawn).
  - This table is a **ready-made map of the board**. Our Lua can call `Global.call("findObject", "<name>")` or `Global.getTable("guidLookup")`.
- `currentAge` is set by the Age setup buttons (`"Age1"` …). The version (Classic/Pantheon/…) is stored in a 3D text object (`versionTextGuid`=b08aae) and read via `Global.call("getVersion")`.
- Event handlers defined: only `onLoad`, `onSave` and `onPlayerAction`, which handles flipping objects tagged `rotate flip` and returns `true` for everything else. There is **no** `onObjectEnterZone`, `onObjectDrop` or `onPlayerTurn` anywhere in the mod's 48 scripts.
- TTS **Turns are disabled** (`Turns.Enable = false`).
- **Conclusion:** the mod does **not** track Moves, turn order or live state. It only acts on button presses.

**Scripted buttons** (all `go_game_piece_black` objects with scripts):
- Setup: `Age 1/2/3 Setup Button` and `Deal Wonders Button`.
  - Age setup shuffles the Age deck and removes 3 cards face-down to z = -25.25. It then deals the pyramid at hard-coded positions, face-up/face-down by row, and adds Pantheon/Agora extras by version.
- `Score Game Button`. Its scoring:
  - Scans the two player zones and identifies cards by **name** against a built-in table, `gCards["Glassworks"] = {Color="Gray", Points=0, ...}`. The table includes Points, Coins, War (shields), science symbol and LinkIn/LinkOut chain symbols.
  - Excludes face-down objects and anything in the pyramid or bank zones.
  - Treats a wonder as **built** if its wonder zone holds >1 interactable object (the wonder card plus the tucked age card).
  - Reads the military VP from which 2/5/10 zone holds the Conflict Pawn.
  - Counts coins by summing `1/3/6 Gold` token names in the player zone.
- Also: `Activate The Great Library`, `Activate The Statue Of Liberty`, `Draw Leaders`, `Toggle Grid`, `Random 1st Player`, and a solo-automa `Take Turn` object (~40 KB of Lua).

**How pieces are represented** (base game)

| Piece | TTS representation |
|---|---|
| Age cards | `Card` objects in decks 689066 (Age I, 23), 5fd30c (Age II, 23), d9d2f2 (Age III, 20), 95f6a0 (Guilds, 7). **Nickname = English card name** (e.g. `"Shelf Quarry"`). **Tag = colour class**: `brownNgrey`, `yellow`, `green`, `blue`, `red`, `purple`. **GMNotes = layout-zone sort key**, not an ID: VP for blue, shields for red, `A1`..`E2` resource codes for brown/grey. Some yellow/purple cards carry effect text in Description. |
| Wonders | `Card` objects tagged `wonder`, Nickname = wonder name. 12 base wonders in deck ce2da4. Expansion/promo wonders sit in separate decks: Pantheon 227c33, Agora 9331fe, promos such as `Statue Of Liberty`, `The Messe`. Built status = an extra object in a per-player wonder zone (`leftPlayerWonder1..4`, `rightPlayerWonder1..4`). |
| Progress tokens | `Custom_Model` tagged `progress token`, Nickname = token name (`"Law"`, `"Economy"` …). Ten are in bag f8ce41, and setup draws 5 to the board zone `progressTokenZone`=36088b. Pantheon/Agora extras sit outside the bag. |
| Coins | `Custom_Token` named `1 Gold` / `3 Gold` / `6 Gold`, spawned from 3 **infinite bags** per side. GUIDs are not stable. Coin count = sum of names inside the player's zone. |
| Military | One `Conflict Pawn` (`Custom_Model`, GUID 7ac61a, tag `conflict pawn`) on the board token `battleTrack` d7240b. The mod only resolves its **VP band** via 6 small zones (2/5/10 VP each side). The **exact track position (−9..+9) is not modelled** and would have to be derived from the pawn's x coordinate. There are four `Military Token` objects (the 2- and 5-coin looting tokens, by Description) with their own zones (`leftPlayer2TokenZone`, …). |
| Players / seats | Two hand zones: **Red at x = −23.6 (left side), White at x = +23.6 (right side)**. The mod talks about "left"/"right" player, not colours. |
| Discard pile | **No discard zone exists** for the 2-player game. Discarded cards just go wherever the player drops them. |
| Expansions | Pantheon (god cards with scripts, mythology/offering tokens, Gate, Minerva pawn) and Agora (senators, conspiracies, decrees, influence cubes, chamber zones spawned at setup) are both present. Layout zones (14, tagged by colour, GMNotes `left`/`right`) auto-sort built cards when enabled. |

### 2.3 Only confirmable from a played save or live session (ticket 03 / smoke tests)

- **The pyramid:** where cards actually end up after setup (GUIDs of dealt cards, face-up/down), and whether players move cards exactly onto positions.
- **Discards:** where players put discarded cards, and where a card tucked under a wonder sits.
- **Seats:** whether left = Red and right = White in practice.
- **Layout zones:** what happens to card positions when they are enabled.
- **Spawned objects:** Pantheon/Agora objects spawned at version selection (e.g. Agora chamber zones are empty `""` in `guidLookup` until setup).
- **The pawn:** the x coordinate of each military-track space.
- **Saves and autosaves:** whether `onSave` state changes during play (only `currentAge` should), and the TTS autosave cadence.
- **WebRequest:** whether it can reach `http://127.0.0.1`.

---

## 3. Attaching our own script without breaking the mod

**Option A: no install, pull via Execute Lua (recommended first step).**
- The external program sends ID 3 `{guid:"-1", script:"..."}` and gets ID 5 back.
- The script should only **read**, with everything `local`, so it can't clobber the mod's globals (`current_age`, `guidLookup`, `utils`, …).
- It can call the mod's own helpers, e.g. `findObject("leftPlayerZone").getObjects()`.
- Zero changes to the mod, and nothing persists in the save.
- Cost: we poll, so we see state rather than Moves, and we must diff snapshots to infer Moves. It also occupies port 39998.

**Option B: an observer object (event-driven).**
- Add one small object of our own (e.g. a hidden, locked token) whose **object script** defines `onObjectDrop`, `onObjectEnterZone`, `onObjectLeaveZone`, `onObjectEnterContainer` and `onExternalMessage`. These are universal events, so they fire for object scripts without editing Global.
- It pushes changes out with `sendExternalMessage`, or with `WebRequest` if the localhost test passes.
- Rules for being a good guest:
  - Handlers return **nothing** (short-circuit rule).
  - Never call `UI.setXml` on Global.
  - Don't destruct or relock the mod's objects.
  - Keep `onSave` trivial.
- **How to install:**
  - Easiest: in TTS, load the mod, spawn the object, paste the script, then *Save As* a personal save (or upload a private Workshop copy).
  - Programmatic: one Execute Lua call that runs `spawnObject{...}` and then `setLuaScript(...)` on it, which installs at runtime with no reload.
  - **Avoid** Save & Play for this, since it reloads and can wipe UI.
- Risk: if the author updates the Workshop item, a personal saved copy won't pick up the update (and vice versa). The mod was last updated Dec 2024, so the risk is low.

**Option C: edit Global.** Possible, but it forks the mod's 60 KB bundled script for no benefit over B. Not recommended.

**Acting later (an AI seat):**
- The same channels can move pieces: Execute Lua or custom messages calling `setPositionSmooth`, `takeObject`, `flip`, and the mod's own buttons through `obj.call("age1Setup")` etc.
- The mod's solo `Take Turn` automa is precedent: it already moves cards by script.

## Sources

- TTS External Editor API: https://api.tabletopsimulator.com/externaleditorapi/ (source: https://github.com/Berserk-Games/Tabletop-Simulator-API/blob/master/docs/externaleditorapi.md)
- TTS Events: https://api.tabletopsimulator.com/events/
- TTS Object: https://api.tabletopsimulator.com/object/
- TTS Lua sandbox: https://api.tabletopsimulator.com/lua-in-tabletop-simulator/
- TTS WebRequest: https://api.tabletopsimulator.com/webrequest/manager/
- TTS UI: https://api.tabletopsimulator.com/ui/ , https://api.tabletopsimulator.com/ui/introUI/ , https://api.tabletopsimulator.com/ui/attributes/
- TTS object tags KB: https://kb.tabletopsimulator.com/game-tools/object-tags/
- Workshop page: https://steamcommunity.com/sharedfiles/filedetails/?id=3368311210
- Steam Web API GetPublishedFileDetails (ids 3368311210 and 2115173088), and the mod files at the returned `file_url`s (decoded BSON; not committed)
- Predecessor mod: https://steamcommunity.com/sharedfiles/filedetails/?id=2115173088
- Community report on WebRequest + localhost (secondary, unconfirmed): https://steamcommunity.com/workshop/filedetails/discussion/2973518625/3826415117583524358
- VS Code TTS extension (port-39998 consumer): https://github.com/rolandostar/tabletopsimulator-lua-vscode
