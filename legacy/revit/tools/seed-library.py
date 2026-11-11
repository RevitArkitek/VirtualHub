#!/usr/bin/env python3
"""Import an existing export folder into the shared model library.

Both plugins publish here on every export, but exports made before the library existed are still
sitting in their own folders. This copies one of them in, with the metadata the homepage needs.

    python tools/seed-library.py --source revit --name "196 SW-2025" --file "196 SW-2025.rvt"
    python tools/seed-library.py --source ingetrazo --name "Farnsworth House" \
        --dir "%LOCALAPPDATA%/IngeTrazo/VR/export"

The entry is keyed by the file name, exactly as a real export is, so re-exporting the same model
replaces it rather than adding a second card.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import unicodedata
from datetime import datetime, timezone
from pathlib import Path

SCENE_FILES = ("scene.glb", "colliders.json", "railings.json", "textures.json")


def library_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("XDG_CACHE_HOME")
    root = Path(base) / "VRHub" if base else Path.home() / ".cache" / "vrhub"
    return root / "library"


def default_export(source: str) -> Path:
    base = os.environ.get("LOCALAPPDATA") or os.environ.get("XDG_CACHE_HOME")
    root = Path(base) if base else Path.home() / ".cache"
    return (root / "RevitVR" / "export") if source == "revit" else (root / "IngeTrazo" / "VR" / "export")


def slugify(name: str) -> str:
    """Keep in step with ModelLibrary.Slugify (C#) and slugify (ingetrazo_vr.py)."""
    text = unicodedata.normalize("NFKD", str(name or "").strip())
    out = []
    for ch in text:
        if unicodedata.combining(ch):
            continue
        if ch.isascii() and ch.isalnum():
            out.append(ch.lower())
        elif out and out[-1] != "-":
            out.append("-")
    slug = "".join(out).strip("-")
    return (slug[:60].rstrip("-") or "model")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", choices=("revit", "ingetrazo"), required=True)
    ap.add_argument("--name", required=True, help="display name (the document name)")
    ap.add_argument("--file", default=None, help="file name shown on the card (defaults to --name)")
    ap.add_argument("--view", default=None, help="Revit 3D view name, if any")
    ap.add_argument("--dir", default=None, help="export folder (defaults to the plugin's own)")
    args = ap.parse_args()

    src = Path(args.dir) if args.dir else default_export(args.source)
    src = src.expanduser()
    if not (src / "scene.glb").is_file() or not (src / "colliders.json").is_file():
        print(f"no scene.glb + colliders.json in {src}", file=sys.stderr)
        return 2

    manifest = json.loads((src / "colliders.json").read_text(encoding="utf-8"))
    slug = slugify(args.name)
    entry = library_dir() / slug
    scene = entry / "scene"
    scene.mkdir(parents=True, exist_ok=True)

    for name in SCENE_FILES:
        s, d = src / name, scene / name
        if s.is_file():
            tmp = d.with_suffix(d.suffix + ".tmp")
            shutil.copyfile(s, tmp)
            os.replace(tmp, d)
        elif d.is_file():
            d.unlink()

    meta = {
        "slug": slug,
        "name": args.name,
        "source": args.source,
        "sourceLabel": (f"Revit {manifest.get('revitVersion')}" if args.source == "revit"
                        and manifest.get("revitVersion") else
                        ("Revit" if args.source == "revit" else "IngeTrazo")),
        "fileName": args.file or args.name,
        "filePath": None,
        "view": args.view,
        "exportedUtc": manifest.get("generatedUtc") or datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "triangles": manifest.get("triangleCount") or 0,
        "generator": "seed-library.py",
    }
    tmp = entry / "meta.json.tmp"
    tmp.write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, entry / "meta.json")

    print(f"{slug}: {meta['name']}  ({meta['triangles']:,} tris)  ->  {entry}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
