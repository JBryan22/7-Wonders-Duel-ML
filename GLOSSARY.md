# 7 Wonders Duel Player

An AI that learns to play 7 Wonders Duel through self-play and advises a human player at a real table (Tabletop Simulator first).

## Language

**Game State**:
The complete, canonical description of a 7 Wonders Duel game at one moment, including information hidden from the players.
_Avoid_: Board, snapshot

**Observation**:
The part of the Game State one player is entitled to see; the only thing the AI may decide from during real play.
_Avoid_: View, visible state

**Move**:
A single legal decision by a player (e.g. take a card and construct it, discard it for coins, or use it to build a Wonder).
_Avoid_: Action, turn

**Move Log**:
The ordered sequence of Moves and Reveals from setup to the present; replaying it reproduces the Game State.
_Avoid_: History, transcript

**Engine**:
The rules implementation: given a Game State, it lists legal Moves and applies them.
_Avoid_: Simulator, rules engine

**Agent**:
Anything that chooses a Move from an Observation — random, heuristic, search-based or neural.
_Avoid_: Bot, player, AI (when a specific chooser is meant)

**Agent Ladder**:
The ranked series of progressively stronger Agents used both to measure strength and as the learning path.

**Self-Play**:
Games in which Agents play against themselves to generate training data.

**Advisor**:
The mode in which an Agent recommends Moves to a human who executes them.
_Avoid_: Assistant, helper

**Adapter**:
A plug-in that connects a place where the game is played (manual entry, Tabletop Simulator) to the canonical Game State and Move Log.
_Avoid_: Bridge, integration, connector

**Reveal**:
A Move-Log entry recording a chance outcome at the moment it becomes known, such as the identity of a card flipped face up, the progress tokens set out, the Wonders offered in the draft, or who plays first.
_Avoid_: Draw, deal, flip (when the logged outcome is meant)

**Chance Source**:
Whatever supplies Reveals: a random generator in Self-Play and search, or the human or table in Advisor mode.
_Avoid_: Dealer, RNG (when the role is meant)

**Pending Decision**:
The kind of choice the Game State is waiting on next (for example: take a card, pick a progress token, destroy an opponent's card, choose who starts the next Age); every Move resolves exactly one.
_Avoid_: Phase, sub-turn, prompt
