# bench

The benchmark of [docs/benchmark.md](../docs/benchmark.md): Claude Code with and without Karvon's tools, task by task.

- `tasks/<id>/`: one task (below).
- `fixtures/<name>/`: the workspaces the tasks start from (their packages are restored when the image is built).
- `Karvon.Bench/`: the runner, Karvon headless: it opens the workspace, starts one Claude Code session of an arm, sends
  the prompt and records the session.
- `run.py`: the sweep: makes each run's workspace, runs the runner and the check in a container of its own, writes
  `results/<date>/runs.jsonl`.
- `report.py`: `results/<date>/report.md` from `runs.jsonl`.
- `Dockerfile`: the image the runs go in.

## A task

`tasks/<id>/task.json`:

```json
{
  "id": "T01",
  "category": "build",
  "fixture": "shop",
  "open": "Shop.sln",
  "prompt": "The solution does not build. Fix it.",
  "maxTurns": 40,
  "maxMinutes": 15
}
```

- `fixture`: a folder of `fixtures/` copied as the workspace, `karvon` (this repository at the commit under test,
  without build output) or `empty`.
- `open`: what Karvon opens in the workspace (a solution, a project, or `.` for the folder).
- Optional: `startup` (the startup project), `breakpoint` (`{"file": "...", "line": 12}`: the runner sets it and starts
  F5 before the prompt, and sends the prompt once the program is stopped there), `database` (`{"name": "shop", "file":
  "shop.db"}`: registered as a SQLite data source of Karvon's Database Explorer).

The scripts, each run with the workspace as its current folder, in the container:

- `setup.sh` (optional): makes the task's state from the fixture (puts the bug in). It runs before the workspace's
  first commit, so `git diff` shows only what the session changed.
- `check.sh`: after the session, decides by itself: exit 0 passes, anything else fails, with one line saying why.
  `$BENCH_ANSWER` is a file with the session's last answer (for the tasks that ask a question). Offline, deterministic,
  under 3 minutes; it never asks a model.
- `solution.sh`: a reference fix, to check the task: on a fresh workspace (after `setup.sh`) `check.sh` fails, and
  after `solution.sh` it passes. For a question it writes a right answer to `$BENCH_ANSWER`. Never copied into a
  workspace, nor are `check.sh` and `task.json`.

The prompt is the same in every arm and never names a tool. Nothing in the workspace gives the answer away.

## The image

Debian with the .NET 10 SDK, Node.js 24, Python 3 with pytest, Go 1.26, Rust (cargo), sqlite3, git, Docker (its own
daemon, for T14), a pinned Claude Code CLI, Karvon's runner, and the language servers and debuggers Karvon would
download. The fixtures' packages are restored when it is built: a run reaches only Anthropic's API.
