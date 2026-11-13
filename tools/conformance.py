"""Both servers, one library: do they answer identically?

The Revit plugin runs a .NET server (server/RevitVr.Server) and the IngeTrazo plugin runs an
embedded Python one, because IngeTrazo users must not need .NET. That is two implementations of
the same routes, which is exactly the kind of thing that drifts — the headset then shows different
models or a 404 depending on which plugin was opened last.

So: start both against one fixture library and compare the bytes.

    python tools/conformance.py                       # uses dist/server/RevitVr.Server.exe
    python tools/conformance.py --server path/to/RevitVr.Server.exe
"""
from __future__ import annotations

import argparse
import http.client
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import types
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
PLUGIN = REPO / "plugins" / "ingetrazo" / "ingetrazo_vr.py"
WEB = REPO / "web"
DEFAULT_SERVER = REPO / "dist" / "server" / "RevitVr.Server.exe"

passed = failed = 0


def check(label, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  ok   {label}")
    else:
        failed += 1
        print(f"  FAIL {label}  {detail}")


def get(port, path):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=5) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as exc:
        return exc.code, b""


def raw_status(port, path):
    """The guard has to be tested with the path as sent: urllib normalises ../ away."""
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    conn.putrequest("GET", path, skip_accept_encoding=True)
    conn.endheaders()
    resp = conn.getresponse()
    resp.read()
    conn.close()
    return resp.status


def make_library(root: Path) -> Path:
    entry = root / "fixture-model"
    (entry / "scene").mkdir(parents=True)
    (entry / "meta.json").write_text(json.dumps({
        "slug": "fixture-model", "name": "Fixture Model", "source": "revit",
        "sourceLabel": "Revit 2025", "fileName": "Fixture Model.rvt", "filePath": None,
        "view": "Level 1", "exportedUtc": "2026-01-01T00:00:00Z", "triangles": 12,
        "generator": "conformance.py",
    }, indent=2), encoding="utf-8")
    (entry / "scene" / "scene.glb").write_bytes(b"glTF-conformance-fixture")
    (entry / "scene" / "colliders.json").write_bytes(
        b'{"name":"Fixture Model","segments":[],"surfaces":[],"terrain":[]}')
    return root


def start_revit(exe: Path, library: Path, port: int, scene: Path):
    proc = subprocess.Popen(
        [str(exe), "--app", str(WEB), "--scene", str(scene), "--library", str(library),
         "--port", str(port)],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.time() + 25
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/status", timeout=1) as r:
                if json.loads(r.read()).get("ok"):
                    return proc
        except Exception:
            time.sleep(0.25)
    proc.terminate()
    raise SystemExit("the .NET server did not start")


def start_python(library: Path, port: int):
    tools = types.ModuleType("tools")
    tools.__path__ = []
    base = types.ModuleType("tools.base")

    class Tool:
        pass

    base.Tool = Tool
    sys.modules["tools"] = tools
    sys.modules["tools.base"] = base
    spec = importlib.util.spec_from_file_location("ingetrazo_vr", PLUGIN)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    os.environ["VRHUB_LIBRARY"] = str(library)
    os.environ["INGETRAZO_VR_WWW"] = str(WEB)
    actual, _ = mod.start_server(port)
    return mod, actual


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", default=str(DEFAULT_SERVER))
    ap.add_argument("--scene", default=None, help="scene folder for the legacy /scene route")
    args = ap.parse_args()

    exe = Path(args.server)
    if not exe.is_file():
        print(f"server not found: {exe}\nbuild it first: dotnet publish server/RevitVr.Server -c Release",
              file=sys.stderr)
        return 2
    if not (WEB / "index.html").is_file():
        print(f"web app not found: {WEB}", file=sys.stderr)
        return 2

    scene = Path(args.scene) if args.scene else REPO / "runtime" / "export"
    scene.mkdir(parents=True, exist_ok=True)

    work = Path(tempfile.mkdtemp(prefix="vrhub-conformance-"))
    library = make_library(work / "library")
    revit_port, python_port = 8795, 8797

    proc = start_revit(exe, library, revit_port, scene)
    mod, python_port = start_python(library, python_port)
    try:
        print("routes")
        for path in ("/", "/viewer.html", "/app.js", "/hub.css"):
            a_status, a_body = get(revit_port, path)
            b_status, b_body = get(python_port, path)
            check(f"{path} identical", a_status == b_status == 200 and a_body == b_body,
                  f"HTTP {a_status}/{b_status}, {len(a_body)}/{len(b_body)} bytes")

        print("model list")
        _, a_body = get(revit_port, "/api/models")
        _, b_body = get(python_port, "/api/models")
        a_models = json.loads(a_body)["models"]
        b_models = json.loads(b_body)["models"]
        check("/api/models identical", a_models == b_models,
              f"{len(a_models)} vs {len(b_models)} models")
        check("one model listed", len(a_models) == 1 and a_models[0]["slug"] == "fixture-model")

        print("model files")
        for name in ("scene.glb", "colliders.json"):
            _, a_body = get(revit_port, f"/models/fixture-model/scene/{name}")
            _, b_body = get(python_port, f"/models/fixture-model/scene/{name}")
            check(f"{name} identical", a_body == b_body and len(a_body) > 0,
                  f"{len(a_body)} vs {len(b_body)} bytes")

        print("guards")
        for attempt in ("/models/../../secrets/scene/scene.glb",
                        "/models/%2e%2e/scene/scene.glb",
                        "/scene/../../secrets"):
            a = raw_status(revit_port, attempt)
            b = raw_status(python_port, attempt)
            check(f"404 both: {attempt}", a == b == 404, f"HTTP {a}/{b}")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        mod.stop_server()
        shutil.rmtree(work, ignore_errors=True)

    print(f"\n{passed} passed, {failed} failed")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
