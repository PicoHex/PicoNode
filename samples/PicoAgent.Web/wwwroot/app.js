// ── State ──
let currentSession = 'default';
let currentModel = '';
let currentProvider = '';
let modelProviderMap = {}; // modelId → providerName

// ── DOM refs ──
const input    = document.getElementById('input');
const sendBtn  = document.getElementById('send');
const messages = document.getElementById('messages');
const statusEl = document.getElementById('status').querySelector('span');
const modelSel = document.getElementById('model-select');
const thinkChk = document.getElementById('thinking-checkbox');
const thinkLvl = document.getElementById('thinking-level');
const sessionList = document.getElementById('session-list');

// ── Stream state ──
let currentAssistant = null;
let isStreaming = false;
let streamMsgIndex = 0;       // index within current session for thinking cache key

// ── Thinking cache (persists across session switches in the same tab) ──
const THINKING_PREFIX = 'picoagent-thinking-';

function thinkingKey(sessionId, msgIdx) {
    return THINKING_PREFIX + sessionId + '-' + msgIdx;
}

function saveThinking(sessionId, msgIdx, text) {
    if (text) sessionStorage.setItem(thinkingKey(sessionId, msgIdx), text);
}

function loadThinking(sessionId, msgIdx) {
    return sessionStorage.getItem(thinkingKey(sessionId, msgIdx));
}

function forgetThinking(sessionId) {
    const prefix = THINKING_PREFIX + sessionId + '-';
    const keys = [];
    for (let i = 0; i < sessionStorage.length; i++) {
        const k = sessionStorage.key(i);
        if (k && k.startsWith(prefix)) keys.push(k);
    }
    keys.forEach(k => sessionStorage.removeItem(k));
}

// ── Init ──
(async function init() {
    // Safety net: redirect to config if no provider
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
        currentProvider = h.provider;
        updateStatus();
    } catch (e) { /* ignore */ }
}

function updateStatus() {
    statusEl.textContent = currentProvider
        ? `${currentModel} @ ${currentProvider}`
        : currentModel || '--';
}

// ── Combined model selector ──
async function loadModels() {
    try {
        const models = await api('GET', '/api/models');
        modelSel.innerHTML = '';
        modelProviderMap = {};

        // Group by provider
        const groups = {};
        for (const m of (models || [])) {
            const prov = m.ownedBy || 'Unknown';
            if (!groups[prov]) groups[prov] = [];
            groups[prov].push(m);
        }

        if (Object.keys(groups).length === 0) {
            modelSel.innerHTML = '<option value="">No models found</option>';
            return;
        }

        // Build optgroups
        for (const [prov, provModels] of Object.entries(groups)) {
            const og = document.createElement('optgroup');
            og.label = prov;
            for (const m of provModels) {
                modelProviderMap[m.id] = prov;
                const opt = document.createElement('option');
                opt.value = m.id;
                opt.textContent = m.id;
                if (m.id === currentModel) opt.selected = true;
                og.appendChild(opt);
            }
            modelSel.appendChild(og);
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
            let assistantIdx = 0;
            for (const m of msgs) {
                const role = (m.Role || m.role || '').toLowerCase();
                if (!role || role === 'system') continue;
                const text = m.Content || m.content
                    || (m.ContentBlocks && m.ContentBlocks[0]?.Text)
                    || (m.contentBlocks && m.contentBlocks[0]?.text) || '';

                if (role === 'assistant' && text) {
                    // Restore cached thinking if available
                    const cachedThinking = loadThinking(sessionId, assistantIdx);
                    renderMessageWithThinking('assistant', text, cachedThinking);
                    assistantIdx++;
                } else if (text) {
                    renderMessage('user', text);
                }
            }
        }
    } catch (e) { /* ignore */ }
}

function renderMessageWithThinking(role, text, thinkingText) {
    const el = document.createElement('div');
    el.className = `message ${role}`;

    // Thinking block (only if we have cached thinking)
    if (thinkingText) {
        const think = document.createElement('details');
        think.className = 'thinking';
        think.open = false;
        think.innerHTML = `<summary>thinking</summary><div class="think-content">${marked.parse(thinkingText)}</div>`;
        el.appendChild(think);
    }

    const content = document.createElement('div');
    content.className = 'msg-content';
    content.innerHTML = marked.parse(text);
    el.appendChild(content);
    messages.appendChild(el);
    deferMermaidRender(content);
    return el;
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
            forgetThinking(sessionId);  // invalidate stale thinking cache
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

// ── Model switch (auto-switches provider) ──
modelSel.addEventListener('change', async () => {
    const modelId = modelSel.value;
    if (!modelId) return;

    const provider = modelProviderMap[modelId];
    try {
        if (provider && provider !== currentProvider) {
            await api('POST', '/api/provider/switch', { provider });
            currentProvider = provider;
        }
        await api('POST', '/api/model/switch', { modelId });
        currentModel = modelId;
        updateStatus();
    } catch (e) { /* ignore */ }
});

// ── Thinking ──
async function switchThinking(enabled, level) {
    try {
        await api('POST', '/api/thinking', { enabled, level });
    } catch (e) { /* ignore */ }
}

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

    // Count current assistant messages for this session to assign an index
    const existing = messages.querySelectorAll('.message.assistant');
    streamMsgIndex = existing.length;

    renderMessage('user', text);
    currentAssistant = renderAssistantShell(thinkChk.checked);
    const contentEl = currentAssistant.querySelector('.msg-content');
    const streamDot = currentAssistant.querySelector('.streaming-indicator');
    const streamText = currentAssistant.querySelector('.stream-text');
    const thinkingBlock = currentAssistant.querySelector('.thinking');

    let rawText = '';
    let rawThinking = '';

    try {
        const response = await fetch(`/api/session/${currentSession}/message`, {
            method: 'POST',
            headers: { 'Content-Type': 'text/plain' },
            body: text,
        });
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

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
                    if (streamDot) streamDot.style.display = 'none';
                    streamText.style.display = 'none';
                    contentEl.innerHTML = marked.parse(rawText);
                    deferMermaidRender(contentEl);
                    if (rawThinking) {
                        const tc = thinkingBlock.querySelector('.think-content');
                        tc.innerHTML = marked.parse(rawThinking);
                        deferMermaidRender(tc);
                        saveThinking(currentSession, streamMsgIndex, rawThinking);
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
                                streamDot.style.display = 'none';
                                streamText.style.display = '';
                            }
                            streamText.textContent = rawText;
                            break;
                        case 'thinking':
                            if (!thinkChk.checked) break;  // skip when disabled
                            rawThinking += evt.content;
                            thinkingBlock.querySelector('.think-content').textContent = rawThinking;
                            thinkingBlock.open = true;
                            saveThinking(currentSession, streamMsgIndex, rawThinking);
                            break;
                        case 'done':
                            if (streamDot) streamDot.style.display = 'none';
                            streamText.style.display = 'none';
                            contentEl.innerHTML = marked.parse(rawText);
                            deferMermaidRender(contentEl);
                            if (rawThinking) {
                                const tc = thinkingBlock.querySelector('.think-content');
                                tc.innerHTML = marked.parse(rawThinking);
                                deferMermaidRender(tc);
                                saveThinking(currentSession, streamMsgIndex, rawThinking);
                            }
                            thinkingBlock.open = false;
                            currentAssistant = null;
                            break;
                        case 'tool_call_start':
                            if (streamDot) streamDot.style.display = 'none';
                            streamText.style.display = 'none';
                            rawText += `\n\n🔧 **${evt.toolName}**\n`;
                            contentEl.innerHTML = marked.parse(rawText);
                            break;
                        case 'tool_call_delta':
                            rawText += evt.content;
                            streamText.textContent = rawText;
                            break;
                        case 'tool_call_end':
                            rawText += '\n';
                            streamText.textContent = rawText;
                            break;
                        case 'error':
                            if (streamDot) streamDot.style.display = 'none';
                            streamText.style.display = 'none';
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

function renderAssistantShell(showThinking = true) {
    const el = document.createElement('div');
    el.className = 'message assistant';
    const content = document.createElement('div');
    content.className = 'msg-content';
    content.innerHTML =
      '<span class="streaming-indicator"><span>●</span><span>●</span><span>●</span></span>' +
      '<span class="stream-text" style="display:none"></span>';
    el.appendChild(content);

    if (showThinking) {
        const think = document.createElement('details');
        think.className = 'thinking';
        think.open = true;
        think.innerHTML = '<summary>thinking...</summary><div class="think-content"></div>';
        el.insertBefore(think, content);
    }

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
    const mmTheme = key === 'ivory-paper' ? 'neutral' : 'dark';
    mermaid.initialize({ startOnLoad: false, theme: mmTheme });
}
const themeKey = localStorage.getItem('picoagent-theme') || 'warm-charcoal';
applyTheme(themeKey);
document.querySelectorAll('#theme-switcher button').forEach(btn => {
    btn.addEventListener('click', () => applyTheme(btn.dataset.themeKey));
});
