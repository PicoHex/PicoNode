"""Verify session persistence in PicoWeb.Samples using Playwright."""
import sys
from playwright.sync_api import sync_playwright

BASE_URL = "http://localhost:7004"

def main():
    failures = []

    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        context = browser.new_context()
        page = context.new_page()

        page.goto(BASE_URL, wait_until="networkidle")
        page.wait_for_timeout(1500)

        prev_session_id = None

        for i in range(3):
            count_btn = page.locator("#btn-session-count")
            count_btn.click()
            page.wait_for_timeout(1000)

            meta = page.locator("#meta-session").text_content()
            output = page.locator("#out-session").text_content()

            # Parse JSON output
            import json
            try:
                data = json.loads(output)
            except json.JSONDecodeError:
                failures.append(f"Click #{i+1}: Could not parse JSON: {output[:100]}")
                continue

            sid = data.get("sessionId")
            counter = data.get("counter")

            if counter != i + 1:
                failures.append(
                    f"Click #{i+1}: Expected counter={i+1}, got counter={counter}"
                )

            if prev_session_id is not None and sid != prev_session_id:
                failures.append(
                    f"Click #{i+1}: Session ID changed! Was {prev_session_id}, now {sid}"
                )

            if sid is None:
                failures.append(f"Click #{i+1}: No sessionId in response")

            prev_session_id = sid

        # Click Info to check metadata
        info_btn = page.locator("#btn-session-info")
        info_btn.click()
        page.wait_for_timeout(1000)

        meta = page.locator("#meta-session").text_content()
        output = page.locator("#out-session").text_content()

        try:
            info_data = json.loads(output)
            if info_data.get("id") != prev_session_id:
                failures.append(
                    f"Info: Expected session ID {prev_session_id}, got {info_data.get('id')}"
                )
        except json.JSONDecodeError:
            failures.append(f"Info: Could not parse JSON: {output[:100]}")

        # Click Reset
        reset_btn = page.locator("#btn-session-reset")
        reset_btn.click()
        page.wait_for_timeout(1000)

        # After reset, count should be 1 again
        count_btn.click()
        page.wait_for_timeout(1000)

        output = page.locator("#out-session").text_content()
        try:
            data = json.loads(output)
            if data.get("counter") != 1:
                failures.append(f"After reset: Expected counter=1, got counter={data.get('counter')}")
        except json.JSONDecodeError:
            failures.append(f"After reset: Could not parse JSON: {output[:100]}")

        # Verify cookies
        cookies = context.cookies()
        sid_cookies = [c for c in cookies if c["name"] == "sid"]
        if not sid_cookies:
            failures.append("No 'sid' cookie found! Session cookie was not set.")

        browser.close()

    if failures:
        print("FAILURES:")
        for f in failures:
            print(f"  - {f}")
        sys.exit(1)
    else:
        print("All session checks passed!")
        sys.exit(0)

if __name__ == "__main__":
    main()
