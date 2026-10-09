# Neural network input and output encoding

Type: grilling
Status: open
Blocked by: 06, 07

## Question

How is an Observation turned into the network's input tensor, how is the Move space encoded as a fixed policy output (with legal-move masking), what does the value head predict (win probability? also victory type?), and what network shape/size do we start with? Must be defined identically in C# and Python (see ADR 0001), and leave room for Pantheon/Agora.
