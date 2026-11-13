"""Correct mismatched image MIME types in a GLB, in place, with a backup.

    python tools/fix_glb.py <file.glb>
"""
import importlib.util
import shutil
import sys
import types
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PLUGIN = REPO / "plugins" / "ingetrazo" / "ingetrazo_vr.py"
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

path = Path(sys.argv[1])
backup = path.with_suffix(path.suffix + ".bak")
if not backup.is_file():
    shutil.copy2(path, backup)
print(f"backup: {backup}")
print(f"fixed : {vr.fix_glb_image_mimes(path)} image(s)")
