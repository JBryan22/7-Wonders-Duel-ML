# Training pipeline and two-machine workflow

Type: grilling
Status: open
Blocked by: 07, 08

## Question

How does the Self-Play → training → evaluation loop run end to end: which process generates games (C#) and in what format they're stored, how Python trains and exports ONNX, how new networks are gated/evaluated against the Agent Ladder, how experiments are tracked so the owner can see learning happen, and how the Linux (development) and Windows (GPU training) machines share code, data and models?

## Context from "Neural network input and output encoding"

Already decided there:
- Self-Play stores already-encoded examples plus Move Logs (ADR 0003).
- The heads and their target meanings are defined.

Still to settle here:
- loss weights for value, policy and outcome class;
- whether to mix the search's value estimate into the value target;
- the planned experiments: Small (256×4) vs Medium (512×6) network at equal GPU-hours, and with vs without derived features.
