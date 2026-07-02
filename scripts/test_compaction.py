"""Headless functional test for PicoAgent.Web compaction feature."""
import sys
import time
import requests
import io
from playwright.sync_api import sync_playwright

# Force UTF-8 output on Windows
if sys.platform == "win32":
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8')

BASE = "http://localhost:9000"
API_KEY = "sk-5ac3e7e59fab48bcaddae5098f28a31b"

def wait_for_server(url, timeout=30):
    start = time.time()
    while time.time() - start < timeout:
        try:
            r = requests.get(url, timeout=2)
            return True
        except:
            time.sleep(0.5)
    return False

def main():
    print("=" * 60)
    print("PicoAgent.Web Compaction Functional Test")
    print("=" * 60)

    errors = []

    print("\n[1] Waiting for servers...")
    if not wait_for_server(f"{BASE}/"):
        print("  FAIL: Web UI server not reachable")
        errors.append("server_not_reachable")
        print_summary(errors)
        return
    print("  OK: Web UI reachable")

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page(viewport={"width": 1280, "height": 800})

        console_errors = []
        page.on("console", lambda msg: (
            console_errors.append(f"[{msg.type}] {msg.text}")
            if msg.type == "error" else None
        ))

        # --- Navigate to main page ---
        print("\n[2] Navigating to main page...")
        page.goto(f"{BASE}/")
        page.wait_for_load_state("networkidle")
        page.wait_for_timeout(2000)

        if page.url.endswith("/config.html"):
            print("  On config page - configuring...")
            provider_sel = page.locator("#provider")
            provider_sel.wait_for(state="visible", timeout=10000)
            options = provider_sel.locator("option")
            opt_count = options.count()
            for i in range(opt_count):
                txt = options.nth(i).text_content()
                if txt and "deepseek" in txt.lower():
                    val = options.nth(i).get_attribute("value")
                    provider_sel.select_option(value=val)
                    print(f"  Selected: {txt}")
                    break

            page.locator("#apikey").fill(API_KEY)
            print("  API key entered")
            page.locator("#btn-validate").click()
            page.wait_for_timeout(5000)

            btn_save = page.locator("#btn-save")
            try:
                btn_save.wait_for(state="visible", timeout=30000)
                btn_save.click()
                page.wait_for_load_state("networkidle")
                page.wait_for_timeout(3000)
                print("  Config saved")
            except:
                print("  WARN: Save button not visible")
                page.screenshot(path="/tmp/picoagent_config_err.png")

            page.goto(f"{BASE}/")
            page.wait_for_load_state("networkidle")
            page.wait_for_timeout(2000)

        if page.url.endswith("/config.html"):
            print("  FAIL: Still on config page")
            errors.append("redirect_loop")
            page.screenshot(path="/tmp/picoagent_02_config_loop.png")
        else:
            print("  OK: On main page")
            page.screenshot(path="/tmp/picoagent_01_main.png")

        # --- Send test messages to create sessions ---
        print("\n[3] Sending test messages...")
        if not page.url.endswith("/config.html"):
            input_box = page.locator("#input")
            send_btn = page.locator("#send")

            if input_box.count() > 0 and send_btn.count() > 0:
                msgs = ["Hello!", "What is 2+2?"]
                for msg in msgs:
                    input_box.fill(msg)
                    send_btn.click()
                    page.wait_for_timeout(15000)
                    print(f"  Sent: '{msg}'")

                page.screenshot(path="/tmp/picoagent_03_chat.png")
                # Wait for session list refresh
                page.wait_for_timeout(2000)
            else:
                print("  SKIP: Input/send not found")
        else:
            print("  SKIP: On config page")

        # --- Check session list and compact buttons ---
        print("\n[4] Checking session list and compact buttons...")
        page.wait_for_timeout(1000)
        session_items = page.locator("#session-list .session")
        count = session_items.count()
        print(f"  Session items: {count}")

        compact_btns = page.locator(".compact-btn")
        compact_count = compact_btns.count()
        print(f"  Compact buttons: {compact_count}")

        if count == 0:
            print("  FAIL: No session items in list")
            errors.append("no_sessions")
        elif compact_count == 0:
            print("  FAIL: No compact buttons in session list")
            errors.append("no_compact_buttons")
        else:
            print("  OK: Sessions and compact buttons present")
            # Verify each session has a compact button
            for i in range(min(count, 5)):
                s = session_items.nth(i)
                btns = s.locator(".compact-btn")
                btn_text = btns.first.text_content() if btns.count() > 0 else "NONE"
                print(f"  Session #{i}: compact btn='{btn_text}'")

        page.screenshot(path="/tmp/picoagent_04_sessions.png")

        # --- Click compact button ---
        print("\n[5] Testing compact button click...")
        compact_btns = page.locator(".compact-btn")
        if compact_btns.count() > 0:
            btn = compact_btns.first
            original = btn.text_content() or ""
            print(f"  Button text before: '{original}' (len={len(original)})")

            btn.click()
            page.wait_for_timeout(4000)

            new_text = btn.text_content() or ""
            print(f"  Button text after: '{new_text}' (len={len(new_text)})")

            # Look for toast
            toast = page.locator(".toast.show")
            if toast.count() > 0:
                msg = toast.first.text_content()
                print(f"  Toast: '{msg}'")
            else:
                toast_all = page.locator(".toast")
                if toast_all.count() > 0:
                    print(f"  Toast (hidden): '{toast_all.first.text_content()}'")
                else:
                    print("  No toast element")

            page.screenshot(path="/tmp/picoagent_05_compact.png")
        else:
            print("  SKIP: No compact buttons")

        # --- API tests ---
        print("\n[6] Direct API tests...")
        api_tests = [
            ("default compact", "default", 20),
            ("custom keepRecent=5", "default", 5),
            ("nonexistent session", "nosuchsession", 20),
        ]
        for label, sid, kr in api_tests:
            try:
                resp = requests.post(
                    f"{BASE}/api/session/{sid}/compact",
                    json={"keepRecent": kr}, timeout=30
                )
                ok = resp.status_code == 200
                print(f"  {'OK' if ok else 'FAIL'}: {label} -> HTTP {resp.status_code}: {resp.text[:150]}")
                if not ok:
                    errors.append(f"api_fail_{label}")
            except Exception as e:
                print(f"  FAIL: {label} -> {e}")
                errors.append(f"api_error_{label}")

        # --- Browser console ---
        print(f"\n[7] Browser console ({len(console_errors)}):")
        for ce in console_errors:
            print(f"  {ce[:200]}")

        browser.close()

    print_summary(errors)

def print_summary(errors):
    print("\n" + "=" * 60)
    if errors:
        print(f"TEST RESULT: FAIL ({len(errors)} issues)")
        for e in errors:
            print(f"  - {e}")
    else:
        print("TEST RESULT: PASS")
    print("=" * 60)
    sys.exit(0 if not errors else 1)

if __name__ == "__main__":
    main()
