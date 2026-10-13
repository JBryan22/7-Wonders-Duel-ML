"""Encoded shards (spec A7, "Storage"): the disposable training cache that C# Self-Play writes and Python memory-maps.

A shard is a directory of plain NumPy .npy files (little-endian, C order) plus a small JSON header:

    shard.json          {"format": "7wd-shard", "encoding_version": 1, "positions": N}
    input.npy           float16 [N, input_size]   the Encoding input vector of each searched decision
    policy_offset.npy   int64   [N + 1]           CSR row pointers into the two arrays below (offset[0] = 0)
    policy_index.npy    int16   [M]               policy index of each legal Move, M = offset[N]
    policy_prob.npy     float32 [M]               its target probability (improved policy / visit share)
    value.npy           float32 [N]               result for the player to decide: +1 win, 0 draw, -1 loss
    outcome.npy         int8    [N]               outcome-class index (manifest heads.outcome.classes)

Row i's legal Moves are policy_index[offset[i]:offset[i+1]]: every legal Move is listed, including those with
target 0, so the list doubles as the legal mask for the masked softmax. Targets in a row sum to 1.

A **compacted** shard (`compact_shard`, which the training loop runs on every new Self-Play shard) replaces the dense
input.npy by the same rows in CSR form; the inputs are ~94% zeros, so this is ~8x smaller and lets a multi-Generation
training window stay in RAM (ticket 26):

    input_offset.npy    int64   [N + 1]           CSR row pointers (input_offset[0] = 0)
    input_index.npy     int16   [K]               input index of each nonzero entry, ascending within a row
    input_value.npy     float16 [K]               its value

The two forms hold exactly the same numbers; readers accept either.
"""
from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path

import numpy as np

FORMAT = "7wd-shard"
HEADER = "shard.json"


class ShardError(ValueError):
    pass


@dataclass
class Batch:
    """Dense arrays for one training batch."""

    input: np.ndarray  # float32 [B, input_size]
    policy: np.ndarray  # float32 [B, policy_size]
    legal: np.ndarray  # bool    [B, policy_size]
    value: np.ndarray  # float32 [B]
    outcome: np.ndarray  # int64   [B]


def write_shard(
    directory: str | Path,
    encoding_version: int,
    inputs: np.ndarray,
    policies: list[tuple[np.ndarray, np.ndarray]],
    values: np.ndarray,
    outcomes: np.ndarray,
) -> Path:
    """Write a shard (used by tests and as the reference for the C# writer). policies[i] = (indices, probs)."""
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    n = len(inputs)
    if not (len(policies) == len(values) == len(outcomes) == n):
        raise ShardError("inputs, policies, values and outcomes differ in length")
    lengths = np.array([len(ix) for ix, _ in policies], dtype=np.int64)
    offset = np.zeros(n + 1, dtype=np.int64)
    np.cumsum(lengths, out=offset[1:])
    index = np.concatenate([np.asarray(ix, dtype=np.int16) for ix, _ in policies]) if n else np.zeros(0, np.int16)
    prob = np.concatenate([np.asarray(p, dtype=np.float32) for _, p in policies]) if n else np.zeros(0, np.float32)

    np.save(directory / "input.npy", np.asarray(inputs, dtype=np.float16))
    np.save(directory / "policy_offset.npy", offset)
    np.save(directory / "policy_index.npy", index)
    np.save(directory / "policy_prob.npy", prob)
    np.save(directory / "value.npy", np.asarray(values, dtype=np.float32))
    np.save(directory / "outcome.npy", np.asarray(outcomes, dtype=np.int8))
    (directory / HEADER).write_text(
        json.dumps({"format": FORMAT, "encoding_version": encoding_version, "positions": n}, indent=2) + "\n",
        encoding="utf-8",
    )
    return directory


class Shard:
    """One shard, memory-mapped."""

    def __init__(self, directory: str | Path, encoding_version: int, input_size: int, policy_size: int,
                 outcome_size: int):
        self.directory = Path(directory)
        header = json.loads((self.directory / HEADER).read_text(encoding="utf-8"))
        if header.get("format") != FORMAT:
            raise ShardError(f"{self.directory}: not a shard (format {header.get('format')!r})")
        if header.get("encoding_version") != encoding_version:
            raise ShardError(f"{self.directory}: Encoding version {header.get('encoding_version')} "
                             f"but the manifest is version {encoding_version}")

        def load(name: str, dtype) -> np.ndarray:
            a = np.load(self.directory / name, mmap_mode="r")
            if a.dtype != np.dtype(dtype):
                raise ShardError(f"{self.directory / name}: dtype {a.dtype}, expected {np.dtype(dtype)}")
            return a

        self.sparse = not (self.directory / "input.npy").exists() and (self.directory / "input_offset.npy").exists()
        if self.sparse:
            self.input = None
            self.input_offset = np.asarray(load("input_offset.npy", np.int64))
            self.input_index = load("input_index.npy", np.int16)
            self.input_value = load("input_value.npy", np.float16)
        else:
            self.input = load("input.npy", np.float16)
        self.offset = np.asarray(load("policy_offset.npy", np.int64))
        self.index = load("policy_index.npy", np.int16)
        self.prob = load("policy_prob.npy", np.float32)
        self.value = load("value.npy", np.float32)
        self.outcome = load("outcome.npy", np.int8)

        n = header.get("positions")
        if self.sparse:
            if (self.input_offset.shape != (n + 1,) or self.input_offset[0] != 0
                    or self.input_offset[-1] != len(self.input_index) or len(self.input_value) != len(self.input_index)):
                raise ShardError(f"{self.directory}: bad input_offset")
            if len(self.input_index) and (int(self.input_index.min()) < 0
                                          or int(self.input_index.max()) >= input_size):
                raise ShardError(f"{self.directory}: input index out of range (input size {input_size})")
        elif self.input.shape != (n, input_size):
            raise ShardError(f"{self.directory}: input shape {self.input.shape}, expected ({n}, {input_size})")
        if self.offset.shape != (n + 1,) or self.offset[0] != 0 or self.offset[-1] != len(self.index):
            raise ShardError(f"{self.directory}: bad policy_offset")
        if len(self.prob) != len(self.index) or self.value.shape != (n,) or self.outcome.shape != (n,):
            raise ShardError(f"{self.directory}: array lengths disagree")
        if len(self.index) and (int(self.index.min()) < 0 or int(self.index.max()) >= policy_size):
            raise ShardError(f"{self.directory}: policy index out of range")
        if n and (int(self.outcome.min()) < 0 or int(self.outcome.max()) >= outcome_size):
            raise ShardError(f"{self.directory}: outcome class out of range")
        self.positions = n


def compact_shard(directory: str | Path, chunk: int = 32768) -> bool:
    """Replace a shard's dense input.npy by input_offset/input_index/input_value (see the module docstring). Safe to
    rerun after a crash: the sparse files are complete before input.npy is deleted, and input_offset.npy (renamed into
    place last) marks them complete. Returns True when it compacted something.

    Reads the dense file with plain reads, in chunks, twice (count, then write), and writes sequentially: a
    desktop-hour shard is ~18 GB dense, and memory-mapping it would pull all of it into this process's working set."""
    directory = Path(directory)
    dense_path = directory / "input.npy"
    if not dense_path.exists():
        return False
    if not (directory / "input_offset.npy").exists():
        counts = np.concatenate([np.count_nonzero(block, axis=1) for block in _dense_chunks(dense_path, chunk)]
                                or [np.zeros(0, np.int64)]).astype(np.int64)
        offset = np.zeros(len(counts) + 1, np.int64)
        np.cumsum(counts, out=offset[1:])
        total = int(offset[-1])
        tmp = {name: directory / f"{name}.tmp.npy" for name in ("input_index", "input_value", "input_offset")}
        with open(tmp["input_index"], "wb") as fi, open(tmp["input_value"], "wb") as fv:
            for f, dtype in ((fi, np.int16), (fv, np.float16)):
                np.lib.format.write_array_header_1_0(f, {"descr": np.lib.format.dtype_to_descr(np.dtype(dtype)),
                                                         "fortran_order": False, "shape": (total,)})
            written = 0
            for block in _dense_chunks(dense_path, chunk):
                rows, cols = np.nonzero(block)  # row-major: ascending columns within each row
                fi.write(cols.astype(np.int16).tobytes())
                fv.write(block[rows, cols].astype(np.float16).tobytes())
                written += len(cols)
            if written != total:
                raise ShardError(f"{dense_path} changed while it was compacted")
        np.save(tmp["input_offset"], offset)
        for name in ("input_index", "input_value", "input_offset"):
            os.replace(tmp[name], directory / f"{name}.npy")
    dense_path.unlink()
    return True


def _dense_chunks(path: Path, chunk: int):
    """The rows of a float16 [N, D] .npy file, `chunk` rows at a time, read without a memory map."""
    with open(path, "rb") as f:
        version = np.lib.format.read_magic(f)
        read_header = np.lib.format.read_array_header_1_0 if version == (1, 0) else np.lib.format.read_array_header_2_0
        shape, fortran, dtype = read_header(f)
        if fortran or dtype != np.dtype(np.float16) or len(shape) != 2:
            raise ShardError(f"{path}: expected a C-order float16 matrix, got {dtype} {shape}")
        n, width = shape
        for a in range(0, n, chunk):
            rows = min(chunk, n - a)
            block = np.fromfile(f, dtype=np.float16, count=rows * width)
            if block.size != rows * width:
                raise ShardError(f"{path}: truncated")
            yield block.reshape(rows, width)


def find_shards(paths: list[str | Path]) -> list[Path]:
    """Shard directories under the given paths (each path is a shard or a folder searched recursively), sorted."""
    found: set[Path] = set()
    for p in map(Path, paths):
        if (p / HEADER).is_file():
            found.add(p)
        elif p.is_dir():
            found.update(h.parent for h in p.rglob(HEADER))
        else:
            raise ShardError(f"no shard at {p}")
    return sorted(found)


class ShardDataset:
    """All positions of a set of shards, indexed 0..len-1, with dense batches built on demand."""

    def __init__(self, directories: list[str | Path], encoding_version: int, input_size: int, policy_size: int,
                 outcome_size: int):
        self.shards = [Shard(d, encoding_version, input_size, policy_size, outcome_size) for d in directories]
        self.input_size = input_size
        self.policy_size = policy_size
        self.starts = np.cumsum([0] + [s.positions for s in self.shards])

    def __len__(self) -> int:
        return int(self.starts[-1])

    def batch(self, positions: np.ndarray) -> Batch:
        positions = np.asarray(positions, dtype=np.int64)
        b = len(positions)
        out = Batch(
            input=np.zeros((b, self.input_size), np.float32),
            policy=np.zeros((b, self.policy_size), np.float32),
            legal=np.zeros((b, self.policy_size), bool),
            value=np.empty(b, np.float32),
            outcome=np.empty(b, np.int64),
        )
        which = np.searchsorted(self.starts, positions, side="right") - 1
        for s in np.unique(which):
            rows = np.nonzero(which == s)[0]
            shard = self.shards[s]
            local = positions[rows] - self.starts[s]
            order = np.argsort(local)  # sorted reads are kinder to a memory map
            rows, local = rows[order], local[order]
            if shard.sparse:
                b0, b1 = shard.input_offset[local], shard.input_offset[local + 1]
                n_in = b1 - b0
                flat_in = np.repeat(b0 - np.cumsum(n_in) + n_in, n_in) + np.arange(n_in.sum())
                out.input[np.repeat(rows, n_in), shard.input_index[flat_in].astype(np.int64)] =                     shard.input_value[flat_in]
            else:
                out.input[rows] = shard.input[local]
            out.value[rows] = shard.value[local]
            out.outcome[rows] = shard.outcome[local]
            # Gather each row's CSR slice without a Python loop.
            begin, end = shard.offset[local], shard.offset[local + 1]
            lengths = end - begin
            flat = np.repeat(begin - np.cumsum(lengths) + lengths, lengths) + np.arange(lengths.sum())
            target_rows = np.repeat(rows, lengths)
            cols = shard.index[flat].astype(np.int64)
            out.policy[target_rows, cols] = shard.prob[flat]
            out.legal[target_rows, cols] = True
        return out
