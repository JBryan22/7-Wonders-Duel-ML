"""Network surgery (spec-pantheon P-A7): turn an Encoding v1 network into an Encoding v2 (Pantheon) network.

    uv run sevenwd-train surgery runs/main/net/latest.pt --out pantheon-start.pt [--onnx pantheon-start.onnx]
    uv run sevenwd-train surgery runs/main/net/gen-0012.onnx --out pantheon-start.pt    # any Generation's network

The parent is a training checkpoint (.pt) or an exported network (.onnx). A Run keeps only one checkpoint
(net/latest.pt, its newest network) but every Generation's ONNX, so the best Generation's network is operated on from
its .onnx: the ONNX file holds the network's weights under the same names (its initializers) and its shape in the
metadata, which is all surgery needs (the child gets a fresh optimiser either way).

Encoding v2 is Encoding v1 unchanged followed by appended Pantheon blocks, for both the inputs and the policy. So the
operated network is the parent with a wider first layer and a longer policy head:

- the trunk, the final norm, the value and outcome heads are copied as they are;
- the first layer (stem) keeps the parent's weights on the v1 input columns; the new Pantheon columns start at 0;
- the policy head keeps the parent's rows for the v1 policy indices; the new Pantheon rows (weights and bias) start
  at 0, a uniform prior over the new Moves until training teaches it otherwise;
- dropped input blocks (e.g. `derived`) stay dropped: v1's blocks keep their names and offsets in v2.

In a base game every Pantheon input is 0 and every Pantheon Move is masked, so the operated network's value, outcome
and v1 policy logits equal its parent's. `operate` checks exactly that on random base-shaped inputs before it returns
(and C# checks it again on real base-game positions through ONNX: Agents.Tests SurgeryTests).

The result is a checkpoint (step 0, a fresh optimiser, the parent recorded under "surgery") for a Pantheon Run's
`init`, or for `sevenwd-train --init ... --manifest data/encoding/v2.json`.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import torch

from .export import export_onnx
from .manifest import Manifest, default_manifest_path, load_manifest
from .network import Network, NetworkConfig

# The check's tolerance. Copied weights and zero columns give the same sums, but a wider matrix product may add them
# in another order (BLAS blocking), so the outputs agree to float32 rounding (~2e-6 on a 256x4 network), not bit for
# bit. A wrong copy is off by orders of magnitude more.
RTOL, ATOL = 1e-4, 1e-5


class SurgeryError(ValueError):
    """The parent can't be operated: wrong Encoding, or the new Encoding doesn't extend the parent's."""


def check_extends(parent: Manifest, child: Manifest) -> None:
    """`child` must be `parent` followed by appended blocks: every parent input and policy index keeps its name, and
    the outcome classes are the same."""
    if child.input_size <= parent.input_size or child.policy_size < parent.policy_size:
        raise SurgeryError(f"Encoding v{child.version} doesn't extend v{parent.version}")
    if child.input_names[:parent.input_size] != parent.input_names:
        raise SurgeryError(f"Encoding v{child.version}'s first {parent.input_size} inputs differ from v{parent.version}'s")
    if child.policy_names[:parent.policy_size] != parent.policy_names:
        raise SurgeryError(f"Encoding v{child.version}'s first {parent.policy_size} policy indices differ from "
                           f"v{parent.version}'s")
    if child.outcome_classes != parent.outcome_classes:
        raise SurgeryError("the outcome classes differ")


def operate(parent: Network, parent_manifest: Manifest, child_manifest: Manifest, check: bool = True) -> Network:
    """The operated copy of `parent` for `child_manifest` (see the module docstring). The parent is not changed."""
    check_extends(parent_manifest, child_manifest)
    pc = parent.config
    if (pc.input_size, pc.policy_size, pc.outcome_size) != (
            parent_manifest.input_size, parent_manifest.policy_size, parent_manifest.outcome_size):
        raise SurgeryError(f"the parent network's sizes aren't Encoding v{parent_manifest.version}'s")
    config = NetworkConfig.from_manifest(child_manifest, pc.width, pc.blocks, pc.dropped_inputs)
    if config.dropped_ranges != pc.dropped_ranges:
        raise SurgeryError(f"the dropped inputs {list(pc.dropped_inputs)} moved between the Encodings")

    child = Network(config)
    n_in, n_policy = pc.input_size, pc.policy_size
    with torch.no_grad():
        state = {k: v.detach().cpu().clone() for k, v in parent.state_dict().items()}
        stem = torch.zeros(pc.width, config.input_size)
        stem[:, :n_in] = state["stem.weight"]
        policy_w = torch.zeros(config.policy_size, pc.width)
        policy_w[:n_policy] = state["policy_head.weight"]
        policy_b = torch.zeros(config.policy_size)
        policy_b[:n_policy] = state["policy_head.bias"]
        state.update({"stem.weight": stem, "policy_head.weight": policy_w, "policy_head.bias": policy_b})
        child.load_state_dict(state)
    child.train(parent.training)
    if check:
        check_same_in_base_games(parent, child)
    return child


def check_same_in_base_games(parent: Network, child: Network, positions: int = 64, seed: int = 0) -> float:
    """The exact check (P-A7): on base-shaped inputs (random v1 inputs, Pantheon inputs 0) the child's value, outcome
    and v1 policy logits equal the parent's. Returns the largest difference; raises SurgeryError beyond float32 rounding (RTOL, ATOL)."""
    g = torch.Generator().manual_seed(seed)
    n_in, n_policy = parent.config.input_size, parent.config.policy_size
    # Sparse 0/1 inputs like the real Encoding's, plus some fractions.
    x = (torch.rand(positions, n_in, generator=g) < 0.1).float()
    x[positions // 2:] *= torch.rand(positions - positions // 2, n_in, generator=g) * 2
    wide = torch.zeros(positions, child.config.input_size)
    wide[:, :n_in] = x
    parent_mode, child_mode = parent.training, child.training
    parent.eval(), child.eval()
    try:
        with torch.no_grad():
            p_policy, p_value, p_outcome = (t.cpu() for t in parent(x.to(next(parent.parameters()).device)))
            c_policy, c_value, c_outcome = (t.cpu() for t in child(wide.to(next(child.parameters()).device)))
    finally:
        parent.train(parent_mode), child.train(child_mode)
    pairs = ((c_policy[:, :n_policy], p_policy), (c_value, p_value), (c_outcome, p_outcome))
    diff = max(float((c - p).abs().max()) for c, p in pairs)
    if not all(torch.allclose(c, p, rtol=RTOL, atol=ATOL) for c, p in pairs):
        raise SurgeryError(f"the operated network differs from its parent in base-game positions by {diff:g}")
    return diff


def load_network(checkpoint: Path, manifest: Manifest) -> tuple[Network, dict]:
    """A training checkpoint's network (and the checkpoint)."""
    from .train import load_checkpoint
    ckpt = load_checkpoint(checkpoint, manifest)
    model = Network(NetworkConfig(**ckpt["network"]))
    model.load_state_dict(ckpt["model"])
    return model.eval(), ckpt


def load_onnx_network(path: Path, manifest: Manifest) -> tuple[Network, dict]:
    """An exported network's weights as a Network (export.py's contract: metadata names the Encoding and the shape; the
    initializers are the state_dict's tensors under their names). Refused unless every weight is found with its
    shape. Returns the network and a stand-in for the checkpoint (step None: an ONNX file doesn't record it)."""
    import onnx
    from onnx import numpy_helper
    from .export import FORMAT
    proto = onnx.load_model(str(path))
    meta = {p.key: p.value for p in proto.metadata_props}
    if meta.get("format") != FORMAT:
        raise SurgeryError(f"{path} is not a 7wd network (metadata format {meta.get('format')!r})")
    if meta.get("encoding_version") != str(manifest.version):
        raise SurgeryError(f"{path} is an Encoding v{meta.get('encoding_version')} network, not v{manifest.version}")
    dropped = tuple(n for n in meta.get("dropped_inputs", "").split(",") if n)
    config = NetworkConfig.from_manifest(manifest, int(meta["width"]), int(meta["blocks"]), dropped)
    model = Network(config)
    weights = {t.name: torch.from_numpy(numpy_helper.to_array(t).copy()) for t in proto.graph.initializer}
    state = model.state_dict()
    missing = [k for k, v in state.items() if k not in weights or tuple(weights[k].shape) != tuple(v.shape)]
    if missing:
        raise SurgeryError(f"{path} doesn't hold the network's weights {', '.join(missing[:5])}"
                           + (f" (and {len(missing) - 5} more)" if len(missing) > 5 else ""))
    model.load_state_dict({k: weights[k].to(v.dtype) for k, v in state.items()})
    return model.eval(), {"step": None}


def surgery(parent_checkpoint: Path, out: Path, parent_manifest: Manifest, child_manifest: Manifest,
            onnx_out: Path | None = None) -> Network:
    """Operate on a checkpoint (or an exported .onnx network) and save the result as a new checkpoint (and optionally its ONNX export)."""
    from .train import save_checkpoint
    parent_checkpoint, out = Path(parent_checkpoint), Path(out)
    if out.resolve() == parent_checkpoint.resolve():
        raise SurgeryError("the operated network must not overwrite its parent")
    load = load_onnx_network if parent_checkpoint.suffix.lower() == ".onnx" else load_network
    parent, ckpt = load(parent_checkpoint, parent_manifest)
    child = operate(parent, parent_manifest, child_manifest)
    optimizer = torch.optim.AdamW(child.parameters())  # fresh: the parent's moments don't fit the new shapes
    save_checkpoint(out, child, optimizer, 0, child_manifest,
                    extra={"surgery": {"parent": str(parent_checkpoint), "parent_step": ckpt["step"],
                                       "parent_encoding_version": parent_manifest.version}})
    if onnx_out is not None:
        export_onnx(child, child_manifest, onnx_out)
    return child


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="sevenwd-train surgery", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("parent", type=Path, help="the Encoding v1 network: a training checkpoint (e.g. a Run's net/latest.pt) "
                                             "or a Generation's exported network (e.g. net/gen-0012.onnx)")
    p.add_argument("--out", type=Path, required=True, help="the operated network's checkpoint (a new file)")
    p.add_argument("--onnx", type=Path, help="also export the operated network as ONNX")
    p.add_argument("--manifest", type=Path, help="the new Encoding's manifest (default: data/encoding/v2.json)")
    p.add_argument("--parent-manifest", type=Path, help="the parent's manifest (default: data/encoding/v1.json)")
    args = p.parse_args(argv)
    parent_manifest = load_manifest(args.parent_manifest or default_manifest_path(1))
    child_manifest = load_manifest(args.manifest or default_manifest_path(2))
    try:
        child = surgery(args.parent, args.out, parent_manifest, child_manifest, args.onnx)
    except ValueError as e:  # SurgeryError, or a checkpoint of another Encoding
        print(f"surgery refused: {e}", file=sys.stderr)
        return 1
    c = child.config
    print(f"wrote {args.out}: Encoding v{child_manifest.version} ({c.input_size} inputs, {c.policy_size} policy), "
          f"{c.width}x{c.blocks}" + (f", without {', '.join(c.dropped_inputs)}" if c.dropped_inputs else "")
          + "; same outputs as the parent in base-game positions" + (f"; exported {args.onnx}" if args.onnx else ""),
          flush=True)
    return 0
