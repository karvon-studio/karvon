#!/usr/bin/env bash
# The bug: a rename left half done. The workbench's status bar call is renamed where it is declared and implemented
# (IWorkbench.Status to ShowStatus), not where the modules call it: some fifty errors in about twenty files of a dozen
# projects. Either way of finishing it (the old name back, or every caller renamed) builds.
set -euo pipefail
python3 - <<'PY'
for path, old, new in [
    ("src/Karvon.Core/Modules/IWorkbench.cs", "    void Status(string text);", "    void ShowStatus(string text);"),
    ("src/Karvon.Core/ViewModels/MainViewModel.Modules.cs", "    void IWorkbench.Status(string text) => SetStatus(text);", "    void IWorkbench.ShowStatus(string text) => SetStatus(text);"),
]:
    text = open(path).read()
    assert text.count(old) == 1, path
    open(path, "w").write(text.replace(old, new))
PY
