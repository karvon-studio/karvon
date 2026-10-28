#!/usr/bin/env python3
"""The sweep (bench/README.md): every task x arm x repetition, shuffled together, a few at a time, each run in a
container of its own (karvon-bench) with its own share of the machine. One JSON line per run in
results/<name>/runs.jsonl; a run that ended on the API's error (overloaded, a rate or usage limit) is the
infrastructure's and runs again. Run again with the same --name, it goes on where it stopped.

  python3 bench/run.py --name 2026-10-28-opus --model claude-opus-5-5 --arms A,B --reps 3 [--tasks T00,T01] [--parallel 3]

The subscription's token (made with `claude setup-token`) is read from ~/.config/karvon-bench/oauth-token and given to
the containers in their environment only; it is never written anywhere else.
"""
import argparse
import datetime
import json
import os
import random
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

BENCH = Path(__file__).resolve().parent
TOKEN_FILE = Path.home() / ".config" / "karvon-bench" / "oauth-token"


def tasks(selected):
    found = {}
    for path in sorted((BENCH / "tasks").glob("*/task.json")):
        task = json.loads(path.read_text())
        found[task["id"]] = task
    return [found[t] for t in selected] if selected else list(found.values())


def done_runs(runs_file):
    """The runs already in runs.jsonl (not the infrastructure's failures): they are not run again."""
    done = set()
    if runs_file.exists():
        for line in runs_file.read_text().splitlines():
            run = json.loads(line)
            if not run.get("infra"):
                done.add(run["id"])
    return done


def docker_run(task, run_id, arm, model, effort, root, token):
    folder = root / "runs" / run_id
    work, out = folder / "work", folder / "out"
    if folder.exists():
        subprocess.run(["rm", "-rf", str(folder)], check=True)
    work.mkdir(parents=True)
    out.mkdir(parents=True)
    name = "karvon-bench-" + run_id.lower().replace("_", "-")
    command = ["docker", "run", "--rm", "--name", name, "--cpus", "4", "--memory", "8g",
               "-e", f"TASK={task['id']}", "-e", f"ARM={arm}", "-e", f"MODEL={model}", "-e", "CLAUDE_CODE_OAUTH_TOKEN",
               "-v", f"{work}:/work", "-v", f"{out}:/out"]
    if effort:
        command += ["-e", f"EFFORT={effort}"]
    # T14's compose stack needs a Docker daemon of its own, which needs a privileged container.
    if task.get("docker") or task.get("fixture") == "compose":
        command += ["--privileged", "-e", "BENCH_DOCKER=1"]
    command.append("karvon-bench")
    limit = (task.get("maxMinutes", 15) + 25) * 60
    env = dict(os.environ, CLAUDE_CODE_OAUTH_TOKEN=token)
    started = time.time()
    try:
        subprocess.run(command, env=env, timeout=limit, stdout=subprocess.DEVNULL, stderr=(out / "docker.log").open("w"))
    except subprocess.TimeoutExpired:
        subprocess.run(["docker", "kill", name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    wall = time.time() - started

    def read(name, default=None):
        path = out / name
        return path.read_text().strip() if path.exists() else default

    run = json.loads(read("run.json", "{}") or "{}")
    check_exit = read("check.exit")
    record = {
        "id": run_id, "task": task["id"], "category": task.get("category"), "arm": arm, "model": model,
        "success": check_exit == "0" and not run.get("infra"),
        "check": (read("check.log", "") or "").splitlines()[-1:] or [""],
        "wallSeconds": round(wall, 1),
        **{k: v for k, v in run.items() if k not in ("task", "arm", "model")},
        **json.loads(read("times.json", "{}") or "{}"),
        "cli": read("cli.txt"),
    }
    record["check"] = record["check"][0]
    if not run:
        record["infra"] = True
        record["error"] = "no run.json: " + (read("runner.log", "") or "")[-300:]
    return record


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--arms", default="A,B")
    parser.add_argument("--reps", type=int, default=3)
    parser.add_argument("--tasks", default="")
    parser.add_argument("--parallel", type=int, default=3)
    parser.add_argument("--effort", default="")
    parser.add_argument("--seed", type=int, default=20261028)
    parser.add_argument("--retries", type=int, default=3)
    args = parser.parse_args()

    if not TOKEN_FILE.exists():
        sys.exit(f"{TOKEN_FILE} is not there: make it with `claude setup-token` (chmod 600).")
    token = TOKEN_FILE.read_text().strip()
    root = BENCH / "results" / args.name
    root.mkdir(parents=True, exist_ok=True)
    runs_file = root / "runs.jsonl"
    selected = [t for t in args.tasks.split(",") if t]
    plan = [(task, f"{task['id']}-{arm}-{rep}", arm)
            for task in tasks(selected) for arm in args.arms.split(",") for rep in range(1, args.reps + 1)]
    # Shuffled together: in blocks, one arm's runs would reuse the prompt cache of the runs before them.
    random.Random(args.seed).shuffle(plan)
    done = done_runs(runs_file)
    todo = [p for p in plan if p[1] not in done]
    (root / "plan.json").write_text(json.dumps({"model": args.model, "arms": args.arms, "reps": args.reps, "seed": args.seed,
                                                "effort": args.effort or None, "runs": [p[1] for p in plan],
                                                "started": datetime.datetime.now().isoformat()}, indent=2))
    print(f"{len(todo)} of {len(plan)} runs to go, {args.parallel} at a time", flush=True)
    lock = threading.Lock()
    pause_until = [0.0]
    counter = [0]

    def one(entry):
        task, run_id, arm = entry
        for attempt in range(1, args.retries + 2):
            while time.time() < pause_until[0]:
                time.sleep(10)
            record = docker_run(task, run_id, arm, args.model, args.effort, root, token)
            record["attempt"] = attempt
            with lock:
                with runs_file.open("a") as f:
                    f.write(json.dumps(record) + "\n")
                counter[0] += 1
                cost = (record.get("result") or {}).get("total_cost_usd")
                print(f"[{counter[0]}] {run_id}: {'pass' if record['success'] else 'fail'}"
                      f"{' (infra)' if record.get('infra') else ''}, {record['wallSeconds']}s, ${cost}", flush=True)
            if not record.get("infra"):
                return
            # A usage or rate limit: everyone waits a while before the next try.
            text = json.dumps(record).lower()
            if "usage limit" in text or "rate limit" in text or "429" in text:
                with lock:
                    pause_until[0] = max(pause_until[0], time.time() + 15 * 60)
            else:
                time.sleep(30)

    with ThreadPoolExecutor(args.parallel) as pool:
        list(pool.map(one, todo))
    print("done:", runs_file, flush=True)


if __name__ == "__main__":
    main()
