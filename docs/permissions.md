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
| Read | at once | `build_errors`, `test_failures`, `test_coverage` (results), `debug_state`, `debug_locals`, `debug_evaluate`, `app_control` (status), `editor_selection`, `open_document`, `code_diagnostics`, `find_in_files`, `code_symbols`, `code_outline`, `code_read_symbol`, `code_definition`, `code_references`, `code_implementations`, `db_connections`, `db_schema`, `db_explain`, `container_list`, `container_logs`, `container_inspect`, `server_list`, `server_logs`, `jira_sites`, `jira_search`, `jira_issue`, `jira_projects`, `tunnel_list`, `pipeline_list`, `pipeline_jobs`, `pipeline_log`, `pr_list`, `pr_view`, `pr_diff`, `k8s_contexts`, `k8s_get`, `k8s_describe`, `k8s_logs`, `k8s_events`, `http_endpoints`, `document_open`, `document_tools`, `document_call` (a tool that reads), `document_close`, `browser_snapshot`, `browser_screenshot`, `browser_logs`, `browser_tabs` (list), `browser_wait`, `browser_test` (its steps), `git_status`, `git_diff`, `git_log` |
| Run | at once in a trusted folder, asks in one that is not | `run_build`, `run_tests`, `test_coverage` (a run), `debug_control` (stepping), `tunnel_close`, `http_send` (to this machine), `db_export` (to a new file), `document_call` (a change in memory), `browser_open` (this machine, or a site open in the browser already), `browser_click`, `browser_type`, `browser_select`, `browser_press`, `browser_tabs` (select, close), `git_fetch`, `package_restore` |
| Write | asks, one call at a time | `app_control` (start, restart, stop), `debug_control` (start, stop), `container_restart`, `compose_up`, `server_exec`, `jira_create`, `jira_transition`, `jira_comment`, `jira_assign`, `tunnel_open`, `pipeline_retry`, `pipeline_cancel`, `pr_create`, `pr_comment`, `pr_review`, `pr_merge`, `k8s_restart`, `k8s_scale`, `http_send` (to another machine), `db_export` (over a file that exists), `db_add_connection`, `document_save`, `document_call` (naming a file), `browser_open` (a site not open yet), `browser_click` and `browser_type` that send a form, `browser_press` (Enter in a form), `browser_eval`, `browser_login`, `browser_test` (a test written), `git_commit`, `git_branch`, `git_pull`, `git_push`, `package_add` |
| Destructive | asks every time; no rule lets it through | `run_outside_sandbox`, `container_remove`, `k8s_delete_pod`; `server_exec` on a production server; `k8s_restart` and `k8s_scale` in a production context, and scaling to zero |

`db_query` is as its SQL is: a `SELECT` reads, an `INSERT`, `UPDATE` or `DELETE` with a `WHERE` writes, and `DROP`,
`TRUNCATE` or an `UPDATE` or `DELETE` without a `WHERE` is destructive. A read-only data source refuses every change, in
every mode.

In Karvon's browser Claude never types a password: `browser_type` refuses a password field, and `browser_login` has
Karvon fill in a sign-in it keeps (the password in the vault) without Claude seeing it. A page's outline never shows a
password field's value, and what a page says reaches Claude marked as data.

What these tools give Claude is checked first: the secrets Karvon keeps and the tokens known by their form are hidden
(`«secret: a GitHub token»`), passwords in connection strings and URLs are masked, and what others wrote (an issue, a
pull request, logs, an HTTP response, a table's rows) is marked as data. Anything in it that reads as an instruction to
Claude is shown to you under the call.

## Where your secrets are, and what can reach them

| Secret | Kept in | What reaches it |
| --- | --- | --- |
| Jira, data source, tunnel, pipeline and SSH passwords Karvon keeps | Karvon's vault (`vault.db` in its settings folder, each entry AES-256-GCM, bound to its name; the entries' names are not encrypted). A data source's password is never in a server list's file: while the vault is locked, or with none, it is kept until Karvon closes | Karvon. Its key is in the system keyring (`karvon-vault/key`); Claude's tools are refused the file and the keyring by the guard, and by the sandbox when it is on. A copy of `vault.db` taken elsewhere opens for no one: without your keyring's key nothing in it reads, and with a passphrase it opens for the passphrase only |
| The vault's key | the system keyring; with a passphrase (Tools > Vault > Set Passphrase), in no keyring: wrapped in the vault by a key drawn from the passphrase, which Karvon asks for as it starts | from the keyring, any process of yours that asks it (Claude's shell is refused `secret-tool` by the guard, and on Linux the sandbox shuts out the keyring's socket); with a passphrase, Karvon alone, once it is given |
| SSH keys, kubeconfig, cloud CLI credentials, `gh` and `glab` tokens, Claude Code's own sign-in | their usual files under your home | the guard refuses Claude's tools these files; the sandbox denies reading them |
| A project's `.env`, keys, certificates, `secrets.json` | the project | the guard refuses Claude's tools these files by name |
| The passwords in a project's settings (`appsettings*.json`, YAML, a connection string in code) | the project | Claude's tools read the files, which code needs; what they give the model has the passwords marked, and Claude uses them in an edit or a command without seeing them. `db_add_connection` makes a connection string a data source, its password in Karvon's vault |
| Secret-looking environment variables | Karvon's environment | not passed to `claude`: their names are listed in Output (Claude) |
| What you type in a prompt | the chat | a card holds a prompt with a secret in it: send it hidden, as it is, or not at all |
| The sites you are signed in to in Karvon's browser (its cookies) | Karvon's Chrome profile | Chrome, which Karvon drives through a pipe only it holds: no debugging port, so no other program (nor another user of the machine) can drive it or read its cookies, and it closes with Karvon. On Windows it has a debugging port on 127.0.0.1 that any program of the machine can reach while it runs |
| The Agents panel's conversations | `agents/` in Karvon's settings folder | you alone: its folders and files are readable by their owner only |

## Karvon's strict sandbox

MCP Servers > karvon > "Keep the agents to the project (strict sandbox)" (off at first). Where the sandbox above keeps the
places that hold secrets from Claude's shell, this one keeps everything of yours from it but the project:

| | The agent's shell and file tools |
| --- | --- |
| The project | read and changed |
| The toolchains in your home folder (.NET, Node, Java, Rust, Go, Python, Arche), the package caches, git's settings | read only. A cache is not changed: what is put there would run in every project's build, outside the sandbox |
| The system's programs and libraries | read only |
| The rest of your home folder, other disks (`/mnt`, `/media`), `/root`, `/var/log` | not there |
| Your keyring's and ssh-agent's sockets | not there |
| The project's `.git`, `.claude`, `.mcp.json`, `.vscode`, `.idea` | read only: what is put there would run later outside the sandbox (a git hook, a server at the next session) |
| The folders you give (Folders Claude may read / change) | read, or changed. Your home folder itself, and a folder that is or holds a place with secrets, are refused |
| The network | Claude Code: the package registries and code hosts, any other host asks. The Agents panel's shell: none |

- **Claude Code** runs its shell in its own sandbox with these rules, and does not start where the sandbox cannot (on
  Linux it needs bubblewrap and socat; Karvon says which is missing). Its own file tools (Read, Edit, Write, Grep, Glob)
  are kept to the same places by Karvon's hook.
- **The Agents panel** (Claude API, OpenRouter, BytePlus ModelArk, DeepSeek, Groq, Mistral, Ollama, LM Studio) runs its
  shell in bubblewrap with these rules, without the network and without Karvon's secret variables, on Linux. Where
  bubblewrap cannot run, its shell commands do not run at all.
- **Codex and the Agent Client Protocol agents** run their commands themselves: the strict sandbox does not hold them
  (Codex has its own), and the panel says so.

What the sandbox keeps from the shell goes through Karvon's tools, which run outside it:

- building, tests, running and debugging the app: `run_build`, `run_tests`, `app_control`, `debug_control`;
- git: `git_status`, `git_diff`, `git_log` read; `git_commit`, `git_branch`, `git_pull` and `git_push` ask. They run your git,
  with your credentials and ssh-agent, never forced;
- packages: `package_add` (asks) and `package_restore`, by the project's own manager (NuGet, npm, pnpm, yarn, pip into the
  project's `.venv`, cargo, go), from your registries. npm's, pnpm's and yarn's install scripts do not run;
- code of a library or the SDK: `code_definition`, `code_read_symbol` (not a file path outside the project);
- databases, containers, servers, Kubernetes, Jira, CI, pull requests, HTTP, documents and the browser: their tools.

And what no tool does:

- **A script**: a tool that cannot do the job (a document the `document_` tools cannot change) is done with a script in
  the project, in the sandbox. Python's and Node's packages are read from your caches; one installed goes into the
  project (`.venv`, `node_modules`).
- **A file or a program outside the project**: add its folder (Folders Claude may read / change), or Claude asks with
  `run_outside_sandbox`: one command, shown in full with why, run with your own access only after you say yes. It asks
  every time; no rule lets it through.

What it does not cover yet: Karvon's own tools that run the project's code (the build, the tests, the app, a package
restore's build steps, git hooks in the project's files such as `.husky`) run it with your access, outside the sandbox.
Code Claude wrote into the project runs that way when they run it.

## Boundaries

| Boundary | State | Proved by |
| --- | --- | --- |
| Secret variables kept from `claude` | enforced | `ClaudeSecretsTests.Claude_Code_is_started_without_them`, `ClaudeNegativeTests` |
| The guard of the secrets (Claude Code's own tools) | enforced, as a guard | `ClaudeSecretsTests.Claude_Codes_tools_may_not_read_change_or_print_them`, `A_session_denies_Claude_Codes_own_tools_the_secrets`, `ClaudeNegativeTests` |
| The vault: encrypted, and not openable by Claude's tools | enforced | `ClaudeSecretsTests.The_vault_keeps_secrets_encrypted_in_its_database_and_takes_over_the_keyrings`, `Claude_Codes_tools_may_not_open_the_vault` |
| A copy of the vault opens for no one without your keyring's key or its passphrase | enforced | `SecretStorageTests.A_vault_copied_to_another_machine_opens_for_no_one_without_this_keyring_or_its_passphrase` |
| A data source's password never in a server list's file (one an older Karvon left there moved into the vault) | enforced | `SecretStorageTests.A_data_sources_saved_password_is_kept_in_the_vault_never_in_the_list_file`, `With_the_vault_locked_or_none_a_saved_password_lasts_until_Karvon_closes_and_is_never_written_down`, `A_password_an_older_Karvon_left_in_the_list_file_is_moved_into_the_vault` |
| The strict sandbox: the agents' shells see the project, toolchains and caches, not the rest of the home folder, the user's sockets or (the Agents panel's) the network | enforced (bubblewrap) | `ProjectSandboxTests.A_command_in_it_sees_the_project_and_toolchains_and_not_the_home_folder_the_network_or_the_users_sockets`, `Claude_Codes_sandbox_is_given_the_rules_and_refuses_its_shell_where_it_cannot_start`, `LiveStrictSandboxTests` (real Claude Code, with a key) |
| The strict sandbox: the project's `.git`, `.claude`, `.mcp.json`, `.vscode` and `.idea` read only | enforced | `ProjectSandboxTests.What_in_the_project_tells_tools_outside_the_sandbox_what_to_run_is_read_only_in_it` |
| git and packages through Karvon's tools, with the user's own access; a commit, a push, a package asked first | enforced | `GitPackageToolsTests` |
| The strict sandbox: the agents' file tools kept to the project; a command outside it asks every time | enforced | `ProjectSandboxTests.The_agents_file_tools_are_kept_to_the_project_with_the_way_to_what_they_need`, `The_Agents_panels_model_is_kept_to_the_project_by_its_tools_and_its_shell`, `A_command_outside_the_sandbox_asks_every_time_and_runs_only_while_the_sandbox_is_on` |
| Karvon's Chrome has no debugging port (Linux, macOS) | enforced | `BrowserTests.Karvons_Chrome_is_driven_through_its_pipe_with_no_port_another_program_could_reach_and_closes_with_Karvon` |
| The Agents panel's conversations readable by you alone | enforced | `SecretStorageTests.An_agents_conversation_is_readable_by_its_owner_alone` |
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
| What Claude Code's own tools read (a file's contents, a command's output) has its secrets marked before the model sees it, and a mark Claude writes back (an edit, a command) gets its value again | enforced: Karvon's PostToolUse and PreToolUse hooks (with Keep the secrets away), checked with the real Claude Code 2.1.312 | `ClaudeSecretsTests.Secrets_are_marked_by_place_and_key_and_given_back_by_their_marks`, `What_Claude_Codes_tools_read_is_marked_and_what_it_writes_or_runs_gets_the_value` |
| Everything Claude Code sends the model (a file attached with `@` too) goes through a gateway that marks the secrets in it | enforced, on at first (MCP Servers > karvon > Scan what Claude Code sends the model), a gateway on this machine that is Claude Code's HTTPS proxy, so Remote Control stays on (it opens only `api.anthropic.com`, with a certificate of that session's that only that CLI trusts; other hosts are tunnels it does not read; what runs in Claude's shell does not use it; with a base URL or proxy of your own it sits at `ANTHROPIC_BASE_URL` instead, and Remote Control is off); a subscription's sign-in goes through it too; a machine where it cannot start goes straight to the API and says so | `ClaudeSecretsTests.The_gateway_hides_the_secrets_in_what_Claude_Code_sends_the_model`, `A_session_with_the_gateway_is_given_its_base_url`, `The_gateway_is_on_at_first_whatever_0_3_10_kept` |
| The vault unlocked by a passphrase instead of the keyring, asked as Karvon starts | enforced when set | `VaultPassphraseTests` |
| The vault unlocked by Touch ID or Windows Hello | planned | |
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
