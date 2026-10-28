#!/usr/bin/env bash
# One run in its container: the workspace from the task's fixture, the task's setup and its first commit, the session
# (Karvon.Bench, as the bench user), then the task's check. TASK, ARM, MODEL (and EFFORT) say which; /work is the
# workspace and /out the run's results, both the host's.
set -uo pipefail
task=/bench/tasks/$TASK
now() { date +%s%3N; }
times() { printf '{"setupMs": %s, "checkMs": %s}\n' "$setup_ms" "${check_ms:-null}" > /out/times.json; }
as_bench() { runuser -u bench -- env HOME=/home/bench PATH="$PATH" "$@"; }

# T14's own Docker daemon (the container is privileged for it), with the images it needs loaded.
if [ "${BENCH_DOCKER:-}" = 1 ]; then
  dockerd > /out/dockerd.log 2>&1 &
  for _ in $(seq 60); do docker info > /dev/null 2>&1 && break; sleep 1; done
  for image in /opt/bench-images/*.tar; do [ -f "$image" ] && docker load -i "$image" > /dev/null; done
fi

start=$(now)
fixture=$(jq -r .fixture "$task/task.json")
if [ "$fixture" != empty ]; then as_bench cp -a "/bench/fixtures/$fixture/." /work/; fi
cd /work
if [ -x "$task/setup.sh" ]; then as_bench "$task/setup.sh" > /out/setup.log 2>&1 || { echo "setup failed" >> /out/setup.log; }; fi
as_bench git init -q && as_bench git add -A && as_bench git commit -q -m "The task's starting point" --allow-empty
setup_ms=$(( $(now) - start ))

as_bench claude --version > /out/cli.txt 2>&1
as_bench env CLAUDE_CONFIG_DIR=/tmp/claude-config KARVON_CONFIG_DIR=/tmp/karvon-config CLAUDE_CODE_OAUTH_TOKEN="${CLAUDE_CODE_OAUTH_TOKEN:-}" \
  /opt/karvon-bench/Karvon.Bench --task "$task/task.json" --workspace /work --arm "$ARM" --model "$MODEL" --out /out ${EFFORT:+--effort "$EFFORT"} \
  > /out/runner.log 2>&1
echo $? > /out/runner.exit

start=$(now)
touch /out/answer.txt
as_bench env BENCH_ANSWER=/out/answer.txt bash -c "cd /work && '$task/check.sh'" > /out/check.log 2>&1
echo $? > /out/check.exit
check_ms=$(( $(now) - start ))
times
