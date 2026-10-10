# Expansion extension points

Type: grilling
Status: open
Blocked by: 17

## Question

v1 builds no expansion support, but it must name the places where Pantheon and Agora will plug in, so that adding them later doesn't mean rewriting the Engine. Using the findings of "Pantheon and Agora rules that affect the Engine's design", decide:
- **Ruleset:** do the expansions become Ruleset switches like the promo Wonders?
- **Game State:** what must stay open for extension, e.g. new component locations, new per-player resources, new victory conditions?
- **Moves, Pending Decisions and Reveals:** does the current model (a player-owned Pending Decision queue, card/slug-named Moves, Reveals at the moment of knowledge) cover every new choice and chance event, or do the expansions need something new?
- **Observation and hidden information:** if Agora's Conspiracies are truly private, what changes in the Observation, the search and the Agent Ladder? This has to stay consistent with "search sees only the Observation".
- **Adapters:** does the TTS Table Snapshot need new places, and does the Advisor Window need new board areas?

## Context

- Encoding: expansions arrive as a new Encoding version that adds input and policy blocks, with no reserved slots (ADR 0003, "Neural network input and output encoding").
- "Promo Wonders in the Engine, training and Encoding" introduced Ruleset switches, a "player to decide" separate from the player whose turn it is, and a queue of player-owned Pending Decisions. These are the closest precedent.
- Building Pantheon/Agora is out of scope. This ticket names the seams only.
