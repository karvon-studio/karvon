#!/usr/bin/env bash
# Builds the benchmark's image (karvon-bench): stages the runner (published), Claude Code's CLI, the tools Karvon
# downloaded on this machine, this repository at HEAD (the karvon fixture) and T14's images, then docker build.
set -euo pipefail
cd "$(dirname "$0")"
repo=$(git rev-parse --show-toplevel)
staging=staging
rm -rf "$staging" && mkdir -p "$staging"/{karvon-tools,images,karvon}
dotnet publish Karvon.Bench -c Release -r linux-x64 --self-contained -o "$staging/runner" -nologo -v q
cp "$(readlink -f "$(command -v claude)")" "$staging/claude"
for tool in roslyn netcoredbg typescript js-debug eslint pyright debugpy gopls dlv rust-analyzer codelldb web-languages; do
  [ -d "$HOME/.local/share/karvon/$tool" ] && cp -a "$HOME/.local/share/karvon/$tool" "$staging/karvon-tools/"
done
# This repository as it is committed, with bengine (a submodule), without build output.
git -C "$repo" archive HEAD | tar -x -C "$staging/karvon"
git -C "$repo/external/bengine" archive HEAD | tar -x -C "$staging/karvon/external/bengine"
# The images T14's compose stack is built from, for its own Docker daemon.
if [ -f fixtures/compose/images.txt ]; then
  while read -r image; do
    [ -n "$image" ] || continue
    docker pull -q "$image" > /dev/null
    docker save "$image" -o "$staging/images/$(echo "$image" | tr '/:' '__').tar"
  done < fixtures/compose/images.txt
fi
echo "$(git -C "$repo" rev-parse --short HEAD)" > "$staging/karvon/.bench-commit"
# The build reaches the mirrors through the host's resolver: Docker's own fallback DNS is not reachable on every network.
docker build --network host -t karvon-bench -f Dockerfile .
