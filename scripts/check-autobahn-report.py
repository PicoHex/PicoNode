"""
Verdict checker for an Autobahn fuzzing-client report (index.json).

Exit codes:
    0 — no FAILED cases (NON-STRICT / INFORMATIONAL / UNIMPLEMENTED are OK)
    1 — one or more FAILED cases, listed on stdout

Usage:
    python scripts/check-autobahn-report.py <path-to-index.json>
"""

import json
import sys


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    with open(sys.argv[1], encoding="utf-8") as f:
        report = json.load(f)

    failed = []
    total = 0
    for agent, cases in report.items():
        for case_id, case in cases.items():
            total += 1
            behavior = case.get("behavior")
            if behavior == "FAILED":
                failed.append((agent, case_id, case.get("behaviorClose"), case.get("resultClose")))

    if failed:
        print(f"Autobahn: {len(failed)}/{total} cases FAILED:")
        for agent, case_id, close, reason in failed:
            print(f"  {case_id} (close={close}) :: {reason}")
        return 1

    print(f"Autobahn: all {total} cases passed (no FAILED).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
