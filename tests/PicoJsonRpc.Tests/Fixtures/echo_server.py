# NDJSON echo fixture (Python parity of echo_server.mjs) — reads request
# frames on stdin, replies a response frame per line with the request id
# preserved and the request echoed as raw JSON text in `result`.
import json
import sys

for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        req = json.loads(line)
        out = {"jsonrpc": "2.0", "id": req.get("id")}
        out["result"] = json.dumps({"echo": req})
        sys.stdout.write(json.dumps(out) + "\n")
    except Exception:
        sys.stdout.write(
            json.dumps({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "parse"}}) + "\n"
        )
    sys.stdout.flush()