#!/usr/bin/env bash
# Install a locally built Karvon Studio .deb, or pass its path explicitly.
set -euo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)

if [[ $# -gt 1 ]]; then
  echo "Usage: $0 [path/to/karvon-studio_VERSION_amd64.deb]" >&2
  exit 2
fi

if [[ $# -eq 1 ]]; then
  DEB=$(realpath "$1")
else
  shopt -s nullglob
  PACKAGES=("$ROOT"/dist/karvon-studio_*_amd64.deb)
  shopt -u nullglob
  if [[ ${#PACKAGES[@]} -eq 0 ]]; then
    echo "No local package found in $ROOT/dist. Build it first; see deploy/linux/package.sh." >&2
    exit 1
  fi
  DEB=$(printf '%s\n' "${PACKAGES[@]}" | sort -V | tail -n 1)
fi

if [[ ! -f "$DEB" ]]; then
  echo "Package not found: $DEB" >&2
  exit 1
fi

sudo apt install "$DEB"
