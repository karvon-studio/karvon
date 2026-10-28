#!/usr/bin/env python3
"""results/<name>/report.md from results/<name>/runs.jsonl and the runs' transcripts (docs/benchmark.md, Analysis).

  python3 bench/report.py --name 2026-10-28-opus
"""
import argparse
import json
import math
import random
import statistics
from collections import Counter, defaultdict
from pathlib import Path

BENCH = Path(__file__).resolve().parent
CORE = [f"T{i:02d}" for i in range(1, 13)]
# The large projects' tasks, reported beside the core ones.
LARGE = ["T15", "T16", "T17"]


def load(root):
    runs = {}
    for line in (root / "runs.jsonl").read_text().splitlines():
        run = json.loads(line)
        if run.get("infra"):
            continue
        runs[run["id"]] = run  # the last attempt of a run counts
    for run in runs.values():
        run.update(transcript_stats(root / "runs" / run["id"] / "out" / "transcript.jsonl"))
    return list(runs.values())


def transcript_stats(path):
    """Tool calls by name, the Bash calls Karvon sent to its tools, and the characters tool results put in the context."""
    tools, result_chars, redirected = Counter(), 0, 0
    if not path.exists():
        return {"tools": {}, "resultChars": 0, "redirected": 0}
    for raw in path.read_text().splitlines():
        try:
            line = json.loads(raw)["line"]
        except (json.JSONDecodeError, KeyError):
            continue
        message = line.get("message") or {}
        if line.get("type") == "assistant" and line.get("parent_tool_use_id") is None:
            for block in message.get("content") or []:
                if block.get("type") == "tool_use":
                    tools[block.get("name", "?")] += 1
        if line.get("type") == "user":
            for block in message.get("content") or [] if isinstance(message.get("content"), list) else []:
                if block.get("type") == "tool_result":
                    content = block.get("content")
                    text = content if isinstance(content, str) else "".join(c.get("text", "") for c in content or [] if isinstance(c, dict))
                    result_chars += len(text)
                    if "Karvon" in text and "tool" in text and ("instead" in text or "Use " in text) and block.get("is_error"):
                        redirected += 1
    return {"tools": dict(tools), "resultChars": result_chars, "redirected": redirected}


def value(run, key):
    result = run.get("result") or {}
    usage = result.get("usage") or {}
    return {
        "cost": result.get("total_cost_usd"),
        "turns": result.get("num_turns"),
        "seconds": (run.get("durationMs") or 0) / 1000,
        "apiSeconds": (result.get("duration_api_ms") or 0) / 1000,
        "input": usage.get("input_tokens"),
        "cacheWrite": usage.get("cache_creation_input_tokens"),
        "cacheRead": usage.get("cache_read_input_tokens"),
        "output": usage.get("output_tokens"),
        "resultChars": run.get("resultChars"),
        # Every token the model read (new, written to the cache, read from it), and what it wrote.
        "tokensIn": sum(usage.get(k) or 0 for k in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens")) if usage else None,
        "tokensOut": usage.get("output_tokens"),
    }[key]


def tokens(n):
    """A token count as people say it: 950, 12.3K, 4.56M."""
    if n is None:
        return "–"
    if n >= 1_000_000:
        return f"{n / 1_000_000:.2f}M"
    if n >= 1_000:
        return f"{n / 1_000:.1f}K"
    return f"{n:.0f}"


def median(values):
    values = [v for v in values if v is not None]
    return statistics.median(values) if values else None


def wilson(passed, n, z=1.96):
    if n == 0:
        return (0, 0)
    p = passed / n
    centre = (p + z * z / (2 * n)) / (1 + z * z / n)
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    return (max(0, centre - half), min(1, centre + half))


def ratio_ci(by_task, key, rounds=2000, seed=7):
    """Over the tasks: the geometric mean of B's median over A's, and a 95% bootstrap interval (tasks, and runs in them)."""
    def gm(sample):
        logs = []
        for runs_a, runs_b in sample:
            a, b = median(value(r, key) for r in runs_a), median(value(r, key) for r in runs_b)
            if a and b:
                logs.append(math.log(b / a))
        return math.exp(sum(logs) / len(logs)) if logs else None
    pairs = [(by_task[t]["A"], by_task[t]["B"]) for t in by_task if by_task[t]["A"] and by_task[t]["B"]]
    point = gm(pairs)
    rng = random.Random(seed)
    boots = []
    for _ in range(rounds):
        sample = [rng.choice(pairs) for _ in pairs]
        sample = [([rng.choice(a) for _ in a], [rng.choice(b) for _ in b]) for a, b in sample]
        g = gm(sample)
        if g:
            boots.append(g)
    boots.sort()
    if not boots:
        return point, None, None
    return point, boots[int(0.025 * len(boots))], boots[int(0.975 * len(boots)) - 1]


def fmt(v, digits=2):
    if v is None:
        return "–"
    if isinstance(v, float):
        return f"{v:.{digits}f}"
    return str(v)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", required=True)
    args = parser.parse_args()
    root = BENCH / "results" / args.name
    runs = load(root)
    arms = sorted({r["arm"] for r in runs})
    models = sorted({r["model"] for r in runs})
    out = [f"# Benchmark {args.name}", "", f"{len(runs)} runs; models: {', '.join(models)}; arms: {', '.join(arms)}.", ""]

    for model in models:
        mine = [r for r in runs if r["model"] == model]
        out += [f"## {model}", "", "### Per arm", "",
                "| Arm | Passed | Median cost | Median turns | Median time (s) | Model time (s) | Cache read | Cache write | Input | Output | Cost per task done | Time per task done (s) |",
                "|---|---|---|---|---|---|---|---|---|---|---|---|"]
        for arm in arms:
            rs = [r for r in mine if r["arm"] == arm]
            passed = sum(r["success"] for r in rs)
            cost = sum(value(r, "cost") or 0 for r in rs)
            secs = sum(value(r, "seconds") for r in rs)
            low, high = wilson(passed, len(rs))
            out.append(f"| {arm} | {passed}/{len(rs)} ({low:.0%}–{high:.0%}) | ${fmt(median(value(r, 'cost') for r in rs), 3)} | "
                       f"{fmt(median(value(r, 'turns') for r in rs))} | {fmt(median(value(r, 'seconds') for r in rs), 0)} | "
                       f"{fmt(median(value(r, 'apiSeconds') for r in rs), 0)} | {fmt(median(value(r, 'cacheRead') for r in rs))} | "
                       f"{fmt(median(value(r, 'cacheWrite') for r in rs))} | {fmt(median(value(r, 'input') for r in rs))} | "
                       f"{fmt(median(value(r, 'output') for r in rs))} | "
                       f"{'$' + fmt(cost / passed, 3) if passed else '–'} | {fmt(secs / passed, 0) if passed else '–'} |")

        out += ["", "### Tokens", "",
                "What the model read (new input, written to the cache, read from it) and wrote, summed over the arm's runs and per run (median).", "",
                "| Arm | Runs | Read in all | of it new | cache write | cache read | Written in all | Read per run | Written per run |",
                "|---|---|---|---|---|---|---|---|---|"]
        for arm in arms:
            rs = [r for r in mine if r["arm"] == arm]
            total = lambda key: sum(value(r, key) or 0 for r in rs)
            out.append(f"| {arm} | {len(rs)} | {tokens(total('tokensIn'))} | {tokens(total('input'))} | {tokens(total('cacheWrite'))} | "
                       f"{tokens(total('cacheRead'))} | {tokens(total('tokensOut'))} | {tokens(median(value(r, 'tokensIn') for r in rs))} | "
                       f"{tokens(median(value(r, 'tokensOut') for r in rs))} |")

        by_task = defaultdict(lambda: defaultdict(list))
        for r in mine:
            by_task[r["task"]][r["arm"]].append(r)
        if {"A", "B"} <= set(arms):
            out += ["", "### B against A, task by task (B's median / A's)", "",
                    "| Task | Category | Passed A | Passed B | Cost | Turns | Time | Tokens read | Tokens written | Tool result chars | Tokens read A → B (median) |",
                    "|---|---|---|---|---|---|---|---|---|---|---|"]
            for task in sorted(by_task):
                a, b = by_task[task]["A"], by_task[task]["B"]

                def r(key):
                    ma, mb = median(value(x, key) for x in a), median(value(x, key) for x in b)
                    return f"{mb / ma:.2f}" if ma and mb else "–"
                category = (a or b)[0].get("category")
                out.append(f"| {task} | {category} | {sum(x['success'] for x in a)}/{len(a)} | {sum(x['success'] for x in b)}/{len(b)} | "
                           f"{r('cost')} | {r('turns')} | {r('seconds')} | {r('tokensIn')} | {r('tokensOut')} | {r('resultChars')} | "
                           f"{tokens(median(value(x, 'tokensIn') for x in a))} → {tokens(median(value(x, 'tokensIn') for x in b))} |")
            core = {t: v for t, v in by_task.items() if t in CORE}
            large = {t: v for t, v in by_task.items() if t in LARGE}
            for group, name in [(core, "the core tasks (T01–T12)"), (large, "the large projects' tasks (" + ", ".join(sorted(large)) + ")")]:
                if not group:
                    continue
                out += ["", f"Over {name}, the geometric mean of the ratios, with a 95% bootstrap interval:", ""]
                for key, label in [("cost", "cost"), ("tokensIn", "tokens read"), ("tokensOut", "tokens written"), ("turns", "turns"), ("seconds", "time"), ("apiSeconds", "model time")]:
                    point, low, high = ratio_ci(group, key)
                    out.append(f"- {label}: {fmt(point)} ({fmt(low)}–{fmt(high)})")

            def per_done(arm, key):
                rs = [x for t in core for x in core[t][arm]]
                passed = sum(x["success"] for x in rs)
                return (sum(value(x, key) or 0 for x in rs) / passed, passed, len(rs)) if passed else (None, 0, len(rs))
            cost_a, pass_a, n_a = per_done("A", "cost")
            cost_b, pass_b, n_b = per_done("B", "cost")
            time_a, _, _ = per_done("A", "seconds")
            time_b, _, _ = per_done("B", "seconds")
            cost_ratio = cost_b / cost_a if cost_a and cost_b else None
            time_ratio = time_b / time_a if time_a and time_b else None
            _, _, cost_high = ratio_ci(core, "cost")
            points = (pass_b / n_b - pass_a / n_a) * 100 if n_a and n_b else 0
            out += ["", "### The decision's measures (core tasks)", "",
                    f"- cost per task done, B / A: {fmt(cost_ratio)} (go needs at most 0.80, the interval's upper end below 1.0: {fmt(cost_high)})",
                    f"- passes: A {pass_a}/{n_a}, B {pass_b}/{n_b} ({points:+.0f} points; go needs no less than −5)",
                    f"- time per task done, B / A: {fmt(time_ratio)} (go needs at most 1.10)",
                    "",
                    "The decision of docs/benchmark.md takes two of the three models; this is one of them."]

        out += ["", "### Tool calls (sums over the runs)", "", "| Arm | Category | Bash | Read | Grep/Glob | Karvon's | Redirected Bash |", "|---|---|---|---|---|---|---|"]
        for arm in arms:
            cats = defaultdict(Counter)
            redirected = Counter()
            for r in mine:
                if r["arm"] != arm:
                    continue
                for name, count in r.get("tools", {}).items():
                    cats[r.get("category")][name] += count
                redirected[r.get("category")] += r.get("redirected", 0)
            for cat in sorted(cats, key=str):
                c = cats[cat]
                karvon = sum(v for k, v in c.items() if k.startswith("mcp__karvon__"))
                out.append(f"| {arm} | {cat} | {c['Bash']} | {c['Read']} | {c['Grep'] + c['Glob']} | {karvon} | {redirected[cat]} |")

        out += ["", "### Overhead", ""]
        for task in ("T00", "T13"):
            if task in by_task:
                out.append(f"- {task}: " + ", ".join(
                    f"{arm} median ${fmt(median(value(x, 'cost') for x in by_task[task][arm]), 4)}, {fmt(median(value(x, 'seconds') for x in by_task[task][arm]), 0)} s"
                    for arm in arms if by_task[task][arm]))
        limits = Counter((r["arm"], r.get("limit")) for r in mine if r.get("limit"))
        if limits:
            out += ["", "Runs stopped at a limit: " + ", ".join(f"{arm} {limit}: {n}" for (arm, limit), n in sorted(limits.items()))]
        out.append("")
    (root / "report.md").write_text("\n".join(out))
    print(root / "report.md")


if __name__ == "__main__":
    main()
