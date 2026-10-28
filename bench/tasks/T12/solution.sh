#!/usr/bin/env bash
cat > "$BENCH_ANSWER" <<'ANSWER'
A module gives its tools (IClaudeTool) from KarvonModule.ClaudeTools(). ClaudeModule.ToolServer() gathers them with the
IDE's and passes them to KarvonTools.Create, which makes a ClaudeToolServer. ClaudeModule gives it to its ClaudeViewModel
(Tools), which starts the CLI with ClaudeStartOptions(..., Tools), and ClaudeSession serves it as an "sdk" MCP server:
the CLI's mcp_message control requests are answered by the ClaudeToolServer, which calls the tool's CallAsync.
ANSWER
