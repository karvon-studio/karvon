#!/usr/bin/env bash
# Restores every fixture's packages in the image (NuGet, npm), so a run's workspace builds offline.
set -euo pipefail
shopt -s nullglob
for dir in /bench/fixtures/*/; do
  cd "$dir"
  for solution in *.sln *.slnx; do dotnet restore "$solution" -v q; done
  if [ -f package-lock.json ]; then npm ci --no-audit --no-fund; fi
  if [ -f make-db.py ]; then python3 make-db.py; fi
  if [ -f Cargo.toml ]; then cargo fetch; fi
  if [ -f go.mod ]; then go mod download; fi
done
