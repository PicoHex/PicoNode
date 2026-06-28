const input = document.getElementById('input');
const sendBtn = document.getElementById('send');
const messages = document.getElementById('messages');
const status = document.getElementById('status');

fetch('/api/health').then(r => r.json()).then(h => {
    status.querySelector('span').textContent = h.model;
});

let currentAssistant = null;
let renderTimer = null;

async function sendMessage() {
    const text = input.value.trim();
    if (!text) return;
    input.value = '';

    appendMessage('user', text);
    currentAssistant = appendMessage('assistant', '');
    const thinkingBlock = appendThinkingBlock(currentAssistant);

    const response = await fetch('/api/session/default/message', {
        method: 'POST',
        body: text,
    });

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    let rawText = '';
    let rawThinking = '';
    const contentEl = currentAssistant.querySelector('.msg-content');

    while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        const lines = buffer.split('\n');
        buffer = lines.pop() || '';
        for (const line of lines) {
            if (!line.startsWith('data: ')) continue;
            const data = line.slice(6);
            if (data === '[DONE]') {
                flushRender(contentEl, rawText);
                if (rawThinking) {
                    thinkingBlock.querySelector('.think-content').innerHTML =
                        marked.parse(rawThinking);
                }
                currentAssistant = null;
                return;
            }
            const evt = JSON.parse(data);
            switch (evt.type) {
                case 'delta':
                    rawText += evt.content;
                    debouncedRender(contentEl, rawText);
                    break;
                case 'thinking':
                    rawThinking += evt.content;
                    thinkingBlock.querySelector('.think-content').textContent = rawThinking;
                    break;
                case 'error':
                    rawText += `\n\n> ⚠️ ${evt.message}`;
                    debouncedRender(contentEl, rawText);
                    break;
            }
            messages.scrollTop = messages.scrollHeight;
        }
    }
}

function debouncedRender(el, raw) {
    clearTimeout(renderTimer);
    renderTimer = setTimeout(() => flushRender(el, raw), 80);
}

function flushRender(el, raw) {
    clearTimeout(renderTimer);
    el.innerHTML = marked.parse(raw);
    // Hide Mermaid placeholder divs before init
    el.querySelectorAll('pre.mermaid-placeholder').forEach(async pre => {
        const code = pre.textContent;
        try {
            const { svg } = await mermaid.render('mermaid-' + Math.random().toString(36).slice(2), code);
            pre.outerHTML = `<div class="mermaid-rendered">${svg}</div>`;
        } catch {
            pre.classList.add('mermaid-error');
            pre.textContent = '[Mermaid render error]\n' + code;
        }
    });
}

function appendMessage(role, text) {
    const el = document.createElement('div');
    el.className = `message ${role}`;
    const content = document.createElement('div');
    content.className = 'msg-content';
    content.innerHTML = marked.parse(text);
    content.querySelectorAll('pre.mermaid-placeholder').forEach(async pre => {
        const code = pre.textContent;
        try {
            const { svg } = await mermaid.render('mermaid-' + Math.random().toString(36).slice(2), code);
            pre.outerHTML = `<div class="mermaid-rendered">${svg}</div>`;
        } catch {
            pre.classList.add('mermaid-error');
        }
    });
    el.appendChild(content);
    messages.appendChild(el);
    return el;
}

function appendThinkingBlock(parent) {
    const el = document.createElement('details');
    el.className = 'thinking';
    el.innerHTML = '<summary>thinking...</summary><div class="think-content"></div>';
    parent.insertBefore(el, parent.querySelector('.msg-content'));
    return el;
}

// ── Mermaid init: handle mermaid code blocks via marked's renderer ──
const renderer = new marked.Renderer();
const origCode = renderer.code.bind(renderer);
renderer.code = function({ text, lang }) {
    if (lang === 'mermaid') {
        return `<pre class="mermaid-placeholder">${text}</pre>`;
    }
    return origCode({ text, lang });
};
marked.setOptions({ renderer, breaks: true });

sendBtn.addEventListener('click', sendMessage);
input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); } });

// ── Theme Switcher ──
const themeKey = localStorage.getItem('picoagent-theme') || 'warm-charcoal';
document.documentElement.setAttribute('data-theme', themeKey);
document.querySelectorAll('#theme-switcher button').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.themeKey === themeKey);
    btn.addEventListener('click', () => {
        const key = btn.dataset.themeKey;
        document.documentElement.setAttribute('data-theme', key);
        localStorage.setItem('picoagent-theme', key);
        document.querySelectorAll('#theme-switcher button').forEach(b => b.classList.toggle('active', b === btn));
    });
});
