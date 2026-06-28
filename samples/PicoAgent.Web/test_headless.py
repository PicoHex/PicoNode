"""PicoAgent.Web headless browser test."""
from playwright.sync_api import sync_playwright
import sys

errors = []

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page()

    page.on("console", lambda msg: (
        errors.append(f"CONSOLE {msg.type}: {msg.text}")
        if msg.type in ("error", "warning") else None
    ))
    page.on("pageerror", lambda err: errors.append(f"PAGE ERROR: {err}"))

    print("1. Load page")
    page.goto('http://localhost:9000/', wait_until='networkidle', timeout=10000)
    page.wait_for_timeout(1000)
    print(f"Title: {page.title()}")
    assert 'PicoAgent' in page.title(), f"Wrong title: {page.title()}"

    print("2. Check DOM")
    assert page.locator('#input').is_visible()
    assert page.locator('#send').is_visible()
    assert page.locator('#messages').is_visible()
    assert page.locator('#status').is_visible()
    print("All elements present")

    print("3. Health check")
    page.wait_for_timeout(1500)
    st = page.locator('#status').inner_text()
    print(f"Status: {st}")
    assert 'model:' in st.lower(), f"Status not updated: {st}"

    print("4. Send message")
    page.locator('#input').fill("Hello test")
    page.locator('#send').click()
    page.wait_for_timeout(500)
    um = page.locator('.message.user')
    assert um.count() == 1, f"Expected 1 user msg, got {um.count()}"

    print("5. Wait for SSE response")
    page.wait_for_timeout(5000)
    am = page.locator('.message.assistant')
    print(f"Assistant msgs: {am.count()}")
    if am.count() > 0:
        print(f"Content: {am.first.inner_text()[:150]}")

    tb = page.locator('.thinking')
    print(f"Thinking blocks: {tb.count()}")

    page.screenshot(path='D:/MyProjects/PicoHex/PicoNode/samples/PicoAgent.Web/test-screenshot.png', full_page=True)
    print("Screenshot saved")

    if errors:
        print("\nERRORS:")
        for e in errors:
            print(f"  {e}")
        print("FAILED")
        sys.exit(1)
    else:
        print("PASSED")

    browser.close()
