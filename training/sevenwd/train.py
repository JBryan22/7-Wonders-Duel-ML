"""train.py (spec A7): train the network on encoded shards and export ONNX.

    uv run sevenwd-train --data runs/x/shards/gen-0003 runs/x/shards/gen-0004 --steps 300 \
        --checkpoint runs/x/net/latest.pt --out runs/x/net/gen-0004.onnx

- With an existing --checkpoint, training continues from it (network, optimiser and step count); otherwise the
  network starts from --init (another checkpoint, e.g. a sibling Run's) or random weights (--seed).
- --steps 0 with no --data exports the starting network as is: Generation 0's random network.
- The checkpoint is written at the end and every --checkpoint-minutes, atomically, and also when training is stopped
  early (Ctrl+C, or the loop's pause request through `tick`), so a stop loses no steps.
- `sevenwd-train surgery PARENT.pt --out CHILD.pt` makes an Encoding v2 (Pantheon) network from a v1 one: see
  surgery.py. Train it with `--manifest data/encoding/v2.json` on Encoding v2 shards.
- Batches are drawn uniformly from all positions of the given shards (loop.py chooses the shards: the window).
"""
from __future__ import annotations

import argparse
import os
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import numpy as np
import torch

from .export import export_onnx
from .losses import compute_losses, policy_entropy
from .manifest import Manifest, load_manifest
from .network import Network, NetworkConfig
from .shards import ShardDataset, find_shards

CHECKPOINT_FORMAT = "7wd-checkpoint"


@dataclass
class TrainConfig:
    steps: int
    batch_size: int = 1024
    learning_rate: float = 1e-3
    weight_decay: float = 1e-4
    width: int = 256
    blocks: int = 4
    seed: int = 0
    device: str = "auto"
    checkpoint_minutes: float = 5.0
    # Input blocks the network drops (a new network only; a checkpoint keeps its own). None: none for a new network,
    # and no check against a checkpoint; a tuple must match the checkpoint's.
    dropped_inputs: tuple[str, ...] | None = None


@dataclass
class TrainResult:
    model: Network
    step: int
    history: list[dict[str, float]] = field(default_factory=list)
    stopped: bool = False  # stopped early by `tick`: fewer steps than asked were taken


def pick_device(name: str) -> torch.device:
    if name == "auto":
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")
    return torch.device(name)


def save_checkpoint(path: Path, model: Network, optimizer: torch.optim.Optimizer, step: int, manifest: Manifest,
                    extra: dict | None = None):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    torch.save({
        "format": CHECKPOINT_FORMAT,
        "encoding_version": manifest.version,
        "network": model.config.to_dict(),
        "model": model.state_dict(),
        "optimizer": optimizer.state_dict(),
        "step": step,
        **(extra or {}),
    }, tmp)
    os.replace(tmp, path)


def load_checkpoint(path: Path, manifest: Manifest) -> dict:
    ckpt = torch.load(path, map_location="cpu", weights_only=True)
    if ckpt.get("format") != CHECKPOINT_FORMAT:
        raise ValueError(f"{path}: not a training checkpoint")
    if ckpt.get("encoding_version") != manifest.version:
        raise ValueError(f"{path}: Encoding version {ckpt.get('encoding_version')}, manifest is {manifest.version}")
    return ckpt


def train(
    manifest: Manifest,
    shard_dirs: list[Path],
    config: TrainConfig,
    checkpoint: Path | None = None,
    init: Path | None = None,
    log_every: int = 0,
    tick: Callable[[int], bool] | None = None,
) -> TrainResult:
    """Train for config.steps steps. `tick(step)` is called after every step (with the cumulative step count); when
    it returns True, training stops there: the checkpoint is saved and the result says `stopped`."""
    torch.manual_seed(config.seed)
    rng = np.random.default_rng(config.seed)
    device = pick_device(config.device)

    resume = load_checkpoint(checkpoint, manifest) if checkpoint and checkpoint.exists() else None
    start = resume or (load_checkpoint(init, manifest) if init else None)
    net_config = (NetworkConfig(**start["network"]) if start
                  else NetworkConfig.from_manifest(manifest, config.width, config.blocks,
                                                   tuple(config.dropped_inputs or ())))
    if config.dropped_inputs is not None and tuple(config.dropped_inputs) != net_config.dropped_inputs:
        raise ValueError(f"the checkpoint drops the inputs {list(net_config.dropped_inputs)}, "
                         f"asked for {list(config.dropped_inputs)}")
    if (net_config.input_size, net_config.policy_size, net_config.outcome_size) != (
            manifest.input_size, manifest.policy_size, manifest.outcome_size):
        raise ValueError("checkpoint sizes don't match the manifest")
    model = Network(net_config)
    if start:
        model.load_state_dict(start["model"])
    model.to(device).train()

    optimizer = torch.optim.AdamW(model.parameters(), lr=config.learning_rate, weight_decay=config.weight_decay)
    step = 0
    if resume:
        optimizer.load_state_dict(resume["optimizer"])
        step = resume["step"]

    result = TrainResult(model, step)
    if config.steps > 0:
        data = ShardDataset(shard_dirs, manifest.version, manifest.input_size, manifest.policy_size,
                            manifest.outcome_size)
        if len(data) == 0:
            raise ValueError("no positions to train on")
        last_save = time.monotonic()
        try:
            for i in range(config.steps):
                batch = data.batch(rng.integers(0, len(data), size=min(config.batch_size, len(data))))
                tensors = [torch.from_numpy(a).to(device, non_blocking=True)
                           for a in (batch.input, batch.policy, batch.legal, batch.value, batch.outcome)]
                x, policy_target, legal, value_target, outcome_target = tensors
                policy, value, outcome = model(x)
                losses = compute_losses(policy, value, outcome, policy_target, legal, value_target, outcome_target)
                optimizer.zero_grad(set_to_none=True)
                losses.total.backward()
                optimizer.step()
                step += 1
                result.history.append({**losses.as_floats(), "entropy": policy_entropy(policy, legal)})
                if log_every and (i + 1) % log_every == 0:
                    avg = {k: np.mean([h[k] for h in result.history[-log_every:]]) for k in result.history[-1]}
                    print(f"step {step}: " + " ".join(f"{k} {v:.4f}" for k, v in avg.items()), flush=True)
                if checkpoint and time.monotonic() - last_save > config.checkpoint_minutes * 60:
                    save_checkpoint(checkpoint, model, optimizer, step, manifest)
                    last_save = time.monotonic()
                if tick is not None and tick(step):
                    result.stopped = True
                    break
        except KeyboardInterrupt:
            # Ctrl+C: keep the steps taken (a step cut short is simply not counted; its update is at worst partial).
            if checkpoint:
                save_checkpoint(checkpoint, model, optimizer, step, manifest)
            raise

    result.step = step
    if checkpoint:
        save_checkpoint(checkpoint, model, optimizer, step, manifest)
    return result


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if argv[:1] == ["surgery"]:  # `sevenwd-train surgery ...`: make a Pantheon network from a base one (surgery.py)
        from .surgery import main as surgery_main
        return surgery_main(argv[1:])
    p = argparse.ArgumentParser(prog="sevenwd-train", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", nargs="*", default=[], help="shard directories, or folders searched for shards")
    p.add_argument("--out", type=Path, required=True, help="the ONNX file to export")
    p.add_argument("--steps", type=int, required=True, help="training steps to take now (0: export only)")
    p.add_argument("--checkpoint", type=Path, help="resume from / save to this checkpoint")
    p.add_argument("--init", type=Path, help="start from this checkpoint's weights when --checkpoint doesn't exist")
    p.add_argument("--manifest", type=Path, help="Encoding manifest (default: data/encoding/v1.json)")
    p.add_argument("--batch-size", type=int, default=1024)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--weight-decay", type=float, default=1e-4)
    p.add_argument("--width", type=int, default=256, help="network width (ignored when starting from a checkpoint)")
    p.add_argument("--blocks", type=int, default=4, help="residual blocks (ignored when starting from a checkpoint)")
    p.add_argument("--drop-inputs", nargs="*", help="input blocks the network never sees, e.g. derived (a new "
                                                    "network; must match a checkpoint's)")
    p.add_argument("--seed", type=int, default=0)
    p.add_argument("--device", default="auto", help="auto (CUDA if available), cpu, cuda, cuda:1, ...")
    p.add_argument("--checkpoint-minutes", type=float, default=5.0)
    p.add_argument("--log-every", type=int, default=100)
    args = p.parse_args(argv)

    manifest = load_manifest(args.manifest)
    shard_dirs = find_shards(args.data) if args.data else []
    if args.steps > 0 and not shard_dirs:
        p.error("--steps > 0 needs --data")
    config = TrainConfig(steps=args.steps, batch_size=args.batch_size, learning_rate=args.lr,
                         weight_decay=args.weight_decay, width=args.width, blocks=args.blocks, seed=args.seed,
                         device=args.device, checkpoint_minutes=args.checkpoint_minutes,
                         dropped_inputs=tuple(args.drop_inputs) if args.drop_inputs is not None else None)
    result = train(manifest, shard_dirs, config, checkpoint=args.checkpoint, init=args.init,
                   log_every=args.log_every)
    export_onnx(result.model, manifest, args.out)
    print(f"exported {args.out} (Encoding v{manifest.version}, step {result.step})", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
