"""The network (spec A6): a residual MLP with a shared trunk and three heads.

    input (Encoding vector) -> Linear -> [residual block] x blocks -> LayerNorm, ReLU -> heads
    residual block:  x + Linear(ReLU(Linear(ReLU(LayerNorm(x)))))     (two fully connected layers plus a skip)
    heads:           policy logits (one per policy index, unmasked), value (tanh, "my chance to win" in [-1, 1]),
                     outcome-class logits

The sizes come from the Encoding manifest; width and depth are training config (default 256 x 4, ~1.5M parameters).
A network can drop input blocks (config `dropped_inputs`, e.g. the derived features): it multiplies them by 0 before
its first layer, a fixed mask that is part of the exported model, so C# feeds it the same Encoding v1 inputs.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass

import torch
from torch import nn

from .manifest import Manifest


@dataclass(frozen=True)
class NetworkConfig:
    input_size: int
    policy_size: int
    outcome_size: int
    width: int = 256
    blocks: int = 4
    dropped_inputs: tuple[str, ...] = ()               # input block names the network never sees
    dropped_ranges: tuple[tuple[int, int], ...] = ()   # their (offset, size) in the input vector

    def __post_init__(self):  # from a checkpoint the tuples come back as lists
        object.__setattr__(self, "dropped_inputs", tuple(self.dropped_inputs))
        object.__setattr__(self, "dropped_ranges", tuple(tuple(r) for r in self.dropped_ranges))

    @staticmethod
    def from_manifest(manifest: Manifest, width: int = 256, blocks: int = 4,
                      dropped_inputs: tuple[str, ...] = ()) -> "NetworkConfig":
        by_name = {b.name: b for b in manifest.input_blocks}
        unknown = [n for n in dropped_inputs if n not in by_name]
        if unknown:
            raise ValueError(f"no input block {', '.join(unknown)} in Encoding v{manifest.version}")
        ranges = tuple((by_name[n].offset, by_name[n].size) for n in dropped_inputs)
        return NetworkConfig(manifest.input_size, manifest.policy_size, manifest.outcome_size, width, blocks,
                             tuple(dropped_inputs), ranges)

    def to_dict(self) -> dict:
        return asdict(self)


class ResidualBlock(nn.Module):
    def __init__(self, width: int):
        super().__init__()
        self.norm = nn.LayerNorm(width)
        self.fc1 = nn.Linear(width, width)
        self.fc2 = nn.Linear(width, width)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        h = self.fc1(torch.relu(self.norm(x)))
        return x + self.fc2(torch.relu(h))


class Network(nn.Module):
    """forward(x) -> (policy_logits [B, policy], value [B], outcome_logits [B, outcome])."""

    def __init__(self, config: NetworkConfig):
        super().__init__()
        self.config = config
        w = config.width
        if config.dropped_ranges:
            keep = torch.ones(config.input_size)
            for offset, size in config.dropped_ranges:
                keep[offset:offset + size] = 0
            self.register_buffer("input_keep", keep, persistent=False)  # rebuilt from the config, not saved
        else:
            self.input_keep = None
        self.stem = nn.Linear(config.input_size, w)
        self.trunk = nn.Sequential(*(ResidualBlock(w) for _ in range(config.blocks)))
        self.final_norm = nn.LayerNorm(w)
        self.policy_head = nn.Linear(w, config.policy_size)
        self.value_head = nn.Linear(w, 1)
        self.outcome_head = nn.Linear(w, config.outcome_size)

    def forward(self, x: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        if self.input_keep is not None:
            x = x * self.input_keep
        h = torch.relu(self.final_norm(self.trunk(self.stem(x))))
        policy = self.policy_head(h)
        value = torch.tanh(self.value_head(h)).squeeze(-1)
        outcome = self.outcome_head(h)
        return policy, value, outcome


def parameter_count(model: nn.Module) -> int:
    return sum(p.numel() for p in model.parameters())
