#!/usr/bin/env bash
set -euo pipefail
python3 - <<'PY'
for path, new, old in [
    ("src/Karvon.Core/Modules/IWorkbench.cs", "    void Status(string text);", "    void ShowStatus(string text);"),
    ("src/Karvon.Core/ViewModels/MainViewModel.Modules.cs", "    void IWorkbench.Status(string text) => SetStatus(text);", "    void IWorkbench.ShowStatus(string text) => SetStatus(text);"),
]:
    text = open(path).read()
    open(path, "w").write(text.replace(old, new))
PY
