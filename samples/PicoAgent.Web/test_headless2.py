"""PicoAgent.Web thorough edge-case test."""
from playwright.sync_api import sync_playwright
import sys

errors = []

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page()
    page.on("console", lambda m: errors.append(f"CONSOLE {m.type}: {m.text}") if m.type in ("error","warning") else None)
    page.on("pageerror", lambda e: errors.append(f"PAGE: {e}"))

    page.goto('http://localhost:9000/', wait_until='networkidle', timeout=10000)
    page.wait_for_timeout(500)

    tests = []

    # Test 1: Empty input should not send
    page.locator('#input').fill("")
    page.locator('#send').click()
    page.wait_for_timeout(300)
    c = page.locator('.message.user').count()
    tests.append(("Empty input blocked", c == 0, f"got {c} msgs"))

    # Test 2: Whitespace only should not send
    page.locator('#input').fill("   ")
    page.locator('#send').click()
    page.wait_for_timeout(300)
    c = page.locator('.message.user').count()
    tests.append(("Whitespace blocked", c == 0, f"got {c} msgs"))

    # Test 3: Normal message
    page.locator('#input').fill("Hello world")
    page.locator('#send').click()
    page.wait_for_timeout(500)
    c = page.locator('.message.user').count()
    tests.append(("Message sent", c == 1, f"got {c}"))

    # Test 4: SSE response received
    page.wait_for_timeout(5000)
    c = page.locator('.message.assistant').count()
    tests.append(("SSE response", c >= 1, f"got {c}"))

    # Test 5: Enter key sends
    page.locator('#input').fill("Enter test")
    page.locator('#input').press('Enter')
    page.wait_for_timeout(500)
    c = page.locator('.message.user').count()
    tests.append(("Enter sends", c == 2, f"got {c}"))

    # Test 6: Shift+Enter does NOT send (multiline support placeholder)
    page.locator('#input').fill("shift")
    page.locator('#input').press('Shift+Enter')
    page.wait_for_timeout(300)
    c = page.locator('.message.user').count()
    tests.append(("Shift+Enter does not send", c == 2, f"got {c}"))

    # Test 7: Model switch
    resp = page.evaluate('''async () => {
        const r = await fetch('/api/model/switch', {method:'POST', headers:{'Content-Type':'application/json'}, body:'{"modelId":"gpt-4"}'});
        return await r.json();
    }''')
    tests.append(("Model switch API", resp.get('status') == 'ok', str(resp)))

    # Test 8: Thinking block exists after message
    tb = page.locator('.thinking').count()
    tests.append(("Thinking block rendered", tb >= 1, f"got {tb}"))

    page.screenshot(path='D:/MyProjects/PicoHex/PicoNode/samples/PicoAgent.Web/test-screenshot2.png', full_page=True)

    print("=" * 40)
    passed = 0; failed = 0
    for name, ok, detail in tests:
        status = "PASS" if ok else "FAIL"
        if ok: passed += 1
        else: failed += 1
        print(f"{status} {name}: {detail}")
    print(f"\n{passed}/{passed+failed} passed")
    if errors:
        print(f"\nCONSOLE ERRORS ({len(errors)}):")
        for e in errors: print(f"  {e}")

    browser.close()
    sys.exit(0 if failed == 0 else 1)
