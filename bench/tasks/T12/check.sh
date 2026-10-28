#!/usr/bin/env bash
# The way: KarvonModule.ClaudeTools() -> ClaudeModule.ToolServer() -> KarvonTools.Create -> ClaudeToolServer ->
# ClaudeViewModel.Tools -> ClaudeStartOptions -> ClaudeSession (an "sdk" MCP server; calls reach IClaudeTool.CallAsync).
answer=$(cat "$BENCH_ANSWER")
found=0; names=()
for type in IClaudeTool KarvonModule ClaudeModule KarvonTools ClaudeToolServer ClaudeViewModel ClaudeStartOptions ClaudeSession; do
  if grep -q -- "$type" <<<"$answer"; then found=$((found+1)); names+=("$type"); fi
done
if grep -q "ClaudeToolServer" <<<"$answer" && grep -q "ClaudeSession" <<<"$answer" && [ $found -ge 5 ]; then
  echo "names $found of the 8 types: ${names[*]}"
else
  echo "names $found of the 8 types (5 needed, with ClaudeToolServer and ClaudeSession): ${names[*]}"; exit 1
fi
