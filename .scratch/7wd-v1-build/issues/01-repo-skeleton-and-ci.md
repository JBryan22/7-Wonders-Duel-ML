# 01: Repo skeleton and CI

**What to build:** The repo layout from spec A2: the .NET 10 solution with Engine, Agents, Cli, Advisor and the two test projects; the uv Python package under training/; data/, tts/, git-ignored runs/. A trivial test on each side, and GitHub Actions running both test suites on Linux and Windows.

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

Spec: `docs/spec-v1.md`.

- [ ] dotnet test passes
- [ ] uv run pytest passes
- [ ] CI workflow runs C# and Python tests on ubuntu and windows runners
- [ ] runs/ is git-ignored
