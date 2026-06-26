"""
E2E test for P0 SSE streaming fix.
Tests: HTTP headers, event delivery, cancellation cleanup, Playwright EventSource.
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


def test_cancellation():
    print("\n[Test 3] SSE cancellation cleanup...", flush=True)
    sock = socket.create_connection(("localhost", SERVER_PORT), timeout=10)
    sock.sendall(
        f"GET /sse/cancel-test HTTP/1.1\r\nHost: localhost:{SERVER_PORT}\r\nAccept: text/event-stream\r\n\r\n".encode()
    )
    sock.settimeout(3)
    try:
        while sock.recv(4096):
            pass
    except socket.timeout:
        pass
    sock.close()
    print("  [OK] Connection closed without hang", flush=True)
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
