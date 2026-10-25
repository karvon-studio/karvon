"""dotnet-trace of the tests': collect writes the trace and its speedscope file (a thread in Main, Total and Tax); each call logged."""
import json
import os
import sys

log = os.environ["FAKE_DIAG_LOG"]
with open(os.path.join(log, "calls.txt"), "a") as f:
    f.write("dotnet-trace " + " ".join(sys.argv[1:]) + "\n")

out = sys.argv[sys.argv.index("-o") + 1]
with open(out, "w") as f:
    f.write("nettrace")
names = ["Process64 app (1)", "Threads", "Thread (1)", "app!Program.Main()", "app!Shop.Total()", "app!Shop.Tax()", "CPU_TIME", "UNMANAGED_CODE_TIME"]
events = [("O", 0, 0), ("O", 1, 0), ("O", 2, 0), ("O", 3, 0), ("O", 4, 0), ("O", 6, 0), ("C", 6, 60),
          ("O", 5, 60), ("O", 6, 60), ("C", 6, 100), ("C", 5, 100), ("C", 4, 100),
          ("O", 7, 100), ("C", 7, 200), ("C", 3, 200), ("C", 2, 200), ("C", 1, 200), ("C", 0, 200)]
speedscope = {
    "$schema": "https://www.speedscope.app/file-format-schema.json",
    "shared": {"frames": [{"name": n} for n in names]},
    "profiles": [{"type": "evented", "name": "Thread (1)", "unit": "milliseconds", "startValue": 0, "endValue": 200,
                  "events": [{"type": t, "frame": fr, "at": at} for t, fr, at in events]}],
}
with open(os.path.splitext(out)[0] + ".speedscope.json", "w") as f:
    json.dump(speedscope, f)
print("Trace completed.")
