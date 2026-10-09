# Hidden cards get their identity only when revealed

The Game State never knows what lies under a face-down card, which cards were removed at setup, or which progress tokens are unused. A face-down slot holds only "an unknown card of Age N" (or "an unknown Guild"). Its identity is fixed only when it flips, by a Reveal in the Move Log that a Chance Source supplies: a random draw from the cards nobody has seen yet in Self-Play and search, or the human or Tabletop Simulator in Advisor mode. The same holds for the setup progress tokens, the Wonder draft offers, the Great Library draw and the first player.

## Considered Options

- Deal everything at setup and have the Observation hide it. This is the usual simulator design, but search could then plan around cards it shouldn't know (strategy fusion), and Adapters, which can't know those cards either, would have to invent a fake deal.

## Consequences

- Search samples chance at the moment it happens, which is what the algorithms research requires for sound play in a perfect-information game with chance.
- Self-Play and Advisor mode share one code path; an Adapter is just another Chance Source.
- The Game State tracks the pool of unseen cards per Age, so random Reveals draw from the right pool. A card the players never see (removed at setup, or an unused Guild) never gets an identity.
