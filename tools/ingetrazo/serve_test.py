"""Serve a scene directory through the plugin's server, for a browser check.

    python tools/serve_test.py <scene-dir> [port]

Points LOCALAPPDATA at a scratch tree so the plugin's export_dir() finds the
scene, then serves the viewer + scene on the given port and keeps running.
"""
import importlib.util
import os
import shutil
import sys
import tempfile
import types
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PLUGIN = REPO / "plugins" / "ingetrazo" / "ingetrazo_vr.py"
scene = Path(sys.argv[1]).resolve()
port = int(sys.argv[2]) if len(sys.argv) > 2 else 8799

scratch = Path(tempfile.mkdtemp(prefix="ingetrazo-vr-serve-"))
export = scratch / "IngeTrazo" / "VR" / "export"
export.mkdir(parents=True)
for f in scene.iterdir():
    if f.is_file():
        shutil.copy2(f, export / f.name)

os.environ["LOCALAPPDATA"] = str(scratch)

tools = types.ModuleType("tools")
base = types.ModuleType("tools.base")


class Tool:
    pass


base.Tool = Tool
sys.modules["tools"] = tools
sys.modules["tools.base"] = base

spec = importlib.util.spec_from_file_location("ingetrazo_vr", PLUGIN)
vr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vr)

print("www   :", vr.www_dir())
print("export:", vr.export_dir())
slug = vr.publish_library(export, name=scene.name)
p, reused = vr.start_server(port)
print(f"library http://localhost:{p}/  ({len(vr.list_models())} model(s), reused={reused})")
print(f"viewer  http://localhost:{p}/viewer.html?model={slug}", flush=True)
import threading
threading.Event().wait()
