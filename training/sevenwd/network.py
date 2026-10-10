"""The network (spec A6): a residual MLP with a shared trunk and three heads.

    input (Encoding vector) -> Linear -> [residual block] x blocks -> LayerNorm, ReLU -> heads
    residual block:  x + Linear(ReLU(Linear(ReLU(LayerNorm(x)))))     (two fully connected layers plus a skip)
    heads:           policy logits (one per policy index, unmasked), value (tanh, "my chance to win" in [-1, 1]),
                     outcome-class logits

The sizes come from the Encoding manifest; width and depth are training config (default 256 x 4, ~1.5M parameters).
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

    @staticmethod
    def from_manifest(manifest: Manifest, width: int = 256, blocks: int = 4) -> "NetworkConfig":
        return NetworkConfig(manifest.input_size, manifest.policy_size, manifest.outcome_size, width, blocks)

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
        self.stem = nn.Linear(config.input_size, w)
        self.trunk = nn.Sequential(*(ResidualBlock(w) for _ in range(config.blocks)))
        self.final_norm = nn.LayerNorm(w)
        self.policy_head = nn.Linear(w, config.policy_size)
        self.value_head = nn.Linear(w, 1)
        self.outcome_head = nn.Linear(w, config.outcome_size)

    def forward(self, x: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        h = torch.relu(self.final_norm(self.trunk(self.stem(x))))
        policy = self.policy_head(h)
        value = torch.tanh(self.value_head(h)).squeeze(-1)
        outcome = self.outcome_head(h)
        return policy, value, outcome


def parameter_count(model: nn.Module) -> int:
    return sum(p.numel() for p in model.parameters())
