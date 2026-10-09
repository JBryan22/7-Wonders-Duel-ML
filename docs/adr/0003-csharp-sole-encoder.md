# C# is the only Encoding implementation; a generated manifest is the contract

The Encoding (how an Observation becomes the network's input numbers, and how the policy outputs map back to Moves) is implemented once, in C#. Self-Play writes training examples that are already encoded: input vector, policy target, outcome. So Python never encodes anything. C# generates a versioned **encoding manifest** listing the version, the sizes and a name for every index (e.g. `policy[147] = wonder baths sphinx`), and Python reads it. Each ONNX model carries the Encoding version it was trained on, and C# refuses to load a model whose version doesn't match.

## Considered Options

- Implement the Encoding in both C# and Python (what ADR 0001 originally assumed). Both sides need it only if Python reads raw games, and two implementations can drift apart silently, which is a classic source of "the network isn't learning" bugs.

## Consequences

- Changing the Encoding makes stored training data unusable. Self-Play therefore also keeps the Move Logs, so data can be re-encoded under a new version.
- Expansions (Pantheon/Agora) arrive as a new Encoding version that adds blocks. Nothing is reserved in advance.
