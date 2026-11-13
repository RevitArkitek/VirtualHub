"""Startup harness: probes the installed API and runs the VR export headlessly.

Installed as a plugin only for the automated check. Writes everything it
learns to %LOCALAPPDATA%\\IngeTrazo\\VR\\test-report.txt.
"""
import importlib.util
import traceback
from pathlib import Path

PLUGIN = Path(__file__).resolve().parents[2] / "plugins" / "ingetrazo" / "ingetrazo_vr.py"
LOG = Path(r"C:\Users\micha\AppData\Local\IngeTrazo\VR\test-report.txt")


def _count(scene):
    try:
        return len(scene.loose_mesh.faces) + len(scene.groups)
    except Exception:
        return 0


def setup(app):
    from PySide6.QtCore import QTimer

    state = {"tries": 0}

    def tick():
        state["tries"] += 1
        scene = getattr(getattr(app, "viewport", None), "scene", None)
        n = _count(scene) if scene is not None else 0
        if n == 0 and state["tries"] < 30:
            QTimer.singleShot(1500, tick)
            return

        L = [f"faces+groups: {n}"]
        try:
            import sys as _s
            L.append("plugin modules loaded by the host: " + repr(
                [k for k in _s.modules if "ingetrazo_vr" in k]))
            from tools.base import Tool as _T
            subs = [c.__name__ for c in _T.__subclasses__()
                    if getattr(c, "name", "") == "Export to VR"]
            L.append("ExportVrTool registered: " + repr(subs))
        except Exception:
            L.append("registration probe failed: " + traceback.format_exc())
        # ---- what the installed build actually exposes -------------------
        try:
            import formats.meshexport as me
            import formats.gltf as g
            L.append("meshexport: " + ", ".join(sorted(
                x for x in dir(me) if not x.startswith("_"))))
            L.append("gltf: " + ", ".join(sorted(
                x for x in dir(g) if not x.startswith("_"))))
            import core.bim as bim
            L.append("bim: " + ", ".join(sorted(
                x for x in dir(bim) if not x.startswith("_")))[:400])
        except Exception:
            L.append("probe failed: " + traceback.format_exc())
        try:
            import itertools
            faces_all = [itertools.chain(scene.loose_mesh.faces,
                                         *(g.mesh.faces for g in scene.groups))]
            f = next(iter(faces_all[0]))
            L.append("Face public: " + ", ".join(sorted(
                x for x in dir(f) if not x.startswith("_"))))
            L.append("Face attrs: " + repr(dict(list(f.attrs.items())[:5])))
            L.append("hole_loops: " + str(len(getattr(f, "hole_loops", []) or [])))
        except Exception:
            L.append("face probe failed: " + traceback.format_exc())
        try:
            g0 = scene.groups[0] if scene.groups else None
            if g0 is not None:
                L.append("Group public: " + ", ".join(sorted(
                    x for x in dir(g0) if not x.startswith("_")))[:400])
        except Exception:
            pass

        # ---- the export -------------------------------------------------
        try:
            spec = importlib.util.spec_from_file_location("ingetrazo_vr_probe", PLUGIN)
            m = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(m)
            r = m.export(scene, name="arco-yanque", write_glb=False)
            L += ["", "--- export ---"] + m._report_lines(r)
            L.append("spawn: " + repr(r["spawn"]))
            L.append("segments sample: " + repr(r["segments"][:3]))
            L.append("surfaces sample: " + repr(r["surfaces"][:2]))
            port, _ = m.start_server(8765)
            L.append(f"server: http://localhost:{port}/")
            # the dialog the user will actually press buttons in
            dlg = m._make_dialog(app.viewport)
            dlg.do_export(None)
            L += ["", "--- dialog ---", dlg.log.toPlainText()]
            dlg.close()
        except Exception:
            L.append("export failed: " + traceback.format_exc())

        LOG.parent.mkdir(parents=True, exist_ok=True)
        LOG.write_text("\n".join(L), encoding="utf-8")

    QTimer.singleShot(2500, tick)
