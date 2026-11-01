### Review triage 2026-11-02

| Finding | Verdict | Evidence and route |
|---|---|---|
| Blind 1: HTTP scenario under Unit | medium | ProductionRegistration starts OperatorServer for all four cases. Patch extraction to Integration, preserve cases. |
| Blind 2: operator runner required environment | medium | Unchanged original ENV removes required Compose interpolation values; config-only reproduction fails. Pre-existing harness issue; defer, no scenario changes. |
| Blind 3: lifecycle direct docker run configuration | medium | Original startup and shutdown probe omit mandatory host settings, unchanged in snapshot. Pre-existing; defer and document limitation, runtime unavailable. |
| Blind 4: lifecycle documented environment | medium | Existing README lacks required Compose env preparation. Direct documentation correction, patch. |
| Blind 5: offline Python prerequisites | medium | Tool tests import jsonschema, guide omitted pinned requirements. Direct documentation correction, patch. |
| Blind 6: recursive tools discovery | low | Only explicit leaf discovery commands are documented and both execute all 22 tool tests. An undocumented parent-directory command discovers zero because subdirectories are not packages. Reject: new aggregator complexity unnecessary for documented contract. |
| Blind 7: privacy fixed venv | medium | Original privacy.py invokes fixed venv path, unchanged. Document exact setup; no runner change. |
| Blind 8: operator follow-up navigation | medium | Main run omits separately runnable operator scripts; guide lacks their lifecycle prerequisites. Patch operator README. |
| Blind 9: non-Python asset proof | medium | Existing mapping hashes and existence checks insufficient as automated assertion. Patch allowlisted content comparison. |
| Blind 10: trait verification | medium | Verifier drops selection traits before comparison without validating values. Patch path/trait and category identity partition checks. |
| Edge 1: real HTTP under Unit | medium | Same concrete scenario as Blind 1; retain separate triage row, group into extraction patch. |
| Verification gap reviewer | false | Reviewer reported no verification gaps; no finding to route. |

All accepted patches completed; final full verification repeated.
