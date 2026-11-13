## What changed

<!-- One or two sentences. The diff shows the rest. -->

## Why

<!-- The problem this solves. Link the issue if there is one. -->

## What was run

- [ ] `dotnet run --project tools/test-wallcut -c Release` and the other Revit-free .NET tests
- [ ] `node tools/test-collision.mjs runtime/export/colliders.json`
- [ ] `python tools/ingetrazo/test_derive.py`
- [ ] `python tools/ingetrazo/test_library.py`
- [ ] `python tools/conformance.py` (both servers, one library)
- [ ] `.\plugins\revit\build.ps1` and `dotnet run --project tools/addincheck` (needs Revit)
- [ ] Walked it in a headset — say which model (demo scene preferred)

## Not tested

<!-- Say what you could not run and why. "No Revit here, so the add-in change is untested" is a
     useful, acceptable answer. Silence is not. -->

## Notes for the reviewer

<!-- Contracts touched? %LOCALAPPDATA%\VRHub, ports, install paths, contracts/*.json.
     Anything that changes an existing installation needs to keep the old behaviour working. -->
