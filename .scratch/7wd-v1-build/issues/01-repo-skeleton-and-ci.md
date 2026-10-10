# 01: Repo skeleton and CI

**What to build:** The repo layout from spec A2: the .NET 10 solution with Engine, Agents, Cli, Advisor and the two test projects; the uv Python package under training/; data/, tts/, git-ignored runs/. A trivial test on each side, and GitHub Actions running both test suites on Linux and Windows.

**Blocked by:** None (can start immediately)

**Status:** done

Spec: `docs/spec-v1.md`.

- [x] dotnet test passes
- [x] uv run pytest passes
- [x] CI workflow runs C# and Python tests on ubuntu and windows runners
- [x] runs/ is git-ignored

## Notes

- Verified locally on the Windows desktop: `dotnet test` (engine/) and `uv run pytest` (training/) pass. The CI workflow is written but has not run yet, because nothing has been pushed to GitHub.
- PyTorch is not a dependency yet. It arrives with ticket 19, together with the CPU/CUDA platform markers.
- uv was installed on the desktop with winget (`astral-sh.uv`). uv manages Python 3.12 itself.
- Shared C# settings live in `engine/Directory.Build.props`: net10.0, nullable, warnings as errors, invariant globalization, and `SevenWondersDuel.<Project>` namespaces. The CLI's binary is named `7wd`.
