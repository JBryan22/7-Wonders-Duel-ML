# 18: Network input/output format and manifest

**What to build:** Encoding v1 in C# per spec A6, 7wd manifest, the committed manifest and its test.

**Blocked by:** 13

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] Every legal Move maps to a unique policy index and back
- [x] Input unchanged when only hidden cards change
- [x] Manifest test fails on any change

## Notes

- Code: `Engine/Encoding/NetworkEncoding.cs` (`Version = 1`, `Encode(observation, Span<float>)` allocation-free, plus `Encode(state, me, span)` for search states copied from an Observation; `PolicyIndex(Move)`, `MoveAt(index)`, `LegalMask(observation, Span<bool>)`, `ValueTarget`, `OutcomeClass`, `InputBlocks/InputNames`, `PolicyBlocks/PolicyNames`, `OutcomeClasses`) and `Engine/Encoding/EncodingManifest.cs`. Named `NetworkEncoding` to avoid clashing with `System.Text.Encoding`. CLI: `7wd manifest [--out PATH]`. Committed manifest: `data/encoding/v1.json` (format `7wd-encoding-manifest`: `version`, `input {size, blocks[{name, offset, size, description}], names}`, `policy {size, masking, blocks, names}`, `heads {policy, value (tanh), outcome {classes}}`). Python (ticket 19) should check `input.size`/`policy.size`/`heads.outcome.size` and that blocks tile the vectors.
- Sizes: **input 2,417, policy 1,509, outcome 7**. Policy blocks exactly as spec A6 (wonder index = card × 14 + wonder; destroy indexes the 13 brown/grey cards in card order); every policy name is the Move as the Move Log writes it (`wonder baths sphinx`).
- "Me" = the Observation's player (always the player to decide for training/search); `turn.me-to-decide` and `turn.my-turn` say whether that holds and whether it is my turn (they differ for the opponent's token pick after a Statue gift).
- Deviations from the A6 table (all within "exact field order fixed in code"): cards-elsewhere has 8 locations, not 6 — added `statue-kept` (while liberty-give is pending) and `statue-given` (until it is built; new public `GameState.LibertyGiven`, no state change), since otherwise those cards vanish from the input. **No "I started the Age" bit**: the state doesn't record it and adding it would change the determinism hashes; `cards-left-in-age` and `my-turn` were added instead. Turn block adds `library-draw-hidden`. Unseen cards = never named and still revealable (Age ≥ current; Guilds before Age III or while a Guild back remains).
- Scaling: coins /20 (cap 40), military lead /9 (−1..1), cards left /20, derived features divided per kind (production 4, choice producers 4, price 8, symbols 6, pairs 6, VP 60, Wonders built 4, open chains 8, capital distance 18); all recorded in block descriptions.
- Tests: 342 Engine (11 new `EncodingTests`: every index ↔ Move round trip and Move Log name; legal Moves unique and masked exactly over 300 random games incl. promos, every Move kind seen; input fully written and in range; no peeking via two Great Library draws; Determinizer worlds encode like their Observation; opening-of-Age-I values; unseen sets; outcome/value targets; blocks tile; committed manifest equals generated; JSON shape), 65 Agents. Determinism hashes and frozen C++ games unchanged. Python untouched (uv not needed).
- Changing the Encoding: the manifest test fails; regenerate with `dotnet run --project engine/src/Cli -- manifest --out data/encoding/v1.json` and bump `Version` if any index changes meaning.
