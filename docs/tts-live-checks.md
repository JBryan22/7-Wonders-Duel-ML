# Live checks in Tabletop Simulator (tickets 32, 33 and 34; Pantheon: Part D, ticket 14)

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

## Part D: Pantheon (Pantheon ticket 14, about 45 minutes)

The Advisor now follows a Pantheon game from the table (Pantheon ticket 13). Everything was tested on fake tables built
from the mod's own file, but a few things can only be confirmed at the real table: where the mod really puts the Pantheon
board's spaces, that TTS reports the Mythology and Offering tokens' pictures the way the Advisor expects, what your own
hand shows, and how you naturally handle the Divinities. You play both sides; take your time, there is no clock.

**How to look at the table:** close the Advisor's console window, double-click `tools\tts-check.cmd`. It says
DIFFERENT (it compares with the practice save; that's expected) and lists every piece by place, for example
`pantheon space 3 (from the left): face-down greek card`, `pyramid 1.0 token: roman`, `divinities White: zeus`,
`tokens Red: offering-3`, `hand: zeus`. It also saves the table to `runs\tts-live-snapshot.json`. Restart the Advisor with
`tools\advisor.cmd` afterwards (it resumes the game).

19. **Setup.** In TTS load the mod as a new game and choose **Pantheon** in its version panel. Sit at **White**. In the
    Advisor click **New game**, tick **Pantheon**, seat P1, Start, and tick **Follow the TTS table**. Expected: "Connected",
    then the usual first-player buttons once you deal the Wonders. Draft as usual (the Sanctuary and the Divine Theater
    are in the Wonder pile now).
20. **Age I and the Mythology tokens.** Press the mod's **Age I Setup**. Expected: the Move Log gets the Age I cards and a
    `reveal myth-tokens 1.0 … 3.4 …` line naming the 5 tokens the mod laid on the structure. Run `tts-check` once now and
    note whether the 5 tokens show as `pyramid 1.0 token: …`, `pyramid 1.2 …`, `pyramid 3.0 …`, `pyramid 3.2 …`,
    `pyramid 3.4 …` with the right Mythology (Greek torch, Phoenician ship, Mesopotamian winged bull, Egyptian ankh,
    Roman eagle). If a token shows as `loose: …` or not at all, note where it lies.
21. **Your own draw.** Play until **you (White)** uncover a card with a Mythology token. Move the token into your area
    (next to your city, as with Progress tokens), then drag the **top 2 cards** of that Mythology's deck (on the right of
    the table) into **your hand**. Expected: `reveal P1 divinities <mythology> <card> <card>` appears by itself. Then put
    one **face down** on an empty space of the Pantheon board and the other back **face down on top of its deck**.
    Expected: `P1 divinity <card> <space>` (if both cards left your hand at once, the Advisor offers two buttons: click
    the card you placed). **Only then flip the uncovered card(s)** (owner ruling 2026-10-26: the Divinity is placed before
    the uncovered cards are revealed); expected: `reveal <slot> <card>` after the placement. If you flip first, the Advisor
    waits for the placement ("Place your Divinity on the Pantheon board …") and then reads both.
22. **The opponent's draw.** Do the same for Red, but drag Red's 2 cards into **Red's hand** (the hand area on the left;
    you can do this from your seat). Expected: the log says `reveal P2 divinities <mythology> ? ?`, and after you place one
    face down, `P2 divinity ? <space>`: the Advisor never names Red's cards. Run `tts-check` while the 2 cards are in
    Red's hand: they must show as `hand: …` **without** a card name. Note any card name you see there.
23. **The spaces.** Before Age II, check with `tts-check` that each placed card shows as
    `pantheon space N (from the left): face-down <mythology> card` with N counting from Red's end, and that space numbers
    in the Advisor's Pantheon panel match (the panel counts from P1's capital: with you as P1 at White, its space 1 is
    the rightmost). Note any card that shows as `loose` or `storage` instead.
24. **Age II.** Put the Gate face up on the empty space and press **Age II Setup** (it turns the Divinities face up and
    deals the Offering tokens). Expected: `reveal pantheon …` and `reveal offerings 1.0 … 1.2 … 1.4 …` lines, plus
    `reveal enki …` if Enki is in the Pantheon (the mod lays its 2 Progress tokens on Enki by itself).
25. **Activating.** For each player, activate a few Divinities: pay, and move the card to that player's **Divinity board**
    (the long board below your city, with 4 card spots). Expected: `activate <divinity>` (and `offer …` when Offering tokens
    were spent: put a spent token back in the box area on the far left, or in the Offering bag, not on the table).
    Then do the effect on the table as you naturally would and note what the Advisor enters:
    - **Baal** (move one of Red's brown/grey cards into your city), **Hades** (a discarded card into your city),
      **Isis** (a discarded card tucked under one of your Wonders), **Anubis** (a built Wonder's card back to the discard
      pile), **Ra** (one of Red's unbuilt Wonders into your 5th Wonder spot, above your row 1), **Zeus** (a card of the
      structure to the discard pile), **Enki** (keep one of its 2 tokens in your area, the other back in the bag),
      **Nisaba** (put the Snake on one of Red's green cards), **Minerva** (put her pawn on a Military track space),
      **Neptune** (remove 2 Military tokens from the board), **the Gate** (lay the top card of each deck face up next to
      its deck, then move the one you choose to your Divinity board and put the others back face down).
    - Expected: each is entered by itself, or as buttons when the table can be read two ways (Neptune: which token was
      discarded and which applied; Minerva: if she stands between two spaces). Note anything entered wrongly or not at all.
26. **The Divine Theater** (if a player builds it): take the whole deck you choose into **your hand**. Expected:
    `theater-deck …` and `reveal P1 theater …`. Put the card you choose on your Divinity board and the 2 others back on the
    deck: the Advisor offers two buttons for which went on top (the table can't show it). Click the right one.
27. **Age III.** Press **Age III Setup**. Expected: `reveal temples …` naming the 3 slots with the **Grand Temple backs**;
    `tts-check` shows them as `face-down temple card`.
28. **Coins.** Astarte's coins (lying on Astarte) must not be counted as treasury: after activating Astarte, the
    "Soft checks" box must not complain about coins because of them.

**What to send Claude for Part D:** for each step 19–28, as expected or what you saw instead, plus the `tts-check` listings
from steps 20, 22 and 23 (copy the console text, or leave `runs\tts-live-snapshot.json`), and the Move Log
(`%LOCALAPPDATA%\7wd\advisor\current.log`). The positions most likely to need a correction are the Pantheon spaces (step
23), the tokens on the structure (step 20) and the Divinity boards (step 25): they were computed from the mod file, not
measured on a live table.

