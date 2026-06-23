"""Verify Auth, RateLimit, and SSE demo panels using Playwright."""
import sys, json
from playwright.sync_api import sync_playwright

BASE_URL = "http://localhost:7004"

def main():
    failures = []

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        context = browser.new_context()
        page = context.new_page()

        page.on("pageerror", lambda err: print(f"  [PAGE_ERROR] {err}"))

        page.goto(BASE_URL, wait_until="networkidle")
        page.wait_for_timeout(2000)

        # ── Auth: Set token first, so all subsequent requests are authenticated ──
        print("=== Auth: Set token ===")
        page.locator("#auth-token").fill("alice")
        page.locator("#btn-auth-set").click()
        page.wait_for_timeout(300)

        # ── SSE Panel (fresh key "alice" - no prior rate limit consumption) ──
        print("=== SSE Panel ===")
        page.locator("#btn-sse-connect").click()
        page.wait_for_timeout(5000)

        out = page.locator("#out-sse").text_content()
        print(f"  SSE output length: {len(out)} chars")

        # Check for event DATA content (not type - JS output omits event type)
        sse_checks = [
            ("Starting demo",       "status event"),
            ("Hello from PicoWeb",  "text_delta event"),
            ("calculator",           "tool_call event"),
            ("result: 4",           "code block event"),
            ("demo complete",       "done event"),
        ]
        for (keyword, label) in sse_checks:
            if keyword in out:
                print(f"  PASS: {label}")
            else:
                failures.append(f"SSE '{label}' missing (no '{keyword}' in output)")

        disc = page.locator("#btn-sse-disconnect")
        if disc.is_enabled():
            disc.click()
            page.wait_for_timeout(300)

        # ── Auth: Status ──
        print("=== Auth: Status ===")
        page.locator("#btn-auth-status").click()
        page.wait_for_timeout(800)
        out = page.locator("#out-auth").text_content()
        if "alice" in out:
            print("  PASS: Auth shows userId=alice")
        else:
            failures.append(f"Auth: {out[:80]}")

        # ── Rate Limit (authenticated key "alice") ──
        print("=== Rate Limit: ping until rate limited ===")
        ping = page.locator("#btn-rate-ping")
        got_429 = False
        for i in range(10):
            ping.click()
            page.wait_for_timeout(300)
            meta = page.locator("#meta-rate").text_content()
            print(f"  Ping {i+1}: {meta}")
            if "429" in meta or "RATE" in meta:
                got_429 = True
                print(f"  PASS: Rate limited on attempt {i+1}")
                break

        if not got_429:
            failures.append("Rate limit never triggered after 10 pings")

        # ── Auth: Clear token ──
        print("=== Auth: Clear ===")
        page.locator("#btn-auth-clear").click()
        page.wait_for_timeout(300)
        page.locator("#btn-auth-status").click()
        page.wait_for_timeout(800)
        out = page.locator("#out-auth").text_content()
        if "false" in out:
            print("  PASS: Unauthenticated after clear")
        else:
            failures.append(f"Auth after clear: {out[:80]}")

        browser.close()

    if failures:
        print(f"\n*** {len(failures)} FAILURES ***")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    else:
        print("\n*** ALL PASSED ***")
        sys.exit(0)

if __name__ == "__main__":
    main()
