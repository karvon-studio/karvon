#!/usr/bin/env bash
set -euo pipefail
sed -i 's/(0, _) => self.major == 0,/(0, minor) => self.major == 0 \&\& self.minor == minor,/' src/lib.rs
