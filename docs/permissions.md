# What Claude may do in Karvon Studio

Karvon Studio starts your own `claude` CLI and gives it Karvon's tools (`mcp__karvon__*`). This page lists what each of
them may do, what asks first and what refuses, where your secrets are kept and what can reach them, and, for each
boundary, whether it is enforced (with the test that proves it) or planned.

## Who decides a call

A call goes through these, in this order:

1. **Karvon's guard of the secrets** (a PreToolUse hook, on by default: MCP Servers > karvon > Keep the secrets away)
   refuses Claude Code's own Read, Edit, Write, NotebookEdit, Grep and Bash when they reach a file that holds secrets
   or ask a secret store (`secret-tool`, `security find-*`, `gh auth token`, `kubectl config view --raw`,
   `git credential fill`, the cloud CLIs' token commands). It is a guard against what slips: the shell can reach
   secrets in ways no list has. The sandbox (below) is the boundary.
2. **The tool itself** refuses what it cannot do here, before anyone is asked: a server Claude may not reach in this
   folder, a data source of a folder not trusted yet, a change to a read-only data source.
3. **Your rules for Karvon's tools** (MCP Servers > karvon, one per tool; one per target in `ClaudeToolRules` of
   Karvon's `settings.json`, as `server_exec@prod-1`, `db_query@shop` or `k8s_scale@prod`): allow, ask or deny.
   Karvon's tool server enforces them on every call, whatever Claude Code's permission mode let through.
4. **Without a rule, the call's access** decides: reading and running go at once, a change asks, and a destructive call
   asks every time, whatever a rule says. In a folder you have not trusted yet, only reading goes at once.
5. **You**, on Claude Code's prompt, or on Karvon's own card when Claude Code did not ask (Bypass permissions, auto mode,
   an allow rule of its own). A yes lets that one call through once. Plan mode refuses Karvon's changes itself.

Every step is written to the session's [audit trail](#the-audit-trail).

## Karvon's tools

| Access | What it means | Tools |
| --- | --- | --- |
| Read | at once | `build_errors`, `test_failures`, `test_coverage` (results), `debug_state`, `debug_locals`, `debug_evaluate`, `app_control` (status), `editor_selection`, `open_document`, `code_diagnostics`, `find_in_files`, `code_symbols`, `code_outline`, `code_read_symbol`, `code_definition`, `code_references`, `code_implementations`, `db_connections`, `db_schema`, `db_explain`, `container_list`, `container_logs`, `container_inspect`, `server_list`, `server_logs`, `jira_sites`, `jira_search`, `jira_issue`, `jira_projects`, `tunnel_list`, `pipeline_list`, `pipeline_jobs`, `pipeline_log`, `pr_list`, `pr_view`, `pr_diff`, `k8s_contexts`, `k8s_get`, `k8s_describe`, `k8s_logs`, `k8s_events`, `http_endpoints` |
| Run | at once in a trusted folder, asks in one that is not | `run_build`, `run_tests`, `test_coverage` (a run), `debug_control` (stepping), `tunnel_close`, `http_send` (to this machine) |
| Write | asks, one call at a time | `app_control` (start, restart, stop), `debug_control` (start, stop), `container_restart`, `compose_up`, `server_exec`, `jira_create`, `jira_transition`, `jira_comment`, `jira_assign`, `tunnel_open`, `pipeline_retry`, `pipeline_cancel`, `pr_create`, `pr_comment`, `pr_review`, `pr_merge`, `k8s_restart`, `k8s_scale`, `http_send` (to another machine) |
| Destructive | asks every time; no rule lets it through | `container_remove`, `k8s_delete_pod`; `server_exec` on a production server; `k8s_restart` and `k8s_scale` in a production context, and scaling to zero |

`db_query` is as its SQL is: a `SELECT` reads, an `INSERT`, `UPDATE` or `DELETE` with a `WHERE` writes, and `DROP`,
`TRUNCATE` or an `UPDATE` or `DELETE` without a `WHERE` is destructive. A read-only data source refuses every change, in
every mode.

What these tools give Claude is checked first: the secrets Karvon keeps and the tokens known by their form are hidden
(`«secret: a GitHub token»`), passwords in connection strings and URLs are masked, and what others wrote (an issue, a
pull request, logs, an HTTP response, a table's rows) is marked as data. Anything in it that reads as an instruction to
Claude is shown to you under the call.

## Where your secrets are, and what can reach them

| Secret | Kept in | What reaches it |
| --- | --- | --- |
| Jira, data source, tunnel, pipeline and SSH passwords Karvon keeps | Karvon's vault (`vault.db` in its settings folder, each entry AES-256-GCM, bound to its name) | Karvon. Its key is in the system keyring (`karvon-vault/key`); Claude's tools are refused the file and the keyring by the guard, and by the sandbox when it is on |
| The vault's key | the system keyring | any process of yours that asks the keyring; Claude's shell is refused `secret-tool` by the guard, and on Linux the sandbox shuts out the keyring's socket |
| SSH keys, kubeconfig, cloud CLI credentials, `gh` and `glab` tokens, Claude Code's own sign-in | their usual files under your home | the guard refuses Claude's tools these files; the sandbox denies reading them |
| A project's `.env`, keys, certificates, `secrets.json` | the project | the guard refuses Claude's tools these files by name |
| Secret-looking environment variables | Karvon's environment | not passed to `claude`: their names are listed in Output (Claude) |
| What you type in a prompt | the chat | a card holds a prompt with a secret in it: send it hidden, as it is, or not at all |

## Boundaries

| Boundary | State | Proved by |
| --- | --- | --- |
| Secret variables kept from `claude` | enforced | `ClaudeSecretsTests.Claude_Code_is_started_without_them`, `ClaudeNegativeTests` |
| The guard of the secrets (Claude Code's own tools) | enforced, as a guard | `ClaudeSecretsTests.Claude_Codes_tools_may_not_read_change_or_print_them`, `A_session_denies_Claude_Codes_own_tools_the_secrets`, `ClaudeNegativeTests` |
| The vault: encrypted, and not openable by Claude's tools | enforced | `ClaudeSecretsTests.The_vault_keeps_secrets_encrypted_in_its_database_and_takes_over_the_keyrings`, `Claude_Codes_tools_may_not_open_the_vault` |
| Claude Code's sandbox for its shell (off at first: MCP Servers > karvon) | enforced by Claude Code when on; Karvon checks what it is given | `ClaudeSecretsTests.The_sandbox_keeps_the_secrets_unreadable_and_no_command_out_of_it` |
| Secrets hidden in what Karvon's tools give | enforced | `ClaudeSecretsTests.No_tool_of_Karvons_tells_Claude_a_secret`, `ClaudeNegativeTests` |
| A prompt with a secret held for your say | enforced | `ClaudeSecretsTests.A_prompt_with_a_secret_waits_for_the_users_say` |
| What others wrote marked as data, instructions in it shown | enforced (the marking; Claude's following it is the model's) | `ClaudeSecretsTests.What_others_wrote_reaches_Claude_as_data_it_cannot_close`, `The_chat_shows_what_reads_as_instructions_to_Claude`, `ClaudeNegativeTests` |
| Your rules, whatever the permission mode | enforced | `ClaudeToolPolicyTests.The_tool_server_enforces_them_whatever_Claude_Code_let_through`, `Bypass_permissions_does_not_skip_them`, `ClaudeNegativeTests` |
| A destructive call asks every time | enforced | `ClaudeToolPolicyTests.A_rule_allows_asks_or_denies_and_a_destructive_call_always_asks` |
| Workspace trust: a repository's `.claude` settings (hooks, allow rules), `.mcp.json`, Gemini servers and data sources wait for you | enforced | `ClaudeToolPolicyTests.Trusted_are_the_users_folders_and_what_is_in_them`, `A_folder_not_trusted_yet_says_so_until_the_user_trusts_it`, `McpTests` |
| Servers tied to the folder | enforced | `ServersTests.Claude_lists_the_servers_and_a_production_command_is_destructive`, `ClaudeNegativeTests` |
| Read-only data sources and plan mode refuse changes | enforced | `ClaudeKarvonToolsTests.Database_tools_describe_query_and_refuse_changes_to_read_only_sources`, `ClaudeNegativeTests` (plan) |
| The audit trail | enforced | `ClaudeToolPolicyTests.Each_session_keeps_what_was_asked_what_decided_it_and_what_ran`, `The_trail_keeps_what_Karvons_hooks_refused_without_the_secret_and_thirty_days` |
| What Claude Code's own tools read (a file's contents, a command's output) scanned before the model sees it | enforced when on (off at first: MCP Servers > karvon > Scan what Claude Code sends the model), a gateway at `ANTHROPIC_BASE_URL`; a subscription's sign-in goes through it too | `ClaudeSecretsTests.The_gateway_hides_the_secrets_in_what_Claude_Code_sends_the_model`, `A_session_with_the_gateway_is_given_its_base_url` |
| The vault unlocked by a passphrase, Touch ID or Windows Hello instead of the keyring | planned | |
| The IDE's own restricted mode for a folder not trusted yet (language servers' design-time builds, tasks) | planned | |
| Rules and folder ties for ports, containers, clusters and data sources | planned | |

`ClaudeNegativeTests` runs a session in default, accept edits, plan, auto and Bypass permissions modes. In each, Claude
tries to read an SSH key, the keyring, a project's `.env` and the environment, run a command on a server, delete a
table's rows, forward a port, follow an issue's instructions and pass on a secret from a tool's output, and you say no
to whatever is asked. Each attempt is refused, hidden or shown to you, and the audit trail shows that only reading ran.

## The audit trail

Each session writes a JSON line for every tool call Claude asked for (its input with the secrets hidden), what decided
it, what Karvon's tools ran (when, how long, whether it failed, the size of its output, what was hidden of it) and what
Claude got back. The trails are in `audit/` in Karvon's settings folder, readable by you alone, and are kept for thirty
days. Past conversations > This session's audit trail opens the current one.
