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
The ranked series of progressively stronger Agents used to measure strength, each rung a baseline for the next.

**Match**:
A fixed set of games between two Agents, played in seed-paired games with seats swapped so luck cancels out, used to compare their strength.
_Avoid_: Series, tournament, benchmark

**Anchor**:
The fixed non-neural Agent that neural networks are periodically matched against, so their strength stays on the shared Agent Ladder scale.
_Avoid_: Baseline (and don't call it the reference: that is the Reference below)

**Reference**:
A stronger fixed Agent than the Anchor (the same MCTS with 20× the simulations), rated on the Agent Ladder and matched alongside the Anchor once networks outgrow it, so progress stays measurable; the plateau test follows whichever of the two still has headroom.
_Avoid_: Strong anchor, second anchor

**Encoding**:
The versioned mapping between an Observation or a Move and the numbers a neural network reads and outputs.
_Avoid_: Featurization, tensor format

**Self-Play**:
Games in which Agents play against themselves to generate training data.

**Generation**:
One trip around the training loop: the current network plays a batch of Self-Play games, a new network is trained on recent games, and it is measured in Matches.
_Avoid_: Iteration, epoch, round

**Run**:
One training effort from one frozen setup, made of successive Generations; it can be paused and continued, and a changed setup is a new Run, never an edit.
_Avoid_: Experiment (when the whole effort is meant), session

**Game Record**:
The permanent record of one Self-Play game: its Move Log, the search's preferred Moves at each searched decision, and the result.
_Avoid_: Replay, episode, sample

**Advisor**:
The mode in which an Agent recommends Moves to a human who executes them.
_Avoid_: Assistant, helper

**Advisor Window**:
The screen where the owner sees the current game as a clickable board with the Advisor's recommendations, fed by any Adapter and correctable by hand.
_Avoid_: Dashboard, board (when the screen is meant)

**Adapter**:
A plug-in that connects a place where the game is played (manual entry, Tabletop Simulator) to the canonical Game State and Move Log.
_Avoid_: Bridge, integration, connector

**Table Snapshot**:
What an Adapter reads from a physical or virtual table at one moment: where each piece lies by place (pyramid slot, city, Wonder, discard pile, …), already stripped of anything the owner may not see. Evidence about the table, not a Game State.
_Avoid_: Board state, table state, snapshot (alone)

**Desync**:
The condition in which a Table Snapshot cannot be explained from the Move Log; it is flagged until the table or the Move Log is corrected.
_Avoid_: Conflict, mismatch, error (when this condition is meant)

**Catch-up**:
The deeper reconciliation the TTS Adapter runs after the Advisor restarts, starts following, starts a new game, or TTS loads a game, until the table and the Move Log match: the table may be several Moves ahead of the saved log.
_Avoid_: Resync, cold join (joining a game already under way, which the Advisor doesn't do)

**Reveal**:
A Move-Log entry recording a chance outcome at the moment it becomes known, such as the identity of a card flipped face up, the progress tokens set out, the Wonders offered in the draft, or who plays first. Each Reveal has an audience: both players, or only one (a private Reveal, such as a Great Library draw).
_Avoid_: Draw, deal, flip (when the logged outcome is meant)

**Chance Source**:
Whatever supplies Reveals: a random generator in Self-Play and search, or the human or table in Advisor mode.
_Avoid_: Dealer, RNG (when the role is meant)

**Determinizer**:
What search uses to turn an Observation into one sampled Game State it cannot rule out, filling every unknown (face-down cards, the opponent's private Reveals) with a consistent guess.
_Avoid_: Sampler (alone), guesser

**Pending Decision**:
The kind of choice the Game State is waiting on next (for example: take a card, pick a progress token, destroy an opponent's card, choose who starts the next Age); every Move resolves exactly one. Each belongs to the player who must make it, usually but not always the player whose turn it is.
_Avoid_: Phase, sub-turn, prompt

**Ruleset**:
The rule options a game is played under, such as the start-player rule, which promo Wonders are in the Wonder pool and which expansions are on; fixed at setup and recorded at the head of the Move Log.
_Avoid_: Variant, mode, settings

**Wonder pool**:
The Wonders shuffled for the draft at setup: the 12 base Wonders plus any promo Wonders the Ruleset adds.
_Avoid_: Wonder deck
