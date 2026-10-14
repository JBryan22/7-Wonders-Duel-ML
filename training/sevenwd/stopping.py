"""The stopping rule (spec A7, "Budget and stopping"): when a Run stops itself.

Checked by loop.py after every Generation (on the rows of generations.csv plus the one just finished) and once when
it starts. A Run stops itself on the first of:

- **cap**: the Run has trained for `stop.cap_hours` desktop-hours (hours with the loop running; pauses don't count).
  Checked between Generations, so the Run overshoots by at most one Generation.
- **plateau**: the Anchor (or reference, see below) Elo hasn't risen by more than its error bar over the last `stop.plateau_generations`
  Generations (20 = 4 Anchor Matches at one every 5): the best Anchor Elo measured after Generation g-20 is at most
  one error bar (the half-width of its 95% interval) above the last one measured at or before g-20.
- **warm-start trigger "science"**: when Generation `science_to` finishes, science wins were under
  `science_min_share` of the Self-Play games of Generations `science_from`..`science_to`.
- **warm-start trigger "no progress"**: when Generation `progress_by` finishes, no Anchor Match since the first one
  measured more than one error bar above it.

The warm-start triggers are checked only on their own Generation, so starting the Run again after one (the owner
decided not to warm-start) simply continues. The cap and the plateau hold again at every start: going on anyway is
an explicit `7wd-train start --ignore-stop`, which loop.py records in the Run's overrides.json.

With a reference (`matches.reference`, a stronger fixed opponent played on the Anchor's Generations), the plateau
and no-progress tests use whichever of the two still has headroom: the one the latest Generation measured scored
lowest against (normally the reference; the Anchor saturates as the network passes it, and near 100% a score can't
show progress any more). Each series is only ever compared with itself.

Anchor Elo here is the Elo implied by the Anchor Match score (400·log10(p/(1-p)), relative to the Anchor; scores of
0 or 1 are taken as half a game from them). Adding the Anchor's own rating only shifts every point by the same amount,
so differences, which are all the rule looks at, are the same with or without it.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Iterable, Mapping

from .config import RunConfig

# Kinds a later start can ignore (with --ignore-stop); warm-start triggers fire only once, faults aren't rules.
STANDING = ("cap", "plateau")


@dataclass(frozen=True)
class StopReason:
    kind: str       # cap | plateau | science | no-progress | fault
    message: str    # one line for status, generations.csv, TensorBoard and the notification

    @property
    def warm_start(self) -> bool:
        return self.kind in ("science", "no-progress")


@dataclass(frozen=True)
class AnchorPoint:
    generation: int
    elo: float
    error: float    # half-width of the 95% interval, in Elo


def value(x) -> float | None:
    if x is None or x == "":
        return None
    return float(x)


def elo_of(p: float, games: int) -> float:
    edge = 0.5 / max(1, games)
    p = min(max(p, edge), 1 - edge)
    return 400 * math.log10(p / (1 - p))


def anchor_points(rows: Iterable[Mapping], games: int, prefix: str = "anchor") -> list[AnchorPoint]:
    """The Elo points of one series of fixed-opponent Matches: the Anchor (columns anchor_*) or the reference
    (ref_*)."""
    points = []
    for r in rows:
        p, lo, hi = value(r.get(f"{prefix}_score")), value(r.get(f"{prefix}_low")), value(r.get(f"{prefix}_high"))
        if p is None:
            continue
        lo = p if lo is None else lo
        hi = p if hi is None else hi
        points.append(AnchorPoint(int(value(r["generation"])), elo_of(p, games),
                                  (elo_of(hi, games) - elo_of(lo, games)) / 2))
    return points


@dataclass(frozen=True)
class Series:
    name: str                   # "Anchor" or "reference": how messages call it
    points: list[AnchorPoint]


def latest_score(rows: Iterable[Mapping], prefix: str) -> float | None:
    scores = [value(r.get(f"{prefix}_score")) for r in rows]
    return next((x for x in reversed(scores) if x is not None), None)


def headroom_series(config: RunConfig, rows: list[Mapping]) -> Series:
    """The fixed-opponent series the plateau and no-progress tests use: of the Anchor and the reference (when the
    config has one), the one the newest measurement scored lowest against: it has the most room left to show
    progress. The Anchor while the reference hasn't been measured."""
    m = config.matches
    chosen = Series("Anchor", anchor_points(rows, 2 * m.pairs))
    if m.reference:
        ref, anchor = latest_score(rows, "ref"), latest_score(rows, "anchor")
        if ref is not None and (anchor is None or ref <= anchor):
            chosen = Series("reference", anchor_points(rows, 2 * (m.reference_pairs or m.pairs), "ref"))
    return chosen


def plateau(points: list[AnchorPoint], last: int, span: int, name: str = "Anchor") -> StopReason | None:
    before = [p for p in points if p.generation <= last - span]
    recent = [p for p in points if p.generation > last - span]
    if not before or not recent:
        return None
    base, best = before[-1], max(recent, key=lambda p: p.elo)
    rise = best.elo - base.elo
    if rise > best.error:
        return None
    return StopReason("plateau", f"plateau: the {name} Elo rose {rise:+.0f} (error bar {best.error:.0f}) over the last "
                                 f"{span} Generations ({len(recent)} {name} Matches since Generation {base.generation})")


def no_progress(points: list[AnchorPoint], by: int, name: str = "Anchor") -> StopReason | None:
    points = [p for p in points if p.generation <= by]
    if len(points) < 2:
        return None
    first, best = points[0], max(points[1:], key=lambda p: p.elo)
    rise = best.elo - first.elo
    if rise > best.error:
        return None
    return StopReason("no-progress", f"warm-start trigger: no {name} Elo progress by Generation {by} (best "
                                     f"{rise:+.0f} vs Generation {first.generation}, error bar {best.error:.0f})")


def science(rows: Iterable[Mapping], first: int, last: int, minimum: float) -> StopReason | None:
    games = wins = 0.0
    for r in rows:
        g = int(value(r["generation"]))
        n, share = value(r.get("selfplay_games")), value(r.get("victory_science"))
        if first <= g <= last and n is not None and share is not None:
            games += n
            wins += n * share
    if games == 0 or wins / games >= minimum:
        return None
    return StopReason("science", f"warm-start trigger: science wins were {wins / games:.1%} of Self-Play games over "
                                 f"Generations {first}-{last} (under {minimum:.0%})")


def check(config: RunConfig, rows: list[Mapping], hours: float, ignore: Iterable[str] = (),
          at_start: bool = False) -> StopReason | None:
    """Whether the Run should stop now. `rows` are the finished Generations (generations.csv rows, strings or
    numbers), `hours` the desktop-hours trained so far. At start only the standing rules (cap, plateau) apply."""
    s, ignore = config.stop, set(ignore)
    if s.cap_hours is not None and "cap" not in ignore and hours >= s.cap_hours:
        return StopReason("cap", f"cap: {hours:.1f} of {s.cap_hours:g} desktop-hours used")
    if not rows:
        return None
    last = int(value(rows[-1]["generation"]))
    series = headroom_series(config, rows)
    points = series.points
    if s.plateau_generations is not None and "plateau" not in ignore:
        reason = plateau(points, last, s.plateau_generations, series.name)
        if reason:
            return reason
    if at_start:
        return None
    if s.science_min_share is not None and last == s.science_to:
        reason = science(rows, s.science_from, s.science_to, s.science_min_share)
        if reason:
            return reason
    if s.progress_by is not None and last == s.progress_by:
        reason = no_progress(points, s.progress_by, series.name)
        if reason:
            return reason
    return None
