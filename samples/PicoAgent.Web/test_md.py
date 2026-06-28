import sys; sys.stdout.reconfigure(encoding='utf-8')
from playwright.sync_api import sync_playwright
with sync_playwright() as p:
    b = p.chromium.launch(headless=True)
    pg = b.new_page()
    errs = []
    pg.on('console', lambda m: errs.append(f'{m.type}: {m.text[:120]}') if m.type in ('error','warning') else None)
    pg.goto('http://localhost:9000/', wait_until='networkidle')
    pg.wait_for_timeout(1500)

    md = pg.evaluate('typeof marked !== "undefined"')
    mm = pg.evaluate('typeof mermaid !== "undefined"')
    print(f'Marked: {md}, Mermaid: {mm}')

    # Test markdown in user message
    pg.locator('#input').fill('# Hello\n\n- item 1\n- item 2')
    pg.locator('#send').click()
    pg.wait_for_timeout(3000)
    html = pg.locator('.message.user .msg-content').first.inner_html()
    has_h1 = '<h1>' in html
    has_li = '<li>' in html
    print(f'User HTML has h1: {has_h1}, li: {has_li}')
    assert has_h1 and has_li

    # Test mermaid code block via evaluate
    pg.evaluate('''() => {
        const el = document.querySelector(".message.user .msg-content");
        el.innerHTML = marked.parse("diagram:\\n```mermaid\\ngraph TD\\nA-->B\\n```");
    }''')
    pg.wait_for_timeout(2000)
    ph = pg.locator('.mermaid-placeholder').count()
    print(f'Mermaid placeholder: {ph}')
    assert ph >= 1, 'No mermaid placeholder'

    # Now send a real message through SSE with markdown
    pg.locator('#input').fill('**bold** and `code`')
    pg.locator('#send').click()
    pg.wait_for_timeout(5000)
    last_html = pg.locator('.message.assistant .msg-content').last.inner_html()
    print(f'Last assistant HTML: {last_html[:150]}')

    if errs: print('Console:', errs[:5])
    print('ALL PASSED')
    b.close()
