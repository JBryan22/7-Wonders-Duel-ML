"""curve.py: a Run's learning curve. Plays every Generation's network against a fixed opponent (default Random) and
tabulates its win rate and Elo, Generation by Generation.

    uv run python -m sevenwd.curve ../runs/laptop-gumbel                      # each Generation vs Random, 100 pairs
    uv run python -m sevenwd.curve ../runs/laptop-gumbel --opponent heuristic --every 2

The loop's own Elo column is a chain (each Generation measured against the one before, the differences added up), so
its errors add up too. Here every Generation meets the same opponent on the same seeds, so the points are independent
and directly comparable: this is the "Elo against Random rises over the Generations" check (spec P7). The network
plays with the Run's Match settings (simulations, mode, search) unless --sims is given.

Writes <run>/curve/gen-NNNN-vs-<opponent>/ (the Matches; rerunning skips finished ones and resumes the rest) and
<run>/curve-vs-<opponent>.csv. Exit code 0 when the curve rises (the last Generation's lower 95% bound is above
Generation 0's upper bound), 3 when it doesn't, 1 on an error.
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
from dataclasses import dataclass, replace
from pathlib import Path

from .config import ConfigError
from .loop import Cli, Loop, Run, gen_name, open_run
from .runstate import RunError

FIELDS = ["generation", "games", "score", "score_low", "score_high", "elo", "elo_low", "elo_high", "wins", "losses",
          "draws", "victory_civilian", "victory_military", "victory_science"]


@dataclass
class Point:
    generation: int
    summary: dict

    def row(self) -> dict:
        s = self.summary
        base = s.get("eloB") or 0.0  # the opponent's rating (Random = 0); the curve is on the shared scale
        v = s.get("victoriesA", {})
        return {"generation": self.generation, "games": s["games"], "score": s["winRate"],
                "score_low": s["winRateLow"], "score_high": s["winRateHigh"], "elo": base + s["eloDiff"],
                "elo_low": base + s["eloDiffLow"], "elo_high": base + s["eloDiffHigh"], "wins": s["winsA"],
                "losses": s["winsB"], "draws": s["draws"], "victory_civilian": v.get("civilian"),
                "victory_military": v.get("military"), "victory_science": v.get("science")}


def generations(run: Run) -> list[int]:
    found = []
    for p in (run.root / "net").glob("gen-*.onnx"):
        m = re.fullmatch(r"gen-(\d+)\.onnx", p.name)
        if m:
            found.append(int(m.group(1)))
    return sorted(found)


def rises(rows: list[dict]) -> bool:
    """The curve rises when the last Generation is above Generation 0 beyond both error bars."""
    return len(rows) >= 2 and rows[-1]["score_low"] > rows[0]["score_high"]


def slope(rows: list[dict]) -> float | None:
    """Least-squares Elo per Generation over the curve (with Elo capped where a Match was won or lost outright)."""
    if len(rows) < 2:
        return None
    xs, ys = [r["generation"] for r in rows], [r["elo"] for r in rows]
    mx, my = sum(xs) / len(xs), sum(ys) / len(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx if sxx else None


def safe_name(spec: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", spec)


def play(run: Run, cli: Cli, g: int, opponent: str, pairs: int, sims: int | None, seed: int,
         say=print) -> dict:
    m = run.config.matches
    section = m if sims is None else replace(m, simulations=sims)
    directory = run.root / "curve" / f"{gen_name(g)}-vs-{safe_name(opponent)}"
    summary_path = directory / "summary.json"
    if summary_path.exists():
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
        if summary.get("complete") and summary.get("pairsPlanned") == pairs:
            return summary
    say(f"Generation {g} against {opponent} ({pairs} seed pairs)")
    spec = "neural:" + Loop(run, cli).neural_spec(run.net(g), section)
    args = ["match", spec, opponent, "--pairs", str(pairs), "--seed", str(seed), "--out", run.rel(directory)]
    if m.promo:
        args += ["--promo", m.promo]
    if m.parallel:
        args += ["--threads", str(m.parallel)]
    cli.run(args, run.root, directory / "log.txt")
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    if not summary.get("complete"):
        raise RuntimeError(f"the Match in {directory} is incomplete")
    return summary


def write_csv(path: Path, rows: list[dict]) -> None:
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        w.writeheader()
        for r in rows:
            w.writerow({k: (f"{v:.4f}" if isinstance(v, float) else v) for k, v in r.items()})


def table(rows: list[dict], opponent: str) -> str:
    lines = [f"Generation  score vs {opponent} (95% CI)      Elo (95% CI)",]
    for r in rows:
        lines.append(f"{r['generation']:>10}  {r['score']:6.1%} ({r['score_low']:5.1%}-{r['score_high']:5.1%})"
                     f"   {r['elo']:+6.0f} ({r['elo_low']:+.0f} to {r['elo_high']:+.0f})")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="sevenwd.curve", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("run", type=Path, help="the Run directory")
    p.add_argument("--opponent", default="random", help="the fixed opponent's Agent spec (default random)")
    p.add_argument("--pairs", type=int, default=100, help="seed pairs per Generation (default 100 = 200 games)")
    p.add_argument("--every", type=int, default=1, help="every Nth Generation (the last one is always included)")
    p.add_argument("--sims", type=int, help="the network's simulations (default: the Run's Match setting)")
    p.add_argument("--seed", type=int, default=1, help="first seed (default 1: other seeds than the loop's Matches)")
    p.add_argument("--cli", help="the 7wd program to run (default: build engine/src/Cli)")
    args = p.parse_args(argv)
    try:
        run = open_run(args.run)
        gens = generations(run)
        if not gens:
            raise RunError(f"{args.run} has no networks yet")
        chosen = [g for g in gens if g % args.every == 0 or g == gens[-1]]
        cli = Cli.locate(args.cli)
        rows = [Point(g, play(run, cli, g, args.opponent, args.pairs, args.sims, args.seed)).row() for g in chosen]
    except (RunError, ConfigError, RuntimeError) as e:
        print(f"sevenwd.curve: {e}", file=sys.stderr)
        return 1
    out = run.root / f"curve-vs-{safe_name(args.opponent)}.csv"
    write_csv(out, rows)
    print(table(rows, args.opponent))
    s = slope(rows)
    verdict = rises(rows)
    print(f"slope {s:+.1f} Elo per Generation; " if s is not None else "", end="")
    print(("RISES: " if verdict else "does NOT rise: ") +
          f"Generation {rows[-1]['generation']} {rows[-1]['score']:.1%} vs Generation {rows[0]['generation']} "
          f"{rows[0]['score']:.1%} (written to {out})")
    return 0 if verdict else 3


if __name__ == "__main__":
    sys.exit(main())
