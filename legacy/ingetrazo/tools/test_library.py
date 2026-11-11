"""The shared model library, without IngeTrazo.

Covers the rules that make the homepage trustworthy: the slug is the entry's identity (so an accent
or a re-export cannot create a duplicate), publishing replaces rather than appends, and the server
serves a model only from inside the library.

    python tools/test_library.py
"""
import importlib.util
import json
import os
import sys
import tempfile
import types
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# The plugin imports its host once, at module level.
tools = types.ModuleType("tools")
tools.__path__ = []
base = types.ModuleType("tools.base")


class Tool:
    pass


base.Tool = Tool
sys.modules["tools"] = tools
sys.modules["tools.base"] = base

spec = importlib.util.spec_from_file_location("ingetrazo_vr", ROOT / "ingetrazo_vr.py")
vr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vr)

passed = failed = 0


def check(label, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  ok   {label}")
    else:
        failed += 1
        print(f"  FAIL {label}  {detail}")


scratch = Path(tempfile.mkdtemp(prefix="vrhub-test-"))
os.environ["VRHUB_LIBRARY"] = str(scratch / "library")
export = scratch / "export"
export.mkdir()
(export / "scene.glb").write_bytes(b"glTF-test")
(export / "colliders.json").write_text('{"name":"Test"}', encoding="utf-8")

print("slug")
check("'196 SW-2025' -> 196-sw-2025", vr.slugify("196 SW-2025") == "196-sw-2025", vr.slugify("196 SW-2025"))
check("accents fold", vr.slugify("Casa Árboles") == "casa-arboles", vr.slugify("Casa Árboles"))
check("empty falls back", vr.slugify("  ") == "model", vr.slugify("  "))

print("publish")
slug = vr.publish_library(export, name="Casa Árboles", file_path=r"C:\models\Casa Árboles.igz",
                          triangles=42)
check("slug comes from the document name", slug == "casa-arboles", slug)
entry = Path(os.environ["VRHUB_LIBRARY"]) / slug
check("scene copied", (entry / "scene" / "scene.glb").read_bytes() == b"glTF-test")
meta = json.loads((entry / "meta.json").read_text(encoding="utf-8"))
check("source recorded", meta["source"] == "ingetrazo", meta["source"])
check("triangles recorded", meta["triangles"] == 42, meta["triangles"])
check("file name recorded", meta["fileName"] == "Casa Árboles.igz", meta["fileName"])

vr.publish_library(export, name="Casa Árboles", triangles=84)
entries = [p for p in Path(os.environ["VRHUB_LIBRARY"]).iterdir() if p.is_dir()]
check("re-publish replaces the entry", len(entries) == 1, f"{len(entries)} entries")
check("re-publish refreshes metadata",
      json.loads((entry / "meta.json").read_text(encoding="utf-8"))["triangles"] == 84)

models = vr.list_models()
check("list shows the model once", len(models) == 1, str(len(models)))
check("list carries the display name", models[0]["name"] == "Casa Árboles")
check("list carries the size", models[0]["glbBytes"] == len(b"glTF-test"))

print("server")
port, _ = vr.start_server(8788)


def get(path):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=5) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as exc:
        return exc.code, b""


status, body = get("/")
check("homepage served", status == 200 and b"Model Library" in body, f"HTTP {status}")
status, body = get("/api/models")
payload = json.loads(body) if status == 200 else {}
check("/api/models lists it", status == 200 and payload["models"][0]["slug"] == slug, f"HTTP {status}")
status, body = get(f"/models/{slug}/scene/scene.glb")
check("model file served", status == 200 and body == b"glTF-test", f"HTTP {status}")


def raw_get(path):
    """urllib normalises ../ away; the guard has to be tested with the path as sent."""
    import http.client
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    conn.putrequest("GET", path, skip_accept_encoding=True)
    conn.endheaders()
    resp = conn.getresponse()
    resp.read()
    conn.close()
    return resp.status


for attempt in ("/models/../../secrets/scene/scene.glb",
                "/models/%2e%2e/scene/scene.glb",
                "/scene/../../secrets"):
    code = raw_get(attempt)
    check(f"traversal refused: {attempt}", code == 404, f"HTTP {code}")

print("brand prefix")
check("window title loses the app name", vr._strip_brand("IngeTrazo — Burj Kalifa") == "Burj Kalifa",
      vr._strip_brand("IngeTrazo — Burj Kalifa"))
check("a model name with its own dash survives",
      vr._strip_brand("Tower A - Phase 2") == "Tower A - Phase 2")
check("prefix stripped for the slug",
      vr.publish_library(export, name="IngeTrazo — Burj Kalifa") == "burj-kalifa")

vr.stop_server()
print(f"\n{passed} passed, {failed} failed")
raise SystemExit(0 if failed == 0 else 1)
