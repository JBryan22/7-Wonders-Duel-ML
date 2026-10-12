# Live checks in Tabletop Simulator (tickets 32, 33 and 34)

Everything the Tabletop Simulator Adapter does is tested automatically with fake tables, but only a real TTS table
can confirm it works with the real mod. This is one session of about 40 minutes on the Windows desktop that covers
all three tickets, in order. You will move both players' pieces yourself; single player is fine.

**Before you start**
- Close VS Code if its Tabletop Simulator extension is running (it uses the same port as the Advisor).
- Keep a note of what happened at each step: "as expected", or what you saw instead.
- Only one program can talk to TTS at a time: `tools\tts-check.cmd` and the Advisor (`tools\advisor.cmd`) never run
  together. Close one's console window before starting the other.

## Part A: reading the practice table (ticket 32, about 5 minutes)

1. Start Tabletop Simulator.
2. Copy `docs\research\tts-mod\ml-practice.json` from this repository into
   `Documents\My Games\Tabletop Simulator\Saves\`. In TTS choose *Games → Save & Load* and open `ml-practice`.
   Take a seat as **White**. Don't move anything.
3. Double-click `tools\tts-check.cmd`. A console window reads the table five times, one second apart.
4. Expected: five lines "Snapshot N: … ms round trip …", the list of places, and at the bottom
   **"MATCH: the table matches ml-practice.snapshot.json place by place."** Note MATCH or DIFFERENT and the
   round-trip times. Close the console window.

## Part B: a new game followed from the table (tickets 33 and 34, about 25 minutes)

5. In TTS, load the mod "7 Wonders Duel [ULTIMATE ed.]" as a new game (base game, no promos). Sit at **White**.
6. Double-click `tools\advisor.cmd`. If it resumes an older game, that's fine: leave it.
7. In the **Tabletop Simulator** box (under the new-game form, or on the right during a game), tick
   **Follow the TTS table**. Expected: "Connected · N ms per reading · you sit White (your TTS seat)", and
   **You sit** shows "Auto: White".
8. **Seat check.** In TTS, change your colour to Grey (spectator) from the colour menu. Expected within 2 seconds:
   "Which colour do you play? Your TTS seat isn't White or Red …". Sit at White again: the message goes away.
9. **New-game check.** On the table, set up as usual: the 5 Progress tokens face up on the board, then the mod's
   **Deal Wonders** button. Don't take a Wonder yet. Expected: **"New game detected …"** with a
   **Start the new game** button and a line like "no promo Wonders · you are P1 · current start rule". It must not
   start by itself. If the line isn't what you want, click *other settings…* and use the form instead.
   Click **Start the new game** (an older game, if there was one, is saved, not lost).
10. Expected: two buttons, "reveal first P1 …" and "reveal first P2 …". Click the one for whoever plays first (or
    just let the first player take a Wonder: then it is entered by itself).
11. Draft all 8 Wonders, deal Age I, and play about 6 turns, taking cards for both players (build into the city,
    discard to the discard tile, build a Wonder by tucking a card under it). Flip newly uncovered cards as you
    normally would. Expected: each Move appears in the Move Log by itself a second or two after the table is still.
12. **Mistake check.** Drop a card into the **wrong** player's city. Expected: a red **Desync** with two short
    lists. Move the card to the right city: the Desync disappears and the Move is entered.
13. **Coin check.** Move one coin from Red's side to White's. Expected: only a line under **Soft checks**
    ("Coins: TTS shows … expected …"), no Desync. Move it back.
14. **Restart check.** Close the Advisor's console window (the game is saved). In TTS, play 4 or 5 more turns for
    both players, flipping uncovered cards as usual. Then double-click `tools\advisor.cmd` again. Expected within a
    few seconds: "catching up" in the box, then all those Moves in the Move Log and "The table matches the Move
    Log". If it shows buttons, click the one that matches what happened; if it asks you to name a card, name it.
    Note what it asked, if anything.
15. **Reload check (short).** In TTS, *Games → Save & Load*, save the table under a new name, then load that save.
    Expected: "catching up" for a moment, then "The table matches the Move Log" with nothing new in the log.

## Part C: the unticked promo (ticket 34, about 5 minutes)

16. In the Advisor click **New game**, leave **both promo boxes unticked**, Start. In TTS, load the mod as a new
    game again (sit White) and set out the 5 Progress tokens and **Deal Wonders**.
17. Put a promo Wonder into the draft by hand: find **The Messe** card in the box area on the far left of the table
    and drag it onto the spot of one of the 4 dealt Wonders; put that Wonder aside in the box area.
18. Expected: "The Messe is in the Wonder draft on the table, but this game was started without it …" and a button
    **Enable The Messe for this game**. Click it. Expected: the Move Log's rules line now ends in `promo=messe`, a
    note says the log was replayed, and the box goes on as usual (the "reveal first …" buttons).

## What to send Claude

- For each numbered step: as expected, or what you saw instead (a screenshot helps).
- Part A: MATCH or DIFFERENT, and the round-trip times.
- If something looks wrong in Part B or C: close the Advisor's console window, double-click `tools\tts-check.cmd`
  (it says DIFFERENT, which is expected for a game other than the practice save) and leave the files it saves:
  `runs\tts-live-snapshot.json`, plus the Move Log at `%LOCALAPPDATA%\7wd\advisor\current.log`. Restart the Advisor
  with `tools\advisor.cmd` afterwards; it resumes the game.
