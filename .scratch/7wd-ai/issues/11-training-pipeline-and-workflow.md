# Training pipeline and two-machine workflow

Type: grilling
Status: open
Blocked by: 07, 08

## Question

How does the Self-Play → training → evaluation loop run end to end: which process generates games (C#) and in what format they're stored, how Python trains and exports ONNX, how new networks are gated/evaluated against the Agent Ladder, how experiments are tracked so the owner can see learning happen, and how the Linux (development) and Windows (GPU training) machines share code, data and models?
