"""Starts the HTTP and HTTPS servers for a browser/curl check.

    python tools/serve_https.py <scene-dir> [port]

HTTP on <port>, HTTPS on <port>+1 with a freshly generated self-signed
certificate, both serving <scene-dir> as /scene/.
"""
import importlib.util
import os
import shutil
import sys
import tempfile
import threading
import types
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PLUGIN = REPO / "plugins" / "ingetrazo" / "ingetrazo_vr.py"
scene = Path(sys.argv[1]).resolve()
ports = sys.argv[2] if len(sys.argv) > 2 else "8770"
http_port, _, https_port = ports.partition(":")
http_port = int(http_port) if http_port not in ("", "-") else None
https_port = int(https_port) if https_port else None

scratch = Path(tempfile.mkdtemp(prefix="ingetrazo-vr-https-"))
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

print("openssl:", vr._openssl())
print("lan    :", vr.lan_address())
print("cert   :", vr.ensure_cert())
p = vr.start_server(http_port)[0] if http_port else None
q = vr.start_https(https_port or (p + 1 if p else vr.DEFAULT_PORT + 1))
print(f"http  : http://localhost:{p}/" if p else "http  : (not started)")
print(f"https : https://localhost:{q}/  (headset: {vr.headset_url(p or 0, q)})", flush=True)
threading.Event().wait()
