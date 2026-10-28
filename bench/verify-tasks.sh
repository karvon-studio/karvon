#!/usr/bin/env bash
# Checks the tasks in the image, with no model: on a fresh workspace (after setup.sh) check.sh fails, and after
# solution.sh it passes. Run in the image: docker run --rm --entrypoint verify-tasks.sh karvon-bench T01 T02 ...
# (T14 with --privileged -e BENCH_DOCKER=1 -v /var/lib/docker: its daemon's storage off the container's overlay).
set -uo pipefail
as_bench() { runuser -u bench -- env HOME=/home/bench PATH="$PATH" "$@"; }
if [ "${BENCH_DOCKER:-}" = 1 ]; then
  dockerd > /tmp/dockerd.log 2>&1 &
  for _ in $(seq 60); do docker info > /dev/null 2>&1 && break; sleep 1; done
  for image in /opt/bench-images/*.tar; do [ -f "$image" ] && docker load -i "$image" > /dev/null; done
fi
failed=0
for id in "$@"; do
  task=/bench/tasks/$id
  work=$(mktemp -d) && chown bench:bench "$work"
  answer=$(mktemp) && chown bench:bench "$answer" && as_bench sh -c "echo nothing > $answer"
  fixture=$(jq -r .fixture "$task/task.json")
  [ "$fixture" != empty ] && as_bench cp -a "/bench/fixtures/$fixture/." "$work/"
  cd "$work"
  [ -x "$task/setup.sh" ] && as_bench "$task/setup.sh" > /tmp/setup.log 2>&1
  as_bench git init -q && as_bench git add -A && as_bench git commit -q -m start --allow-empty
  start=$(date +%s)
  before=$(as_bench env BENCH_ANSWER="$answer" "$task/check.sh" 2>&1 | tail -1); before_exit=${PIPESTATUS[0]}
  as_bench env BENCH_ANSWER="$answer" "$task/solution.sh" > /tmp/solution.log 2>&1
  after=$(as_bench env BENCH_ANSWER="$answer" "$task/check.sh" 2>&1 | tail -1); after_exit=${PIPESTATUS[0]}
  took=$(( $(date +%s) - start ))
  if [ "$before_exit" != 0 ] && [ "$after_exit" = 0 ]; then verdict=ok; else verdict=WRONG; failed=1; fi
  echo "$id $verdict (${took}s): before [$before] after [$after]"
  cd / && rm -rf "$work" "$answer"
done
exit $failed
