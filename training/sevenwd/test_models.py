"""Tiny ONNX models for the C# tests (ticket 20), committed under engine/tests/Agents.Tests/Models/.

    tiny.onnx           a seeded random network, width 8 x 1 block (~33k parameters, ~140 KB): real inference
    tiny.expected.json  its torch outputs for two fixed inputs, so C# can check it runs the model the same way
    zero.onnx           all weights zero: a uniform policy (every logit 0) and value 0 everywhere
    wrong-version.onnx  like zero.onnx but stamped with Encoding version 999, which C# must refuse

Regenerate with `uv run python -m sevenwd.test_models` (from training/) after a change to the network or the export.
"""
from __future__ import annotations

import argparse
import dataclasses
import json
from pathlib import Path

import torch

from .export import export_onnx
from .manifest import REPO_ROOT, Manifest, load_manifest
from .network import Network, NetworkConfig

DEFAULT_DIR = REPO_ROOT / "engine" / "tests" / "Agents.Tests" / "Models"
WRONG_VERSION = 999


def fixed_inputs(manifest: Manifest) -> torch.Tensor:
    """Two inputs C# can rebuild exactly: all zeros, and x[i] = (i % 7) / 7."""
    ramp = torch.tensor([(i % 7) / 7 for i in range(manifest.input_size)], dtype=torch.float32)
    return torch.stack([torch.zeros(manifest.input_size), ramp])


def write_test_models(out: Path, manifest: Manifest | None = None) -> list[Path]:
    manifest = manifest or load_manifest()
    out.mkdir(parents=True, exist_ok=True)

    torch.manual_seed(20)
    tiny = Network(NetworkConfig.from_manifest(manifest, width=8, blocks=1)).eval()
    with torch.no_grad():
        policy, value, outcome = tiny(fixed_inputs(manifest))
    expected = {
        "inputs": ["zeros", "ramp7"],
        "policy": policy.tolist(),
        "value": value.tolist(),
        "outcome": outcome.tolist(),
    }

    zero = Network(NetworkConfig.from_manifest(manifest, width=1, blocks=1)).eval()
    with torch.no_grad():
        for p in zero.parameters():
            p.zero_()

    paths = [
        export_onnx(tiny, manifest, out / "tiny.onnx"),
        export_onnx(zero, manifest, out / "zero.onnx"),
        export_onnx(zero, dataclasses.replace(manifest, version=WRONG_VERSION), out / "wrong-version.onnx"),
    ]
    expected_path = out / "tiny.expected.json"
    expected_path.write_text(json.dumps(expected) + "\n", encoding="utf-8")
    return [*paths, expected_path]


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out", type=Path, default=DEFAULT_DIR)
    args = p.parse_args(argv)
    for path in write_test_models(args.out):
        print(f"wrote {path} ({path.stat().st_size:,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
