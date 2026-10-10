"""Training losses (spec A7): policy cross-entropy (weight 1) against the improved policy over legal Moves, value MSE
(weight 1) against the result z, outcome-class cross-entropy (weight 0.25)."""
from __future__ import annotations

from dataclasses import dataclass

import torch
import torch.nn.functional as F

POLICY_WEIGHT = 1.0
VALUE_WEIGHT = 1.0
OUTCOME_WEIGHT = 0.25


@dataclass
class Losses:
    total: torch.Tensor
    policy: torch.Tensor
    value: torch.Tensor
    outcome: torch.Tensor

    def as_floats(self) -> dict[str, float]:
        return {k: float(v.detach()) for k, v in self.__dict__.items()}


def masked_log_softmax(logits: torch.Tensor, legal: torch.Tensor) -> torch.Tensor:
    """Log-probabilities over legal Moves only: illegal logits are set to -inf before the softmax."""
    return logits.masked_fill(~legal, float("-inf")).log_softmax(dim=-1)


def policy_loss(logits: torch.Tensor, target: torch.Tensor, legal: torch.Tensor) -> torch.Tensor:
    """Cross-entropy -sum(target * log p) over legal Moves, averaged over the batch. Targets are 0 off the mask."""
    log_p = masked_log_softmax(logits, legal).masked_fill(~legal, 0.0)
    return -(target * log_p).sum(dim=-1).mean()


def compute_losses(
    policy_logits: torch.Tensor,
    value: torch.Tensor,
    outcome_logits: torch.Tensor,
    policy_target: torch.Tensor,
    legal: torch.Tensor,
    value_target: torch.Tensor,
    outcome_target: torch.Tensor,
) -> Losses:
    p = policy_loss(policy_logits, policy_target, legal)
    v = F.mse_loss(value, value_target)
    o = F.cross_entropy(outcome_logits, outcome_target)
    total = POLICY_WEIGHT * p + VALUE_WEIGHT * v + OUTCOME_WEIGHT * o
    return Losses(total, p, v, o)
