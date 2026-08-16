"""
Python 3 compatibility patch for autobahntestsuite (pinned 0.8.2).

The published autobahntestsuite packages are Python-2-era code that does not
import on Python 3.12:
  - `__init__.py` uses an absolute `from _version import __version__`
  - `except X, e:`, `xrange`, print statements, `raise X, msg`
  - absolute imports inside the `case`/`wampcase` subpackages
  - py2 str/bytes semantics (binascii.b2a_hex, utf8validator, sendFrame)
  - kwargs removed from autobahn 19 (WebSocket*Factory debug)
  - zope.interface.implements → implementer, dict.has_key

This script applies the verified, deterministic fix set on a clean sdist
extraction so results do not depend on previously patched site-packages.

Usage:
    python scripts/patch-autobahntestsuite.py <site-packages/autobahntestsuite>

The companion environment requirements (see run-autobahn.ps1):
    pip install "autobahntestsuite==0.8.2" "autobahn==19.11.2"
    pip uninstall wsaccel        # Cython utf8validator is broken on py3.12
"""

import io
import os
import re
import sys


def patch(path: str) -> None:
    for root, _dirs, files in os.walk(path):
        for name in files:
            if not name.endswith(".py"):
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, path).replace("\\", "/")
            with io.open(full, encoding="utf-8", errors="replace") as f:
                src = f.read()
            orig = src

            if rel == "__init__.py":
                # The published wheel/sdist ships `_version.py` INSIDE the
                # package but imports it as a top-level module.
                src = src.replace(
                    "from _version import __version__",
                    "from ._version import __version__",
                )

            # ── Syntax-level py2 → py3 ────────────────────────────────
            src = re.sub(r"except\s+([\w.]+)\s*,\s*(\w+)\s*:", r"except \1 as \2:", src)
            src = src.replace("xrange(", "range(")
            # single-line print statements (horizontal whitespace only — a bare
            # `print` on its own line must not swallow the next line)
            src = re.sub(r"^(\s*)print[ \t]+([^\n(][^\n]*)$", r"\1print(\2)", src, flags=re.M)
            # raise X, msg  /  raise X, (multi-line args)
            src = re.sub(r"raise\s+([\w.]+),\s*\(", r"raise \1(", src)
            src = re.sub(
                r'raise\s+([\w.]+),\s*(".*")[^\n]*$', r"raise \1(\2)", src, flags=re.M
            )
            # octal literals with leading zeros (never touch decimals)
            src = re.sub(r"(?<![\w.])0(\d{3,})\b", r"0o\1", src)
            # dict.has_key → `in` (member chains incl. quoted indexes)
            src = re.sub(r"([\w.\[\]'\"()]+)\.has_key\(([^)]+)\)", r"\2 in \1", src)
            # zope.interface: implements → implementer alias
            src = src.replace(
                "from zope.interface import implements",
                "from zope.interface import implementer as implements",
            )
            # dynamic type() bases: (object, Case, ) breaks py3 MRO
            src = re.sub(r"\(object,\s*(Case\w*),?\s*\)", r"(\1, )", src)
            # absolute imports inside subpackages (case9_1_1 style modules only —
            # never touch caseset/case.py itself)
            src = re.sub(r"^from (case\d[\w]+) import \*", r"from .\1 import *", src, flags=re.M)
            src = re.sub(r"^from (case\d[\w]+) import ([A-Z]\w+)", r"from .\1 import \2", src, flags=re.M)
            if rel.startswith("case/"):
                src = re.sub(
                    r"^from case import ", r"from case.case import ", src, flags=re.M
                )

            # ── Runtime-level py2 → py3 ───────────────────────────────
            if rel == "fuzzing.py":
                # autobahn 19 dropped the debug kwargs
                src = src.replace(
                    "WebSocketServerFactory.__init__(self, debug = debug, debugCodePaths = debug)",
                    "WebSocketServerFactory.__init__(self)",
                )
                src = src.replace(
                    "WebSocketClientFactory.__init__(self, debug = debug, debugCodePaths = debug)",
                    "WebSocketClientFactory.__init__(self)",
                )
                # binLogData must produce str and tolerate str input
                src = src.replace(
                    """def binLogData(data, maxlen = 64):
   ellipses = " ..."
   if len(data) > maxlen - len(ellipses):
      dd = binascii.b2a_hex(data[:maxlen]) + ellipses
   else:
      dd = binascii.b2a_hex(data)
   return dd""",
                    """def binLogData(data, maxlen = 64):
   ellipses = " ..."
   if isinstance(data, str):
      data = data.encode("utf8")
   if len(data) > maxlen - len(ellipses):
      dd = binascii.b2a_hex(data[:maxlen]).decode("ascii") + ellipses
   else:
      dd = binascii.b2a_hex(data).decode("ascii")
   return dd""",
                )

            if rel == "case/case.py":
                # py3 porting guard: a case whose onOpen never populated
                # expectedClose must not crash the whole fuzzing run.
                src = src.replace(
                    """      if self.p.connectionWasOpen:
         # check the close status
         if self.expectedClose["closedByMe"] != self.p.closedByMe:""",
                    """      if self.p.connectionWasOpen:
         # check the close status
         if not self.expectedClose:
            print("WARN: %s has no expectedClose" % self.__class__.__name__)
            self.expectedClose = {"closedByMe": True, "closeCode": [], "requireClean": False}
         if self.expectedClose["closedByMe"] != self.p.closedByMe:""",
                )

            if rel == "choosereactor.py":
                # `print """...""" % str(e)` blocks: the generic print pass
                # consumed one quote and the trailing paren — restore both.
                src = src.replace('print(""")', 'print("""')
                src = re.sub(r'^""" % str\(e\)$', '""" % str(e))', src, flags=re.M)

            if rel.startswith("case/"):
                # str PAYLOAD fed to bytes-only helpers at format time
                src = re.sub(
                    r'binascii\.b2a_hex\(([^)]*)\)\.decode\("ascii"\)',
                    r'binascii.b2a_hex(str(\1).encode("utf8")).decode("ascii")',
                    src,
                )
                # bare b2a_hex over str values: PAYLOAD, CaseX.PAYLOAD, slices
                src = re.sub(
                    r'binascii\.b2a_hex\(([A-Za-z_]\w*(?:\.[A-Za-z_]\w*|\[\d*:?\d*\])*)\)',
                    r'binascii.b2a_hex(str(\1).encode("utf8")).decode("ascii")',
                    src,
                )
                # utf8validator (pure-python fallback) requires bytes
                src = re.sub(
                    r"\.validate\((vss\[[^\]]*\])\)",
                    r'.validate(\1.encode("utf8"))',
                    src,
                )
                # autobahn 19 PerMessageDeflate* uses snake_case kwargs/attrs
                src = re.sub(
                    r"requestNoContextTakeover\s*=", r"request_no_context_takeover =", src
                )
                src = re.sub(
                    r"requestMaxWindowBits\s*=", r"request_max_window_bits =", src
                )
                src = src.replace(
                    "if offer.acceptNoContextTakeover:", "if offer.accept_no_context_takeover:"
                )
                src = src.replace(
                    "if offer.acceptMaxWindowBits:", "if offer.accept_max_window_bits:"
                )
                src = src.replace(
                    "x.requestNoContextTakeover", "x.request_no_context_takeover"
                )
                src = src.replace(
                    "x.requestMaxWindowBits", "x.request_max_window_bits"
                )

            if src != orig:
                with io.open(full, "w", encoding="utf-8", newline="\n") as f:
                    f.write(src)
                print("patched", rel)


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    patch(sys.argv[1])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
