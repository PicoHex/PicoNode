// ── State ──
let currentSession = 'default';
let currentModel = '';
let providers = [];

// ── DOM refs ──
const input    = document.getElementById('input');
const sendBtn  = document.getElementById('send');
const messages = document.getElementById('messages');
const statusEl = document.getElementById('status').querySelector('span');
const modelSel = document.getElementById('model-select');
const provSel  = document.getElementById('provider-select');
const thinkChk = document.getElementById('thinking-checkbox');
const thinkLvl = document.getElementById('thinking-level');
const sessionList = document.getElementById('session-list');

// ── Stream state ──
let currentAssistant = null;
let isStreaming = false;

// ── Init ──
(async function init() {
    // Check if configuration exists
    try {
        const r = await fetch('/api/config/status');
        const s = await r.json();
        if (!s.configured) {
            window.location.href = '/config.html';
            return;
        }
    } catch { /* if config endpoint unavailable, continue */ }
    await loadHealth();
    await loadModels();
    await loadSessions();
    await loadMessages(currentSession);
    switchSession(currentSession);
})();

// ── API helpers ──
async function api(method, url, body) {
    const opts = { method };
    if (body !== undefined) {
        opts.headers = { 'Content-Type': body instanceof FormData ? undefined : 'application/json' };
        opts.body = body instanceof FormData ? body : JSON.stringify(body);
    }
    const r = await fetch(url, opts);
    if (!r.ok) throw new Error(`${r.status}`);
    return r.headers.get('content-type')?.includes('json') !== false ? r.json() : r.text();
}

async function loadHealth() {
    try {
        const h = await api('GET', '/api/health');
        currentModel = h.model;
        statusEl.textContent = `${h.model} @ ${h.provider}`;
    } catch (e) { /* ignore */ }
}

async function loadModels() {
    try {
        const models = await api('GET', '/api/models');
        modelSel.innerHTML = '';
        provSel.innerHTML = '';
        const seenIds = new Set();
        const seenProvs = new Set();
        for (const m of (models || [])) {
            if (!seenIds.has(m.id)) {
                seenIds.add(m.id);
                const opt = document.createElement('option');
                opt.value = m.id;
                opt.textContent = m.id;
                if (m.id === currentModel) opt.selected = true;
                modelSel.appendChild(opt);
            }
            if (m.ownedBy && !seenProvs.has(m.ownedBy)) {
                seenProvs.add(m.ownedBy);
                const opt = document.createElement('option');
                opt.value = m.ownedBy;
                opt.textContent = m.ownedBy;
                provSel.appendChild(opt);
            }
        }
        if (modelSel.options.length === 0) {
            modelSel.innerHTML = '<option value="">No models found</option>';
        }
        if (provSel.options.length === 0) {
            provSel.innerHTML = '<option value="">No providers</option>';
        }
    } catch (e) {
        modelSel.innerHTML = '<option value="">Failed to load</option>';
    }
}

async function loadSessions() {
    try {
        const sessions = await api('GET', '/api/sessions');
        sessionList.innerHTML = '';
        for (const s of (sessions || [currentSession])) {
            const div = document.createElement('div');
            div.className = 'session' + (s === currentSession ? ' active' : '');
            div.dataset.id = s;
            div.textContent = s;
            const compactBtn = document.createElement('button');
            compactBtn.className = 'compact-btn';
            compactBtn.textContent = '🗜️';
            compactBtn.title = 'Compress history';
            compactBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                compactSession(s, compactBtn);
            });
            div.appendChild(compactBtn);
            div.addEventListener('click', () => switchSession(s));
            sessionList.appendChild(div);
        }
    } catch (e) { /* ignore */ }
}

async function loadMessages(sessionId) {
    try {
        const msgs = await api('GET', `/api/session/${sessionId}/messages`);
        messages.innerHTML = '';
        if (msgs && Array.isArray(msgs)) {
            for (const m of msgs) {
                const role = (m.Role || m.role || '').toLowerCase();
                if (!role || role === 'system') continue;
                const displayRole = role === 'user' ? 'user' : 'assistant';
                const text = m.Content || m.content
                    || (m.ContentBlocks && m.ContentBlocks[0]?.Text)
                    || (m.contentBlocks && m.contentBlocks[0]?.text) || '';
                if (text) renderMessage(displayRole, text);
            }
        }
    } catch (e) { /* ignore */ }
}

async function switchSession(id) {
    currentSession = id;
    document.querySelectorAll('#session-list .session').forEach(el => {
        el.classList.toggle('active', el.dataset.id === id);
    });
    await loadMessages(id);
    messages.scrollTop = messages.scrollHeight;
}

async function createSession() {
    const name = prompt('Session name:')?.trim();
    if (!name) return;
    try {
        await api('POST', `/api/session/create/${name}`);
        await loadSessions();
        switchSession(name);
    } catch (e) { alert('Failed: ' + e.message); }
}

async function saveSession() {
    const btn = document.getElementById('btn-save-session');
    btn.textContent = '...';
    try {
        await api('POST', `/api/session/save/${currentSession}`);
        btn.textContent = '✓';
        setTimeout(() => { btn.textContent = '💾'; }, 1500);
    } catch (e) {
        btn.textContent = '✗';
        setTimeout(() => { btn.textContent = '💾'; }, 1500);
    }
}

async function compactSession(sessionId, btn) {
    const originalText = btn.textContent;
    btn.textContent = '...';
    btn.disabled = true;
    try {
        const result = await api('POST', `/api/session/${sessionId}/compact`, { keepRecent: 20 });
        if (result.compressedCount > 0) {
            showToast(`Compressed ${result.compressedCount} messages, saved ${result.tokensSaved} tokens`);
        } else {
            showToast('Nothing to compress (messages <= 20)');
        }
        btn.textContent = '✓';
        setTimeout(() => { btn.textContent = originalText; btn.disabled = false; }, 1500);
        if (sessionId === currentSession) {
            await loadMessages(sessionId);
        }
    } catch (e) {
        btn.textContent = '✗';
        showToast(`Compression failed: ${e.message}`);
        setTimeout(() => { btn.textContent = originalText; btn.disabled = false; }, 1500);
    }
}

function showToast(msg) {
    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = msg;
    document.body.appendChild(toast);
    requestAnimationFrame(() => toast.classList.add('show'));
    setTimeout(() => {
        toast.classList.remove('show');
        setTimeout(() => toast.remove(), 300);
    }, 3000);
}

async function switchModel(modelId) {
    try {
        await api('POST', '/api/model/switch', { modelId });
        currentModel = modelId;
        statusEl.textContent = `${modelId}`;
    } catch (e) { /* ignore */ }
}

async function switchProvider(provider) {
    try {
        await api('POST', '/api/provider/switch', { provider });
        await loadHealth();
        await loadModels();
    } catch (e) { /* ignore */ }
}

async function switchThinking(enabled, level) {
    try {
        await api('POST', '/api/thinking', { enabled, level });
    } catch (e) { /* ignore */ }
}

// ── Event bindings ──
modelSel.addEventListener('change', () => switchModel(modelSel.value));
provSel.addEventListener('change', () => switchProvider(provSel.value));
thinkChk.addEventListener('change', () => switchThinking(thinkChk.checked, thinkLvl.value));
thinkLvl.addEventListener('change', () => switchThinking(thinkChk.checked, thinkLvl.value));
document.getElementById('btn-new-session').addEventListener('click', createSession);
document.getElementById('btn-save-session').addEventListener('click', saveSession);

// ── Send message ──
sendBtn.addEventListener('click', sendMessage);
input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); }
});

async function sendMessage() {
    const text = input.value.trim();
    if (!text || isStreaming) return;
    input.value = '';
    isStreaming = true;
    sendBtn.disabled = true;

    renderMessage('user', text);
    currentAssistant = renderAssistantShell();
    const contentEl = currentAssistant.querySelector('.msg-content');
    const streamDot = currentAssistant.querySelector('.streaming-indicator');
    const thinkingBlock = currentAssistant.querySelector('.thinking');

    try {
        const response = await fetch(`/api/session/${currentSession}/message`, {
            method: 'POST',
            headers: { 'Content-Type': 'text/plain' },
            body: text,
        });
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        let rawText = '';
        let rawThinking = '';

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });

            const lines = buffer.split('\n');
            buffer = lines.pop() || '';
            for (const line of lines) {
                if (!line.startsWith('data: ')) continue;
                const payload = line.slice(6);
                if (payload === '[DONE]') {
                    if (streamDot) streamDot.remove();
                    contentEl.innerHTML = marked.parse(rawText);
                    deferMermaidRender(contentEl);
                    if (rawThinking) {
                        const tc = thinkingBlock.querySelector('.think-content');
                        tc.innerHTML = marked.parse(rawThinking);
                        deferMermaidRender(tc);
                    }
                    currentAssistant = null;
                    break;
                }
                try {
                    const evt = JSON.parse(payload);
                    switch (evt.type) {
                        case 'delta':
                            rawText += evt.content;
                            if (rawText.trim() && streamDot) {
                                streamDot.remove();
                                streamDot = null;
                            }
                            if (!streamDot) {
                                contentEl.innerText = rawText;
                            }
                            break;
                        case 'thinking':
                            rawThinking += evt.content;
                            thinkingBlock.querySelector('.think-content').textContent = rawThinking;
                            thinkingBlock.open = true;
                            break;
                        case 'done':
                            if (streamDot) streamDot.remove();
                            contentEl.innerHTML = marked.parse(rawText);
                            deferMermaidRender(contentEl);
                            if (rawThinking) {
                                const tc = thinkingBlock.querySelector('.think-content');
                                tc.innerHTML = marked.parse(rawThinking);
                                deferMermaidRender(tc);
                            }
                            thinkingBlock.open = false;
                            currentAssistant = null;
                            break;
                        case 'tool_call_start':
                            if (streamDot) { streamDot.remove(); streamDot = null; }
                            rawText += `\n\n🔧 **${evt.toolName}**\n`;
                            contentEl.innerHTML = marked.parse(rawText);
                            break;
                        case 'tool_call_delta':
                            rawText += evt.content;
                            if (!streamDot) contentEl.innerText = rawText;
                            break;
                        case 'tool_call_end':
                            rawText += '\n';
                            if (!streamDot) contentEl.innerText = rawText;
                            break;
                        case 'error':
                            if (streamDot) { streamDot.remove(); streamDot = null; }
                            rawText += `\n\n> ⚠️ ${evt.message}`;
                            contentEl.innerHTML = marked.parse(rawText);
                            break;
                    }
                } catch { /* malformed JSON, skip */ }
                messages.scrollTop = messages.scrollHeight;
            }
        }
    } catch (err) {
        contentEl.innerText = `[Error: ${err.message}]`;
    } finally {
        isStreaming = false;
        sendBtn.disabled = false;
        input.focus();
        // Auto-save after each message so conversation survives restart
        try { await api('POST', `/api/session/save/${currentSession}`); } catch {}
        await loadSessions();
    }
}

// ── Rendering ──
function renderMessage(role, text) {
    const el = document.createElement('div');
    el.className = `message ${role}`;
    const content = document.createElement('div');
    content.className = 'msg-content';
    content.innerHTML = marked.parse(text);
    el.appendChild(content);
    messages.appendChild(el);
    // Defer Mermaid rendering to avoid blocking
    deferMermaidRender(content);
    return el;
}

async function deferMermaidRender(container) {
    const placeholders = container.querySelectorAll('pre.mermaid-placeholder');
    for (const pre of placeholders) {
        try {
            const id = 'mermaid-' + Math.random().toString(36).slice(2);
            const { svg } = await mermaid.render(id, pre.textContent);
            pre.outerHTML = `<div class="mermaid-rendered">${svg}</div>`;
        } catch { pre.classList.add('mermaid-error'); }
    }
}

function renderAssistantShell() {
    const el = document.createElement('div');
    el.className = 'message assistant';
    const content = document.createElement('div');
    content.className = 'msg-content';
    content.innerHTML = '<span class="streaming-indicator"><span>●</span><span>●</span><span>●</span></span>';
    el.appendChild(content);

    // Thinking block (collapsible)
    const think = document.createElement('details');
    think.className = 'thinking';
    think.open = true;  // start expanded so user sees activity
    think.innerHTML = '<summary>thinking...</summary><div class="think-content"></div>';
    el.insertBefore(think, content);

    messages.appendChild(el);
    messages.scrollTop = messages.scrollHeight;
    return el;
}

// ── Marked + Mermaid ──
const renderer = new marked.Renderer();
const origCode = renderer.code.bind(renderer);
renderer.code = function({ text, lang }) {
    if (lang === 'mermaid') return `<pre class="mermaid-placeholder">${text}</pre>`;
    return origCode({ text, lang });
};
marked.setOptions({ renderer, breaks: true });

// ── Theme ──
function applyTheme(key) {
    document.documentElement.setAttribute('data-theme', key);
    localStorage.setItem('picoagent-theme', key);
    document.querySelectorAll('#theme-switcher button').forEach(b => b.classList.toggle('active', b.dataset.themeKey === key));
    // Match Mermaid theme: dark for warm themes, neutral for light
    const mmTheme = key === 'ivory-paper' ? 'neutral' : 'dark';
    mermaid.initialize({ startOnLoad: false, theme: mmTheme });
}
const themeKey = localStorage.getItem('picoagent-theme') || 'warm-charcoal';
applyTheme(themeKey);
document.querySelectorAll('#theme-switcher button').forEach(btn => {
    btn.addEventListener('click', () => applyTheme(btn.dataset.themeKey));
});
