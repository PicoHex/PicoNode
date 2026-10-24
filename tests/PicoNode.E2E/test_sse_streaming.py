"""
E2E test for P0 SSE streaming fix.
Tests: HTTP headers, event delivery, cancellation observed by the server, Playwright EventSource.
"""
import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

SERVER_PORT = 18789
SERVER_URL = f"http://localhost:{SERVER_PORT}"
CANCEL_PROBE_URL = f"{SERVER_URL}/sse/cancel-test/probe"
SERVER_DIR = str(Path(__file__).resolve().parent / "SseTestServer")


def wait_for_server(url, timeout=30):
    start = time.time()
    while time.time() - start < timeout:
        try:
            urllib.request.urlopen(url, timeout=2)
            return True
        except urllib.error.HTTPError:
            return True
        except Exception:
            time.sleep(0.5)
    return False


def parse_sse(data: bytes):
    events = []
    for block in data.decode("utf-8").split("\n\n"):
        block = block.strip()
        if not block:
            continue
        evt = {}
        for line in block.split("\n"):
            if line.startswith("event: "):
                evt["event"] = line[7:]
            elif line.startswith("data: "):
                evt["data"] = line[6:]
        if evt:
            events.append(evt)
    return events


def test_headers():
    print("\n[Test 1] SSE headers...", flush=True)
    req = urllib.request.Request(f"{SERVER_URL}/sse/stream")
    resp = urllib.request.urlopen(req, timeout=15)
    ct = resp.getheader("Content-Type")
    assert ct == "text/event-stream", f"Content-Type: {ct}"
    print(f"  [OK] Content-Type: {ct}", flush=True)
    te = resp.getheader("Transfer-Encoding")
    assert te == "chunked", f"Transfer-Encoding: {te}"
    print(f"  [OK] Transfer-Encoding: {te}", flush=True)
    cc = resp.getheader("Cache-Control")
    assert cc == "no-cache", f"Cache-Control: {cc}"
    print(f"  [OK] Cache-Control: {cc}", flush=True)
    resp.read()
    return True


def test_streaming():
    print("\n[Test 2] SSE event delivery...", flush=True)
    req = urllib.request.Request(f"{SERVER_URL}/sse/stream")
    resp = urllib.request.urlopen(req, timeout=15)
    raw = resp.read()
    events = parse_sse(raw)
    assert len(events) == 6, f"Expected 6 events, got {len(events)}"
    for i in range(5):
        assert events[i]["event"] == "update", f"Event {i}: expected 'update', got '{events[i].get('event')}'"
        assert json.loads(events[i]["data"])["seq"] == i + 1
    assert events[5]["data"] == "[DONE]"
    print(f"  [OK] Received {len(events)} events (5 updates + [DONE])", flush=True)
    return True


def read_cancel_probe():
    """Read the server's own account of what the /sse/cancel-test handler did."""
    with urllib.request.urlopen(CANCEL_PROBE_URL, timeout=10) as resp:
        return json.loads(resp.read().decode("utf-8"))


def wait_for_cancel_probe(predicate, timeout=5.0):
    """Poll the server-side probe until `predicate` holds, then return the last sample."""
    deadline = time.time() + timeout
    probe = read_cancel_probe()
    while not predicate(probe) and time.time() < deadline:
        time.sleep(0.05)
        probe = read_cancel_probe()
    return probe


def test_cancellation():
    print("\n[Test 3] SSE cancellation observed by the server...", flush=True)

    before = read_cancel_probe()

    sock = socket.create_connection(("localhost", SERVER_PORT), timeout=10)
    sock.sendall(
        f"GET /sse/cancel-test HTTP/1.1\r\nHost: localhost:{SERVER_PORT}\r\nAccept: text/event-stream\r\n\r\n".encode()
    )
    sock.settimeout(3)

    # (1) The stream is genuinely live before we hang up.
    received = b""
    opened = False
    try:
        while b"event: start" not in received:
            chunk = sock.recv(4096)
            if not chunk:
                break
            received += chunk
        opened = b"event: start" in received
    except socket.timeout:
        opened = False

    assert opened, "no 'event: start' reached the client: the stream never became live"
    print("  [OK] client received 'event: start'", flush=True)

    # (2) Half-close: the client stops sending but keeps reading. From here on the only
    # thing worth asserting is what the SERVER saw — that our own socket ends proves
    # nothing about whether the handler ever noticed.
    sock.shutdown(socket.SHUT_WR)

    try:
        # (3) The server-side handler itself must have observed the disconnect (its token
        # fired) and unwound. This is the probe endpoint's whole purpose.
        probe = wait_for_cancel_probe(
            lambda p: p["cancel_observed"] > before["cancel_observed"] or p["exited"] > before["exited"],
            timeout=5.0,
        )
        assert probe["entered"] == before["entered"] + 1, (
            f"the /sse/cancel-test handler never ran for this request: {before} -> {probe}"
        )
        assert probe["cancel_observed"] > before["cancel_observed"], (
            f"server-side handler never observed the client disconnect: {before} -> {probe}"
        )
        assert probe["exited"] > before["exited"], (
            f"server-side handler never unwound after the disconnect: {before} -> {probe}"
        )
        print(f"  [OK] server handler observed the disconnect and unwound: {probe}", flush=True)

        # (4) The client-visible consequence: the response ends instead of hanging on the
        # dangling pipe. If the disconnect is never propagated, this read blocks until the
        # 3s timeout.
        tail = b""
        timed_out = False
        try:
            while True:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                tail += chunk
        except socket.timeout:
            timed_out = True
        assert not timed_out, "server did not end the SSE response within 3s of the client half-close"
        print(
            f"  [OK] server ended the response after the client hung up ({len(received) + len(tail)} bytes)",
            flush=True,
        )
    finally:
        sock.close()
    return True


def test_thinking_stream():
    print("\n[Test 3b] SSE thinking event delivery...", flush=True)
    req = urllib.request.Request(f"{SERVER_URL}/sse/thinking")
    resp = urllib.request.urlopen(req, timeout=15)
    raw = resp.read()
    events = parse_sse(raw)

    assert len(events) >= 3, f"Expected at least 3 events, got {len(events)}"

    thinking_events = [e for e in events if e.get("event") == "thinking"]
    assert len(thinking_events) == 2, f"Expected 2 thinking events, got {len(thinking_events)}"

    delta_events = [e for e in events if e.get("event") == "delta"]
    assert len(delta_events) == 1, f"Expected 1 delta event, got {len(delta_events)}"

    done_events = [e for e in events if e.get("data") == "[DONE]"]
    assert len(done_events) == 1, f"Expected 1 [DONE] event, got {len(done_events)}"

    print(f"  [OK] Thinking stream: {len(thinking_events)} thinking + {len(delta_events)} delta + [DONE]", flush=True)
    return True


def test_playwright():
    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        print("\n[Test 4] Playwright not available, skip", flush=True)
        return True

    print("\n[Test 4] Playwright API request verification...", flush=True)

    with sync_playwright() as p:
        # Use Playwright's APIRequestContext (bypasses browser CORS)
        request_context = p.request.new_context()

        resp = request_context.get(f"{SERVER_URL}/sse/stream")

        ct = resp.headers.get("content-type", "")
        assert "text/event-stream" in ct, f"Content-Type: {ct}"
        print(f"  [OK] Content-Type: {ct}", flush=True)

        body = resp.body()
        events = parse_sse(body)
        assert len(events) == 6, f"Expected 6 events, got {len(events)}"
        for i in range(5):
            assert events[i]["event"] == "update"
            assert json.loads(events[i]["data"])["seq"] == i + 1
        assert events[5]["data"] == "[DONE]"
        print(f"  [OK] Playwright API received {len(events)} events", flush=True)

        request_context.dispose()
    return True


def main():
    print("=" * 60, flush=True)
    print("PicoNode SSE Streaming E2E Test", flush=True)
    print("=" * 60, flush=True)

    print("\nBuilding test server...", flush=True)
    r = subprocess.run(
        ["dotnet", "build", SERVER_DIR],
        capture_output=True, text=True, timeout=120,
    )
    if r.returncode != 0:
        print(f"[FAIL] Build failed (rc={r.returncode})", flush=True)
        for line in (r.stderr or "").splitlines()[-5:]:
            print(f"  {line}", flush=True)
        return False

    print("Starting server...", flush=True)
    proc = subprocess.Popen(
        ["dotnet", "run", "--project", SERVER_DIR, "--no-build"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )

    try:
        if not wait_for_server(f"{SERVER_URL}/sse/stream"):
            print("[FAIL] Server failed to start", flush=True)
            return False
        print(f"[OK] Server ready\n", flush=True)

        tests = [test_headers, test_streaming, test_thinking_stream, test_cancellation, test_playwright]
        passed = all(t() for t in tests)

        print("\n" + "=" * 60, flush=True)
        if passed:
            print("[OK] ALL TESTS PASSED", flush=True)
        else:
            print("[FAIL] SOME TESTS FAILED", flush=True)
        return passed
    finally:
        print("Shutting down server...", flush=True)
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()


if __name__ == "__main__":
    sys.exit(0 if main() else 1)
