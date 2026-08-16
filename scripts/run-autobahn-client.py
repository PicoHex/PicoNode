"""
Runs the Autobahn fuzzing client against the server specified in the
fuzzingclient spec file.

Why a separate launcher: the patched autobahntestsuite package needs its own
directory first on sys.path (the `_version` absolute-import shim) before
`wstest` can be imported, and the run must happen in UTF-8 mode.

Usage:
    python scripts/run-autobahn-client.py <fuzzingclient.json>
"""

import os
import sys


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    spec = os.path.abspath(sys.argv[1])
    import sysconfig

    pkg = os.path.join(sysconfig.get_paths()["purelib"], "autobahntestsuite")
    sys.path.insert(0, pkg)

    from wstest import run

    sys.argv = ["wstest", "-m", "fuzzingclient", "-s", spec]
    run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
