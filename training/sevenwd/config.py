"""A Run's config (spec A7, "Runs"): everything that decides the Run, read from its frozen `config.yaml`.

The file has these sections (every key optional; unknown keys are refused, so a typo can't silently fall back to a
default). `training/configs/default.yaml` lists them all with the spec's starting defaults; `tiny.yaml` is the CPU
smoke-test config.

    encoding_version: 1     the Encoding the Run trains on; refused if the committed manifest is another version
    seed: 0                 Self-Play, training and Match seeds all derive from it
    generations: null       stop after this Generation (null: keep going until stopped)
    init: null              start Generation 0 from this checkpoint (e.g. another Run's net/latest.pt) instead of random
    network:   width, blocks
    selfplay:  games, simulations, mode (gumbel | classic), search (extra neural: settings), promo_rate, device,
               threads (ONNX Runtime CPU threads), parallel (games at a time), workers (inference threads)
    training:  window_positions, reuse (times each new position is seen on average), batch_size, learning_rate,
               weight_decay, device, checkpoint_minutes, min_steps
    matches:   pairs, simulations, mode, search, device, threads, parallel (game threads), promo, anchor, anchor_elo,
               anchor_every, generation0_opponent
    stop:      when the Run stops itself (spec A7, "Budget and stopping"; null switches a rule off):
               cap_hours (desktop-hours of training), plateau_generations (the Anchor Elo hasn't risen by more than its
               error bar over this many Generations), science_min_share + science_from/science_to (warm-start
               trigger: science wins under this share of the Self-Play games of those Generations), progress_by (warm-
               start trigger: no Anchor Elo progress by this Generation). See stopping.py.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass, field, fields
from pathlib import Path
from typing import Any

import yaml


class ConfigError(ValueError):
    """The config file is malformed or names a setting that doesn't exist."""


@dataclass(frozen=True)
class NetworkSection:
    width: int = 256
    blocks: int = 4


@dataclass(frozen=True)
class SelfPlaySection:
    games: int = 2000
    simulations: int = 64
    mode: str = "gumbel"
    search: str = ""
    promo_rate: float = 1.0
    device: str = "cpu"
    threads: int = 4
    parallel: int | None = None
    workers: int | None = None


@dataclass(frozen=True)
class TrainingSection:
    window_positions: int = 1_000_000
    reuse: float = 2.0
    batch_size: int = 1024
    learning_rate: float = 1e-3
    weight_decay: float = 1e-4
    device: str = "auto"
    checkpoint_minutes: float = 5.0
    min_steps: int = 1


@dataclass(frozen=True)
class MatchSection:
    pairs: int = 200
    simulations: int = 800
    mode: str = "gumbel"
    search: str = ""
    device: str = "cpu"
    threads: int = 4
    parallel: int | None = None
    promo: str | None = None
    anchor: str = "mcts"
    anchor_elo: float | None = None
    anchor_every: int = 5
    generation0_opponent: str | None = "random"


@dataclass(frozen=True)
class StopSection:
    cap_hours: float | None = 300.0
    plateau_generations: int | None = 20
    science_min_share: float | None = 0.01
    science_from: int = 6
    science_to: int = 10
    progress_by: int | None = 20


@dataclass(frozen=True)
class RunConfig:
    encoding_version: int = 1
    seed: int = 0
    generations: int | None = None
    init: str | None = None
    network: NetworkSection = field(default_factory=NetworkSection)
    selfplay: SelfPlaySection = field(default_factory=SelfPlaySection)
    training: TrainingSection = field(default_factory=TrainingSection)
    matches: MatchSection = field(default_factory=MatchSection)
    stop: StopSection = field(default_factory=StopSection)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


SECTIONS = {"network": NetworkSection, "selfplay": SelfPlaySection, "training": TrainingSection,
            "matches": MatchSection, "stop": StopSection}


def _build(cls, raw: Any, where: str):
    if raw is None:
        return cls()
    if not isinstance(raw, dict):
        raise ConfigError(f"{where}: expected a mapping, got {type(raw).__name__}")
    known = {f.name: f for f in fields(cls)}
    unknown = sorted(set(raw) - set(known))
    if unknown:
        raise ConfigError(f"{where}: unknown setting(s) {', '.join(unknown)} (known: {', '.join(known)})")
    values = {}
    for name, value in raw.items():
        if name in SECTIONS and cls is RunConfig:
            values[name] = _build(SECTIONS[name], value, f"{where}.{name}" if where else name)
            continue
        default = getattr(cls(), name)
        if value is not None and isinstance(default, float) and isinstance(value, int) and not isinstance(value, bool):
            value = float(value)
        values[name] = value
    return cls(**values)


def parse_config(raw: Any) -> RunConfig:
    config = _build(RunConfig, raw or {}, "")
    validate(config)
    return config


def load_config(path: str | Path) -> RunConfig:
    path = Path(path)
    try:
        raw = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as e:
        raise ConfigError(f"{path}: not valid YAML: {e}") from e
    try:
        return parse_config(raw)
    except ConfigError as e:
        raise ConfigError(f"{path}: {e}") from e


def validate(c: RunConfig) -> None:
    def need(ok: bool, message: str):
        if not ok:
            raise ConfigError(message)

    need(c.network.width > 0 and c.network.blocks > 0, "network width and blocks must be positive")
    need(c.selfplay.games > 0 and c.selfplay.simulations > 0, "selfplay games and simulations must be positive")
    need(c.selfplay.mode in ("gumbel", "classic"), "selfplay.mode is gumbel or classic")
    need(0 <= c.selfplay.promo_rate <= 1, "selfplay.promo_rate is a probability, 0..1")
    need(c.training.window_positions > 0 and c.training.reuse > 0 and c.training.batch_size > 0,
         "training window_positions, reuse and batch_size must be positive")
    need(c.matches.pairs > 0 and c.matches.simulations > 0, "matches pairs and simulations must be positive")
    need(c.matches.mode in ("gumbel", "classic"), "matches.mode is gumbel or classic")
    need(c.matches.anchor_every >= 0, "matches.anchor_every is 0 (never) or a positive number of Generations")
    need(c.generations is None or c.generations >= 0, "generations must be 0 or more")
    st = c.stop
    need(st.cap_hours is None or st.cap_hours > 0, "stop.cap_hours is positive (or null: no cap)")
    need(st.plateau_generations is None or st.plateau_generations > 0, "stop.plateau_generations is positive or null")
    need(st.science_min_share is None or 0 <= st.science_min_share <= 1, "stop.science_min_share is 0..1 or null")
    need(1 <= st.science_from <= st.science_to, "stop.science_from..science_to are Generations, 1 <= from <= to")
    need(st.progress_by is None or st.progress_by > 0, "stop.progress_by is a positive Generation or null")
