import asyncio
from playwright.async_api import async_playwright

async def test():
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True, args=["--no-sandbox"])
        page = await browser.new_page()
        results = []
        
        all_requests = []
        page.on("request", lambda req: all_requests.append(req.url))
        console_msgs = []
        page.on("console", lambda msg: console_msgs.append(f"[{msg.type}] {msg.text}"))
        
        # 1. Page loads
        await page.goto("http://127.0.0.1:9000/", timeout=20000, wait_until="domcontentloaded")
        await page.wait_for_timeout(2000)
        title = await page.title()
        results.append(("Page loads", title == "PicoAgent", title))
        
        # 2. Static files loaded
        static_files = [r for r in all_requests if r.endswith((".css", ".js"))]
        results.append(("Static files", len(static_files) >= 2, f"{len(static_files)} files"))
        
        # 3. Health check proxied to backend
        health_calls = [r for r in all_requests if "health" in r]
        results.append(("Health API call", len(health_calls) > 0, f"{len(health_calls)} calls"))
        
        # 4. Input field exists
        inp = await page.query_selector("textarea, input[type='text']")
        results.append(("Input field", inp is not None, ""))
        
        # 5. Send button exists
        send_btn = None
        for b in await page.query_selector_all("button"):
            if "Send" in (await b.inner_text() or ""):
                send_btn = b
                break
        results.append(("Send button", send_btn is not None, ""))
        
        # 6. Send message triggers API call AND renders response
        if inp and send_btn:
            pre_count = len(all_requests)
            # Capture DOM state before sending
            pre_html = await page.content()
            await inp.fill("What is PicoNode?")
            await send_btn.click()
            # Wait for response to render (new content appears)
            await page.wait_for_timeout(6000)
            
            post_requests = all_requests[pre_count:]
            api_calls = [r for r in post_requests if "message" in r or "session" in r]
            results.append(("Message API call", len(api_calls) > 0, f"{len(api_calls)} new API calls"))
            
            # Verify new DOM content appeared (not error)
            post_html = await page.content()
            new_content = len(post_html) > len(pre_html) + 50  # at least 50 chars added
            has_error = "error" in post_html.lower() and "error" not in pre_html.lower()
            response_rendered = new_content and not has_error
            results.append(("Response rendered (no error)", response_rendered,
                f"+{len(post_html)-len(pre_html)} chars, error={'yes' if has_error else 'no'}"))
        else:
            results.append(("Message API call", False, "Missing input or button"))
            results.append(("Response rendered", False, "Skipped"))
        
        # 7. No console errors
        errors = [m for m in console_msgs if "[error]" in m.lower()]
        results.append(("No console errors", len(errors) == 0, str(errors[:3]) if errors else "none"))
        
        # 8. Screenshot
        await page.screenshot(path="/tmp/picoagent-web-final.png")
        results.append(("Screenshot", True, "/tmp/picoagent-web-final.png"))
        
        await browser.close()
        
        passed = sum(1 for _, ok, _ in results if ok)
        total = len(results)
        print(f"=== Results: {passed}/{total} ===")
        for name, ok, detail in results:
            print(f"{'PASS' if ok else 'FAIL'} {name}: {detail}")
        return passed == total

ok = asyncio.run(test())
exit(0 if ok else 1)
