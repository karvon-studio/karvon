# Benchmark: what Karvon's tools save Claude Code

Status: plan, not run yet (October 2026). It is the first step of the document tools: their work starts
only if this benchmark shows that Karvon's tools pay off (see [Decision](#decision)).

## The question

Does Claude Code finish the same task with fewer tokens, for less money and as often when it has Karvon's
tools (`mcp__karvon__*` and "Before the shell") as when it has only its own (Bash, Read, Grep, Glob,
Edit...)? And how does that differ between Claude Opus 5.5, Claude Sonnet 5.5 and Claude Haiku 5.5?

Hypotheses, each checked on its own:

- **H1, tokens and cost.** With Karvon's tools a task costs less: tool results that are already the
  answer (`build_errors`, `test_failures`, `code_read_symbol`, `db_query`) put less in the context than a
  shell command's output or whole files.
- **H2, success.** The tools do not lower the share of tasks done; for runtime and data tasks they raise it.
- **H3, models.** The saving holds on all three models; a smaller model gains as much or more (fewer wrong
  turns), so Haiku 5.5 with Karvon's tools may get done what Sonnet 5.5 without them does, for less.
- **H4, time.** Fewer turns: the wall-clock time falls too.

## What is compared

Every run goes through the same runner, the same Claude Code CLI version and the same flags. Only what
Karvon gives the session differs; both are settings Karvon has already (`settings.json`):

| Arm | Claude Code gets | Settings |
|---|---|---|
| **A, baseline** | its own tools only | `ClaudeKarvonTools: false` |
| **B, Karvon** | its own tools, Karvon's tools, Karvon's system prompt, and Bash calls of what a tool does sent to the tool | `ClaudeKarvonTools: true`, `ClaudeKarvonToolsFirst: true` |
| **C, Karvon without redirection** | as B, but the shell is never sent to the tools | `ClaudeKarvonTools: true`, `ClaudeKarvonToolsFirst: false` |

A against B answers the question; C tells how much of B's saving is the redirection's and how much the
tools' alone. To save money C runs on one model only (Sonnet 5.5).

## Models

| Model | Id | List price per million tokens: input / output / cache read |
|---|---|---|
| Claude Opus 5.5 | `claude-opus-5-5` | $4 / $20 / $0.20 |
| Claude Sonnet 5.5 | `claude-sonnet-5-5` | $2 / $10 / $0.20 |
| Claude Haiku 5.5 | `claude-haiku-5-5` | $0.10 / $0.50 (prompts up to 100K tokens) / about $0.01 |

The ids are exact, never aliases (`opus`, `sonnet`), so a model the CLI maps an alias to later does not
change the benchmark. Prices are Anthropic's list prices as of October 2026 (writing to the cache costs
1.25 times the input price for 5 minutes; Haiku 5.5's cache read is taken as a tenth of its input price);
check them on the pricing page on the day of the run and keep them with the results. The cost every comparison uses is the CLI's own `total_cost_usd`.

**Effort.** Effort changes token use more than anything else, so it is fixed and recorded:

- main sweep: each model at the effort Claude Code gives it by default (what a Karvon user gets), read
  from the session's settings and written into every result;
- second sweep, optional: all three at `medium`, set per session as Karvon's effort slider sets it
  (`effortLevel`), to compare the models at the same depth.

**Billing.** The runs use an API key of a Console workspace of their own, not a Claude subscription: a
subscription's limits would stop long sweeps halfway, and the workspace's spend is then the benchmark's
alone to check against the sum of `total_cost_usd`.

## Tasks

Each task is a folder `bench/tasks/<id>/`:

- `task.yaml`: the prompt (the same in every arm, it never names a tool), the fixture and its commit, the
  category, at most how many turns and minutes;
- `setup.sh`: makes the workspace from a clean copy of the fixture (puts the bug in, registers a data
  source, sets a breakpoint...);
- `check.sh`: after the session, decides pass or fail by itself: the build passes, the tests pass, the
  test files are unchanged, the answer has the expected numbers or names. No model grades, so grading
  costs nothing and does not vary.

The core suite is offline and deterministic: every package is restored when the image is built, and a
run reaches only Anthropic's API.

| Id | Category | Fixture | Task | Karvon tools it can use |
|---|---|---|---|---|
| T00 | calibration | empty folder | "Reply with OK." | none: what each arm costs before any work (tool definitions, system prompt) |
| T01 | build | BlazorSample (.NET) | three compile errors in three files: "The solution does not build. Fix it." | `run_build`, `build_errors` |
| T02 | tests | BlazorSample | a bug makes two tests fail: "Make the tests pass without changing them." | `run_tests`, `test_failures` |
| T03 | code question | Karvon itself, pinned commit | "Where is the Ports API key kept, and which code reads and writes it?" | `code_symbols`, `code_references`, `code_read_symbol` |
| T04 | refactor | BlazorSample | "Add a `currency` parameter to `PriceFormatter.Format` and update every caller." | `code_references`, `code_definition`, `run_build` |
| T05 | runtime | BlazorSample with an API | "GET /api/orders/7 returns a wrong total. Find out why and fix it." | `app_control`, `http_send`, `http_endpoints` |
| T06 | debugger | BlazorSample | the program is stopped at a breakpoint in `Checkout`: "Why is `discount` negative here? Fix it." | `debug_state`, `debug_locals`, `debug_evaluate` |
| T07 | database | SQLite file in the workspace | "Which product sold most in March 2026, and how many customers bought it?" | `db_schema`, `db_query` |
| T08 | TypeScript | small Vite app | a Vitest test fails: "Make the tests pass." | `run_tests`, `test_failures` |
| T09 | Python | small package | a pytest test fails: "Make the tests pass." | `run_tests`, `test_failures` |
| T10 | Go | small module | does not build: "Fix the build." | `run_build`, `build_errors` |
| T11 | Rust | small crate | a cargo test fails: "Make the tests pass." | `run_tests`, `test_failures` |
| T12 | large code question | Karvon itself | "How does a module's Claude tool reach the Claude Code session? List the types it passes through." | `code_*` |
| T13 | control | BlazorSample | "Write a CONTRIBUTING.md for this project." | none: should cost the same in A and B |
| T14 | containers (optional) | compose stack, Docker in the image | the api container keeps restarting: "Find out why and fix it." | `container_list`, `container_logs`, `compose_up` |

BlazorSample is the tests' small Blazor solution (`tests/Karvon.Tests/TestAssets/BlazorSample`); what the
tasks name that it does not have yet (`PriceFormatter`, `Checkout`, an orders API) is added to its copy in
`bench/fixtures`, and the other fixtures are made there too.

T13 is there to keep the suite honest: tasks chosen by Karvon's authors could all favour Karvon's tools,
and on a task the tools cannot help with B must not cost more than A (other than what T00 shows).

Left out of the core suite, as they need services: Pipelines, Pull Requests, Jira, Kubernetes, Servers.
They can come later with the fakes the tests have (`FakeGh`, `FakeKubectl`, `FakeJira`), given to both arms
alike: the baseline's `gh` and `kubectl` are the same fakes Karvon's modules call.

## The runner

Karvon's tools are an MCP server inside Karvon: the CLI reaches them over the control protocol of the
session Karvon starts (`ClaudeToolServer`, an "sdk" server), not over a port. So the runner is Karvon
itself, headless, as the tests run it:

- `bench/Karvon.Bench`, a console app beside the tests: it makes a `MainViewModel` on Avalonia's headless
  platform, opens the task's workspace, waits until its language servers are ready (IntelliSense), and
  starts a Claude Code session through the Claude Code module with the arm's settings, the model and
  the effort;
- it sends the prompt and approves everything the session asks (the CLI's permission requests, and
  Karvon's own for the tools that change things: a build, `debug_control start`, `compose_up`) as a user
  in Bypass permissions would; the runs are in a throwaway container, which is what makes that safe;
- it keeps the session's stream-json transcript, ends the session at its `result` event (or at the
  task's turn or time limit: a failure, its tokens still counted), runs `check.sh` and writes one line of
  results;
- the same runner does arm A: Karvon then starts the CLI without its server, its system prompt and its
  hook, and the flags are otherwise the same.

**Isolation.** Each run gets a fresh copy of the fixture, a fresh `CLAUDE_CONFIG_DIR` (no memory, no
CLAUDE.md of the user's, no plugins or MCP servers) and a fresh `KARVON_CONFIG_DIR`.

**The image** (`bench/Dockerfile`) has the .NET 10 SDK, Node.js, Python, Go, Rust, sqlite3 (so the
baseline can query the database too), Docker for T14, a pinned Claude Code CLI, Karvon built at the commit
under test, the language servers and debuggers already downloaded (Karvon would download them on first
use), and every fixture's packages restored.

**Order.** All runs are shuffled together and run two or three at a time. Run in blocks, one arm's runs
would reuse the prompt cache of the runs just before them (the same system prompt and tool definitions)
and look cheaper than the other's; shuffled, both arms get the same chance.

**Failures that are not the model's.** A run that ends on an API error (429, 529, a network failure) is an
infrastructure failure: it is run again, and the count of them is reported.

## What each run records

One JSON line per run in `bench/results/<date>/runs.jsonl`:

- task, arm, model, effort, repetition, CLI version, Karvon commit, start time;
- `success`, and what `check.sh` said;
- from the `result` event: `total_cost_usd`, `num_turns`, `duration_ms`, `duration_api_ms`, and `usage`:
  `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens`; the use per
  model too (`modelUsage`), since the CLI may call a small model for side work (a title, a compaction);
- from the transcript: every tool call by name (Bash, Read, Grep, `mcp__karvon__*`), the Bash calls sent
  to a tool, how many characters the tool results put in the context, the largest context of one call,
  compactions.

The transcripts stay beside it (`transcripts/<run>.jsonl`), to see why a run cost what it did.

## Repetitions and cost

The model's answers vary, so every task runs **5 times** in each arm and model:

| | Runs |
|---|---|
| A and B, 15 tasks, 3 models, 5 times | 450 |
| C, 15 tasks, Sonnet 5.5, 5 times | 75 |
| **In all** | **525** |

What it costs depends on what a run uses, which the pilot measures first. An estimate, with an assumed
run of 400K tokens read from the cache, 40K written to it, 5K other input and 6K output:

| Model | One run | Runs | About |
|---|---|---|---|
| Opus 5.5 | 0.08 + 0.20 + 0.02 + 0.12 = $0.42 | 150 | $63 |
| Sonnet 5.5 | 0.08 + 0.10 + 0.01 + 0.06 = $0.25 | 225 | $56 |
| Haiku 5.5 | about $0.013 | 150 | $2 |

So about **$120**, and $10–15 for the pilot. The assumptions can be off by several times: T03 and T12 read
a large repository, T00 costs almost nothing. The pilot's real numbers replace them before the full sweep
starts.

## Analysis

The report (`bench/results/<date>/report.md`, made from `runs.jsonl` by `bench/report`) shows:

1. **For each model and arm:** tasks done (of 75), the medians of tokens by kind, cost, turns and time,
   and **cost per task done**: all the runs' cost divided by the runs that passed. Runs that failed cost
   money too, so this counts them.
2. **B against A, task by task.** For each task and model, the ratio of B's median to A's (cost, tokens,
   turns). Over the tasks, their geometric mean with a 95% bootstrap interval (resampling tasks, and the
   runs within them).
3. **Success:** passes per arm and model with a 95% Wilson interval, and the tasks where one arm passes
   and the other does not.
4. **Why:** Bash calls, Karvon tool calls, the Bash calls redirected, and the characters of tool results in
   the context, per category. If B saves, this shows where.
5. **Between models:** the cheapest model and arm per task done, for example whether Haiku 5.5 with
   Karvon's tools does what Sonnet 5.5 without them does.
6. **Overhead:** T00 (fixed cost per session) and T13 (a task the tools cannot help with).

Tokens of different kinds have different prices (a cache read costs a twentieth of an input token on
Opus 5.5), so a sum of all tokens alone would mislead; cost is the primary measure, and the kinds are
shown beside it.

## Decision

Agreed before the run, so the results cannot move the line:

- **Go** for the document tools if, on the core suite (T01–T12), B costs at most **0.8** of A per task done
  (at least 20% less) with the 95% interval's upper end below 1.0 on at least **two of the three models**,
  and B's passes are no more than **5 points** below A's on any model.
- **No go** if B costs as much as A or more on two models, or passes clearly fewer tasks; then the report
  says why (the overhead of T00? one category?) and that is fixed first.
- **In between:** the categories where B saves decide which kind of tool is worth building first.

What carries over to documents: if B's saving comes from tool results that put less in the context (step
4 of the analysis), document tools that leave Office's XML out of the context should save the same way.

## Then: documents

If the decision is go:

1. **One fork per document type,** with the tools agents need added: WordCraft
   ([storytold/wordcraft](https://github.com/storytold/wordcraft), MIT or Apache-2.0) for Word, which has
   an MCP server already (`wordcraft-cli mcp`) but reads a document only whole (`get_text`) or with all its
   formatting (`inspect_document`). The tools to add: the outline (the headings, where each starts), the
   text of a range of paragraphs, finding with the lines around, and small edits. Excel, PowerPoint and PDF
   each get a project chosen the same way.
2. **The `Karvon.Documents` module:** the forks' CLIs as components, downloaded as the language servers
   are; their MCP servers given to Claude Code confined to the workspace (WordCraft's
   `--automation-read-root` and `--automation-write-root`), with Claude as the author of tracked changes;
   and shell rules that send `unzip x.docx`, `python -c "import docx"` and `pandoc x.docx` to them.
3. **The same benchmark, with document tasks:** read a section of a 60-page `.docx`, fix a table in it,
   total a column of a 10,000-row `.xlsx`, find a clause in a PDF. The arms: the shell (Python, unzip),
   the upstream MCP server as it is, and the forks' tools through Karvon.

## Steps

1. The runner, the image, T00–T02 and the report (`bench/`).
2. The pilot: T00–T02, all three models, A and B, twice each (36 runs). It checks the runner and measures
   what a run costs, to fix the budget.
3. The rest of the tasks, then the full sweep (525 runs).
4. The report, and the decision as above.
