#!/usr/bin/env python3
"""The document engines' zip for one platform: the library built by `cargo build --release`, with each upstream
repository's licenses and notices beside it (Apache-2.0 asks for them).

    package.py <library> <version> <rid> <out-dir>   ->   <out-dir>/karvon-documents-<version>-<rid>.zip
"""
import json
import pathlib
import subprocess
import sys
import zipfile

here = pathlib.Path(__file__).resolve().parent
library, version, rid, out = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3], pathlib.Path(sys.argv[4])
metadata = json.loads(subprocess.check_output(["cargo", "metadata", "--format-version", "1", "--locked"], cwd=here))
licensed = ("NOTICE", "LICENSE-MIT", "LICENSE-APACHE", "ATTRIBUTION.md")

# Each upstream repository once: its root is the nearest folder above a package's manifest that has a NOTICE.
roots = {}
for package in metadata["packages"]:
    source = package.get("source") or ""
    if not source.startswith("git+https://github.com/storytold/"):
        continue
    folder = pathlib.Path(package["manifest_path"]).parent
    while folder != folder.parent and not (folder / "NOTICE").exists():
        folder = folder.parent
    if (folder / "NOTICE").exists():
        name = source.split("/storytold/")[1].split("?")[0]
        rev = source.split("#")[-1]
        roots[f"{name}-{rev}"] = folder

out.mkdir(parents=True, exist_ok=True)
target = out / f"karvon-documents-{version}-{rid}.zip"
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    info = zipfile.ZipInfo(library.name)
    info.external_attr = 0o755 << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    z.writestr(info, library.read_bytes())
    for key, root in sorted(roots.items()):
        for name in licensed:
            if (root / name).exists():
                z.write(root / name, f"licenses/{key}/{name}")
        # The fonts and pictures they embed: their licenses (OFL and the like).
        for extra in sorted((root / "assets").rglob("*")) if (root / "assets").exists() else []:
            if extra.is_file() and (extra.name.startswith(("LICENSE", "OFL")) or extra.name.endswith("LICENSE.txt")):
                z.write(extra, f"licenses/{key}/{extra.relative_to(root)}")
    z.writestr("README.txt", f"Karvon Studio's document engines {version} ({rid}): PdfCraft, WordCraft, GridCraft and DeckCraft "
               f"(github.com/storytold), headless, in one library built from native/documents of Karvon's sources. "
               f"The engines' licenses and notices are in licenses/: {', '.join(sorted(roots))}.\n")
print(target)
