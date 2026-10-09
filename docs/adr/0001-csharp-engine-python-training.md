# C# for engine and self-play, Python only for training

Self-play search needs a fast Engine, and nearly all ML training tools are Python. We split the stack: the Engine, search, Self-Play and Adapters are C# (.NET, runs on Windows and Linux; the owner's strongest language), and Python/PyTorch only trains the network, which is exported to ONNX and run from C# via ONNX Runtime (CUDA on the Windows training box).

## Considered Options

- All-Python: simplest and best for learning, but a Python Engine + search is ~50–100× slower, turning days of Self-Play into weeks.
- Rust Engine with Python bindings: fastest and common in hobby AlphaZero projects, but means learning Rust and ML at once.

## Consequences

Two languages, with an ONNX model file as the contract between them. The Encoding lives only in C#, and Python reads a generated manifest ([ADR 0003](0003-csharp-sole-encoder.md)); this replaces the earlier "defined identically on both sides" line.
