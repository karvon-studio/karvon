#!/usr/bin/env bash
# The bug: a caret requirement below 1.0 no longer pins the minor version, so one test fails.
set -euo pipefail
sed -i 's/(0, minor) => self.major == 0 && self.minor == minor,/(0, _) => self.major == 0,/' src/lib.rs
grep -q '(0, _) => self.major == 0,' src/lib.rs
