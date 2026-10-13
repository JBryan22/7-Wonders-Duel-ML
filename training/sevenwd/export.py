"""ONNX export (spec A6/A7): the hand-off from Python training to C# inference.

The exported model's contract (what C# relies on):

    input   "input"    float32 [batch, input_size]     the Encoding input vector (dynamic batch)
    output  "policy"   float32 [batch, policy_size]    raw logits, NOT masked: C# sets illegal Moves to -inf, softmaxes
    output  "value"    float32 [batch]                 tanh, the player to decide's chance to win in [-1, 1]
    output  "outcome"  float32 [batch, outcome_size]   raw outcome-class logits (softmax for probabilities)

and string metadata (ONNX metadata_props, ORT's ModelMetadata.CustomMetadataMap):

    format = "7wd-network", encoding_version = "<int>", input_size, policy_size, outcome_size,
    outcome_classes (comma-separated, manifest order), width, blocks
    dropped_inputs (comma-separated input block names; only on a network that drops inputs, see network.py: it
    takes the same Encoding inputs, and C# checks that the names are blocks of its Encoding)
"""
from __future__ import annotations

from pathlib import Path

import onnx
import torch

from .manifest import Manifest
from .network import Network

FORMAT = "7wd-network"
OPSET = 18
INPUT_NAME = "input"
OUTPUT_NAMES = ("policy", "value", "outcome")


def export_onnx(model: Network, manifest: Manifest, path: str | Path) -> Path:
    """Export the model to a single self-contained .onnx file stamped with the Encoding version."""
    config = model.config
    if (config.input_size, config.policy_size, config.outcome_size) != (
        manifest.input_size, manifest.policy_size, manifest.outcome_size
    ):
        raise ValueError("the network's sizes don't match the manifest")
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    was_training = model.training
    device = next(model.parameters()).device
    model = model.cpu().eval()
    try:
        example = torch.zeros(2, config.input_size, dtype=torch.float32)
        program = torch.onnx.export(
            model,
            (example,),
            input_names=[INPUT_NAME],
            output_names=list(OUTPUT_NAMES),
            dynamic_shapes=({0: torch.export.Dim("batch")},),
            opset_version=OPSET,
            dynamo=True,
            verbose=False,
        )
        proto = program.model_proto
    finally:
        model.to(device)
        model.train(was_training)

    stamp = {
        "format": FORMAT,
        "encoding_version": str(manifest.version),
        "input_size": str(config.input_size),
        "policy_size": str(config.policy_size),
        "outcome_size": str(config.outcome_size),
        "outcome_classes": ",".join(manifest.outcome_classes),
        "width": str(config.width),
        "blocks": str(config.blocks),
    }
    if config.dropped_inputs:
        stamp["dropped_inputs"] = ",".join(config.dropped_inputs)
    del proto.metadata_props[:]
    for key, value in stamp.items():
        proto.metadata_props.add(key=key, value=value)
    proto.producer_name = "sevenwd"
    onnx.checker.check_model(proto)
    onnx.save_model(proto, str(path), save_as_external_data=False)
    return path


def read_metadata(path: str | Path) -> dict[str, str]:
    """The metadata stamped into an exported model."""
    proto = onnx.load_model(str(path), load_external_data=False)
    return {p.key: p.value for p in proto.metadata_props}
