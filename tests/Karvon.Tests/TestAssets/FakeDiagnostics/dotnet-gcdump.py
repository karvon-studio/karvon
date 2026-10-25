"""dotnet-gcdump of the tests': collect writes a dump, report prints a heap's types as the real one does; each call logged."""
import os
import sys

log = os.environ["FAKE_DIAG_LOG"]
with open(os.path.join(log, "calls.txt"), "a") as f:
    f.write("dotnet-gcdump " + " ".join(sys.argv[1:]) + "\n")

if sys.argv[1] == "collect":
    out = sys.argv[sys.argv.index("-o") + 1]
    with open(out, "w") as f:
        f.write("gcdump")
    print(f"Writing gcdump to '{out}'...")
    print("\tFinished writing 388039 bytes.")
elif sys.argv[1] == "report":
    print("""      1,163,002  GC Heap bytes
         20,925  GC Heap objects

   Object Bytes     Count  Type
        262,168         1  System.String[] (Bytes > 100K)  [System.Private.CoreLib.dll]
         29,526         1  System.String (Bytes > 10K)  [System.Private.CoreLib.dll]
            448         3  System.Globalization.CultureData  [System.Private.CoreLib.dll]
            288         1  Entry<System.String,System.Object>[]  [System.Private.CoreLib.dll]
         41,290    20,645  System.String  [System.Private.CoreLib.dll]
""")
