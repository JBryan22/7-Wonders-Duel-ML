# Promo Wonders in the Engine, training and Encoding

Type: grilling
Status: open
Blocked by: 15

## Question

The owner sometimes plays with the promo Wonders (Statue of Liberty, The Messe), so the Agent must handle them whenever they are in the Wonder pool. How do they fit in?
- **Engine:** how is the Wonder pool chosen at setup? Is it a game option such as "base 12" or "base 12 + promos"? How do any new Move types or Pending Decisions they introduce get modelled?
- **Training:** should Self-Play include them in some, most or all games, so the network learns them without weakening base-game play?
- **Encoding:** the encoding was decided for 12 Wonders, with a policy indexed by card and Wonder and expansions arriving as new Encoding versions (ADR 0003). Do the promos go into Encoding v1 directly or arrive as v2?
- **Adapters:** how do the Advisor Window picker and the TTS reader (`Statue Of Liberty` and `The Messe` cards are already in the mod) handle them?

## Context

- "Neural network input and output encoding" sized the policy at about 1,140 moves for 12 Wonders.
- "Capture a mid-game TTS save and verify unknowns on Windows": in the mod, both promo Wonders sit set aside at x ≈ −76.7 when they are not used.

## Context from "Promo Wonder rules: Statue of Liberty and The Messe"

- **Rules:** see `docs/research/promo-wonders.md`.
- **Proposed Moves:**
  - `messe <card>` (2 to 6 options);
  - `liberty <keep> <give>` (6 options).
- **Proposed Reveal:** `reveal returned age<N> a b c`, a new chance event.
- **Game State changes:**
  - the player who must decide can differ from the player whose turn it is;
  - Pending Decisions are queued and can belong to either player;
  - the non-active player can win immediately.
- **Ruleset:** one flag per promo is recommended. The Go engine uses one switch for both.
- **TTS:** the mod's Statue button cannot be trusted. The Adapter must see the 3 revealed cards some other way, or have them named in the picker.
