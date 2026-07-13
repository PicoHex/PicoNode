// ── State ──
let currentSession = 'default';
let currentModel = '';
let currentProvider = '';
let modelProviderMap = {};
let isStreaming = false;
let streamMsgIndex = 0;
let abortCtrl = null;
let inputHistory = [];
let historyIdx = -1;

// ── DOM ──
const input       = document.getElementById('input');
const sendBtn     = document.getElementById('send');
const stopBtn     = document.getElementById('stop');
const messages    = document.getElementById('messages');
const statusEl    = document.getElementById('status').querySelector('span');
const modelSel    = document.getElementById('model-select');
const thinkChk    = document.getElementById('thinking-checkbox');
const thinkLvl    = document.getElementById('thinking-level');
const sessionList = document.getElementById('session-list');
const sysPrompt   = document.getElementById('system-prompt');
const historyHint = document.getElementById('history-hint');

// ── Thinking cache ──
const THINKING_PREFIX = 'picoagent-thinking-';
function thinkingKey(sid, idx) { return THINKING_PREFIX + sid + '-' + idx; }
function saveThinking(sid, idx, text) { if (text) sessionStorage.setItem(thinkingKey(sid, idx), text); }
function loadThinking(sid, idx) { return sessionStorage.getItem(thinkingKey(sid, idx)); }
function forgetThinking(sid) {
    for (let i = sessionStorage.length - 1; i >= 0; i--) {
        const k = sessionStorage.key(i); if (k && k.startsWith(THINKING_PREFIX + sid + '-')) sessionStorage.removeItem(k);
    }
}

// ── Init ──
(async function init() {
    try { const r = await fetch('/api/config/status'); const s = await r.json(); if (s.configured) { thinkChk.checked = s.thinkingEnabled !== false; thinkLvl.value = s.thinkingLevel || 'xhigh'; switchThinking(thinkChk.checked, thinkLvl.value); } else { window.location.href = '/config.html'; return; } } catch {}
    await loadHealth();
    await loadModels();
    await loadSessions();
    await loadMessages(currentSession);
    await loadSystemPrompt();
    switchSession(currentSession);
    document.getElementById('sidebar-toggle').addEventListener('click', () => document.getElementById('sidebar').classList.toggle('open'));
    document.getElementById('chat').addEventListener('click', () => document.getElementById('sidebar').classList.remove('open'));
})();

// ── API ──
async function api(method, url, body) {
    const opts = { method }; if (body !== undefined) { opts.headers = { 'Content-Type': 'application/json' }; opts.body = JSON.stringify(body); }
    const r = await fetch(url, opts); if (!r.ok) throw new Error(`${r.status}`);
    return r.headers.get('content-type')?.includes('json') ? r.json() : r.text();
}
async function loadHealth() { try { const h = await api('GET', '/api/health'); currentModel = h.model; currentProvider = h.provider; updateStatus(); } catch (e) { console.warn('loadHealth failed:', e); } }
function updateStatus() { statusEl.textContent = currentProvider ? `${currentModel} @ ${currentProvider}` : currentModel || '--'; }

// ── Models ──
async function loadModels() {
    try {
        const models = await api('GET', '/api/models'); modelSel.innerHTML = ''; modelProviderMap = {};
        const groups = {}; for (const m of (models || [])) { const p = m.ownedBy || 'Unknown'; (groups[p] ??= []).push(m); }
        if (!Object.keys(groups).length) { modelSel.innerHTML = '<option value="">No models</option>'; return; }
        for (const [prov, pms] of Object.entries(groups)) {
            const og = document.createElement('optgroup'); og.label = prov;
            for (const m of pms) { modelProviderMap[m.id] = prov; const o = document.createElement('option'); o.value = m.id; o.textContent = m.id; if (m.id === currentModel) o.selected = true; og.appendChild(o); }
            modelSel.appendChild(og);
        }
    } catch { modelSel.innerHTML = '<option value="">Failed</option>'; }
}

// ── Sessions ──
async function loadSessions() {
    try {
        const sessions = await api('GET', '/api/sessions'); sessionList.innerHTML = '';
        for (const s of (sessions || [currentSession])) {
            const div = document.createElement('div'); div.className = 'session' + (s === currentSession ? ' active' : ''); div.dataset.id = s;
            const span = document.createElement('span'); span.textContent = s; div.appendChild(span);
            const cBtn = document.createElement('button'); cBtn.className = 'compact-btn'; cBtn.textContent = '🗜️'; cBtn.title = 'Compress';
            cBtn.addEventListener('click', e => { e.stopPropagation(); compactSession(s, cBtn); }); div.appendChild(cBtn);
            const dBtn = document.createElement('button'); dBtn.className = 'session-delete'; dBtn.textContent = '×'; dBtn.title = 'Delete';
            dBtn.addEventListener('click', e => { e.stopPropagation(); deleteSession(s, div); }); div.appendChild(dBtn);
            div.addEventListener('click', () => switchSession(s)); sessionList.appendChild(div);
        }
    } catch {}
}
async function switchSession(id) { currentSession = id; document.querySelectorAll('#session-list .session').forEach(el => el.classList.toggle('active', el.dataset.id === id)); await loadMessages(id); messages.scrollTop = messages.scrollHeight; }
async function createSession() { const n = prompt('Session name:')?.trim(); if (!n) return; try { await api('POST', `/api/session/create/${n}`); await loadSessions(); switchSession(n); } catch (e) { showToast('Failed: ' + e.message, true); } }
async function deleteSession(id, el) { if (id === 'default') { showToast('Cannot delete default', true); return; } if (!confirm(`Delete "${id}"?`)) return; try { await api('POST', `/api/session/delete/${id}`); forgetThinking(id); if (currentSession === id) { currentSession = 'default'; await switchSession('default'); } await loadSessions(); showToast('Deleted'); } catch (e) { showToast('Delete failed: ' + e.message, true); } }
async function saveSession() { const b = document.getElementById('btn-save-session'); b.textContent = '...'; try { await api('POST', `/api/session/save/${currentSession}`); b.textContent = '✓'; } catch { b.textContent = '✗'; } setTimeout(() => { b.textContent = '💾'; }, 1500); }
async function compactSession(id, btn) { const o = btn.textContent; btn.textContent = '...'; btn.disabled = true; try { const r = await api('POST', `/api/session/${id}/compact`, { keepRecent: 20 }); showToast(r.compressedCount > 0 ? `Compressed ${r.compressedCount} msgs, saved ${r.tokensSaved} tokens` : 'Nothing to compress'); forgetThinking(id); btn.textContent = '✓'; if (id === currentSession) await loadMessages(id); } catch (e) { showToast('Compression failed: ' + e.message, true); btn.textContent = '✗'; } setTimeout(() => { btn.textContent = o; btn.disabled = false; }, 1500); }

// ── System prompt ──
async function loadSystemPrompt() { try { const r = await api('GET', '/api/system-prompt'); sysPrompt.value = r.prompt || ''; } catch {} }
document.getElementById('btn-save-prompt').addEventListener('click', async () => {
    const p = sysPrompt.value; try { await api('POST', '/api/system-prompt', { prompt: p }); showToast('Prompt saved'); } catch (e) { showToast('Save failed', true); }
});

// ── Messages ──
async function loadMessages(sessionId) {
    try {
        const msgs = await api('GET', `/api/session/${sessionId}/messages`); messages.innerHTML = '';
        if (msgs && Array.isArray(msgs)) {
            let ai = 0;
            for (const m of msgs) {
                const role = (m.Role || m.role || '').toLowerCase();
                if (!role || role === 'system' || role === 'toolresult' || role === 'compactionsummary' || role === 'branchsummary') continue;
                const blocks = m.ContentBlocks || m.contentBlocks || [];
                const textBlocks = blocks.filter(cb => (cb.Type || cb.type) === 'text');
                const toolBlocks = blocks.filter(cb => (cb.Type || cb.type) === 'tool_call');
                let text = textBlocks.map(cb => cb.Text || cb.text || '').join('');
                if (!text) text = m.Content || m.content || '';
                if (!text && !toolBlocks.length) continue;
                if (role === 'assistant') {
                    const tokenInfo = (m.Usage) ? `↑${m.Usage.InputTokens ?? 0} ↓${m.Usage.OutputTokens ?? 0}` : '';
                    renderMessage('assistant', text, tokenInfo, sessionId + '-' + ai);
                    for (const tc of toolBlocks) {
                        // Find matching toolResult message
                        const tcId = tc.Id || tc.id || '';
                        let resultText = '';
                        let isError = false;
                        for (const rm of msgs) {
                            const rRole = (rm.Role || rm.role || '').toLowerCase();
                            if (rRole === 'toolresult' && (rm.ToolCallId || rm.toolCallId) === tcId) {
                                const rBlocks = rm.ContentBlocks || rm.contentBlocks || [];
                                resultText = rBlocks.filter(cb => (cb.Type || cb.type) === 'text').map(cb => cb.Text || cb.text || '').join('');
                                isError = rm.IsError || rm.isError || false;
                                break;
                            }
                        }
                        addHistoryToolBlock(messages.lastElementChild, tc, resultText, isError);
                    }
                    const ct = loadThinking(sessionId, ai); if (ct) { addThinkingBlock(messages.lastElementChild, ct); }
                    ai++;
                } else if (text) { renderMessage('user', text, '', null); }
            }
        }
    } catch {}
}
function addHistoryToolBlock(el, tc, resultText, isError) {
    const tcDiv = document.createElement('details');
    tcDiv.className = 'tool-call';
    if (isError) tcDiv.classList.add('tool-error');
    tcDiv.open = isError;
    const name = tc.Name || tc.name || 'tool';
    const args = tc.Arguments || tc.arguments || {};
    // Normalize args keys to lowercase for case-insensitive matching
    const normalizedArgs = {};
    for (const k of Object.keys(args)) normalizedArgs[k.toLowerCase()] = args[k];
    const argsStr = Object.keys(normalizedArgs).length ? JSON.stringify(normalizedArgs) : '';
    // Skill detection: read + path ends with SKILL.md
    const skillPath = normalizedArgs.path || '';
    const isSkill = name === 'read' && typeof skillPath === 'string' && (skillPath.endsWith('SKILL.md') || skillPath.includes('SKILL.md'));
    if (isSkill) tcDiv.classList.add('skill-read');
    const icon = isSkill ? '📚' : '🔧';
    const summary = document.createElement('summary');
    summary.innerHTML = icon + ' <strong>' + name + '</strong> <span class="tool-args">' + argsStr + '</span>';
    tcDiv.appendChild(summary);
    const resultDiv = document.createElement('div');
    resultDiv.className = 'tool-result';
    resultDiv.textContent = resultText || '(no result)';
    tcDiv.appendChild(resultDiv);
    const msgContent = el.querySelector('.msg-content');
    if (msgContent) msgContent.appendChild(tcDiv);
}
function renderMessage(role, text, extra = '', msgId = null) {
    const el = document.createElement('div'); el.className = `message ${role}`; if (msgId) el.dataset.msgId = msgId;
    if (extra) { const tu = document.createElement('div'); tu.className = 'token-usage'; tu.textContent = extra; el.appendChild(tu); }
    const content = document.createElement('div'); content.className = 'msg-content'; content.innerHTML = marked.parse(text); el.appendChild(content);
    deferMermaidRender(content);
    const bar = document.createElement('div'); bar.className = 'msg-actions';
    bar.innerHTML = '<button title="Copy">📋</button>';
    bar.querySelector('button').addEventListener('click', () => copyMessage(content));
    if (role === 'user') {
        const eb = document.createElement('button'); eb.textContent = '✏️'; eb.title = 'Edit';
        eb.addEventListener('click', () => showEditOverlay(text)); bar.appendChild(eb);
    }
    if (role === 'assistant') {
        const rb = document.createElement('button'); rb.textContent = '🔄'; rb.title = 'Retry';
        rb.addEventListener('click', () => retryLastMessage()); bar.appendChild(rb);
    }
    el.appendChild(bar); messages.appendChild(el); return el;
}
function addThinkingBlock(el, thinkingText) {
    const think = document.createElement('details'); think.className = 'thinking'; think.open = false;
    think.innerHTML = `<summary>thinking</summary><div class="think-content">${marked.parse(thinkingText)}</div>`;
    el.insertBefore(think, el.querySelector('.msg-content')); deferMermaidRender(think);
}
function copyMessage(contentEl) { navigator.clipboard.writeText(contentEl.innerText).then(() => showToast('Copied')).catch(() => showToast('Copy failed', true)); }

// ── Edit overlay ──
let editOverlay = null;
function showEditOverlay(text) {
    if (!editOverlay) {
        editOverlay = document.createElement('div'); editOverlay.className = 'edit-overlay';
        editOverlay.innerHTML = '<textarea rows="3"></textarea><button>Send</button><button style="background:var(--border)">✕</button>';
        editOverlay.querySelector('button').addEventListener('click', () => { const t = editOverlay.querySelector('textarea').value.trim(); if (t) { editOverlay.classList.remove('active'); sendMessage(t); } });
        editOverlay.querySelectorAll('button')[1].addEventListener('click', () => { editOverlay.querySelector('textarea').value = ''; editOverlay.classList.remove('active'); });
        document.getElementById('chat').appendChild(editOverlay);
    }
    editOverlay.querySelector('textarea').value = text; editOverlay.classList.add('active'); editOverlay.querySelector('textarea').focus();
}

// ── Retry ──
async function retryLastMessage() {
    const userMsgs = messages.querySelectorAll('.message.user'); if (!userMsgs.length) return;
    const last = userMsgs[userMsgs.length - 1]; const text = last.querySelector('.msg-content').textContent.trim();
    forgetThinking(currentSession);
    try { await fetch(`/api/session/${currentSession}/retry`, { method: 'POST' }); } catch {}
    let next = last; while (next) { const r = next; next = next.nextElementSibling; r.remove(); }
    await sendMessage(text);
}

// ── Toast ──
function showToast(msg, isError) { const t = document.createElement('div'); t.className = 'toast'; t.textContent = msg; if (isError) t.style.background = '#c0392b'; document.body.appendChild(t); requestAnimationFrame(() => t.classList.add('show')); setTimeout(() => { t.classList.remove('show'); setTimeout(() => t.remove(), 300); }, 3000); }

// ── Model switch ──
modelSel.addEventListener('change', async () => { const id = modelSel.value; if (!id) return; const p = modelProviderMap[id]; try { if (p && p !== currentProvider) { await api('POST', '/api/provider/switch', { provider: p }); currentProvider = p; } await api('POST', '/api/model/switch', { modelId: id }); currentModel = id; updateStatus(); } catch (e) { showToast('Switch failed: ' + e.message, true); } });

// ── Thinking ──
async function switchThinking(en, lv) { try { await api('POST', '/api/thinking', { enabled: en, level: lv }); } catch (e) { showToast('Thinking switch failed', true); } }
thinkChk.addEventListener('change', () => switchThinking(thinkChk.checked, thinkLvl.value));
thinkLvl.addEventListener('change', () => switchThinking(thinkChk.checked, thinkLvl.value));
document.getElementById('btn-new-session').addEventListener('click', createSession);
document.getElementById('btn-save-session').addEventListener('click', saveSession);

document.getElementById('btn-reload').addEventListener('click', async () => {
    const b = document.getElementById('btn-reload');
    b.textContent = '⏳';
    try { await api('POST', '/api/reload'); b.textContent = '✅'; showToast('Skills & config reloaded'); }
    catch (e) { b.textContent = '❌'; showToast('Reload failed', true); }
    setTimeout(() => { b.textContent = '🔄'; }, 1500);
});

// ── Export ──
document.getElementById('btn-export').addEventListener('click', async () => {
    const all = messages.querySelectorAll('.message');
    let md = '# PicoAgent - ' + currentSession + '\n\n';
    for (const m of all) {
        const role = m.classList.contains('user') ? '## User' : '## Assistant';
        const text = m.querySelector('.msg-content')?.innerText || '';
        const usage = m.querySelector('.token-usage')?.textContent || '';
        md += `${role}\n\n${text}\n\n${usage ? '_' + usage + '_\n\n' : ''}`;
    }
    const blob = new Blob([md], { type: 'text/markdown' });
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `${currentSession}.md`; a.click(); showToast('Exported');
});

// ── Send / Stop ──
sendBtn.addEventListener('click', () => sendMessage());
stopBtn.addEventListener('click', stopGeneration);
input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); return; }
    if (e.key === 'Escape') { if (editOverlay?.classList.contains('active')) { editOverlay.classList.remove('active'); editOverlay.querySelector('textarea').value = ''; } else { input.blur(); } return; }
    if (e.key === 'ArrowUp' && !input.value && inputHistory.length) {
        e.preventDefault(); historyIdx = historyIdx < 0 ? inputHistory.length - 1 : Math.max(0, historyIdx - 1);
        input.value = inputHistory[historyIdx]; showHistoryHint();
    }
    if (e.key === 'ArrowDown' && historyIdx >= 0) {
        e.preventDefault(); historyIdx++; if (historyIdx >= inputHistory.length) { historyIdx = -1; input.value = ''; } else { input.value = inputHistory[historyIdx]; }
        showHistoryHint();
    }
});
function showHistoryHint() { if (historyIdx >= 0) { historyHint.textContent = `History ${historyIdx + 1}/${inputHistory.length}`; historyHint.classList.add('show'); setTimeout(() => historyHint.classList.remove('show'), 1500); } }
function stopGeneration() { if (abortCtrl) { abortCtrl.abort(); abortCtrl = null; } setStreaming(false); }
function setStreaming(on) { isStreaming = on; sendBtn.style.display = on ? 'none' : ''; stopBtn.style.display = on ? '' : 'none'; sendBtn.disabled = on; }

// ── Streaming helpers ──
function createTextSeg() { const s = document.createElement('div'); s.className = 'stream-text-seg'; return s; }
function finalizeTextSeg(seg) { seg.classList.add('finalized'); }
function renderAllSegments(container) {
    for (const seg of container.querySelectorAll('.stream-text-seg:not(.finalized)')) { seg.classList.add('finalized'); }
    for (const seg of container.querySelectorAll('.stream-text-seg:not(.rendered)')) {
        seg.classList.add('rendered');
        seg.innerHTML = marked.parse(seg.textContent);
        deferMermaidRender(seg);
    }
}
function cleanupToolBlocks(container) {
    for (const tc of container.querySelectorAll('.tool-call')) {
        const resultDiv = tc.querySelector('.tool-result');
        if (resultDiv && (resultDiv.textContent || '').trim() && resultDiv.textContent.startsWith('[no result]')) continue; // already handled
        if (resultDiv && !(resultDiv.textContent || '').trim()) {
            const nameEl = tc.querySelector('summary strong');
            const name = (nameEl && nameEl.textContent && nameEl.textContent !== 'tool')
                ? nameEl.textContent
                : (tc.dataset.toolName || tc.dataset.toolId || '?');
            const argsSpan = tc.querySelector('.tool-args');
            const argsText = (argsSpan && argsSpan.textContent && argsSpan.textContent !== 'running...') ? ' ' + argsSpan.textContent : '';
            resultDiv.textContent = '[no result] ' + name + argsText;
            tc.classList.add('tool-error');
            tc.open = true;
        }
    }
}

async function sendMessage(overrideText) {
    const text = (overrideText || input.value).trim(); if (!text || isStreaming) return;
    if (!overrideText) { inputHistory.push(text); input.value = ''; historyIdx = -1; }
    setStreaming(true);
    const existing = messages.querySelectorAll('.message.assistant'); streamMsgIndex = existing.length;
    abortCtrl?.abort(); abortCtrl = new AbortController();
    renderMessage('user', text);
    const asst = renderMessage('assistant', '', '', currentSession + '-' + streamMsgIndex);
    const msgContent = asst.querySelector('.msg-content');
    const turnContainers = {}; // turnId -> container element (future multi-agent)
    msgContent.innerHTML = '<span class="streaming-indicator"><span>●</span><span>●</span><span>●</span></span>';
    const streamDot = msgContent.querySelector('.streaming-indicator');
    let thinkBlock = null;
    let rawText = '', rawThinking = '';
    let segStart = 0; // rawText offset where current text segment starts
    let thinkingPhase = 0;
    let toolBlocks = {};
    let currentTextSeg = null;
    let currentTurnId = null;
    let llmRound = 1; // LLM round counter, used to create unique tool call keys across rounds

    try {
        const response = await fetch(`/api/session/${currentSession}/message`, { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: text, signal: abortCtrl.signal });
        const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = '';
        while (true) { const { done, value } = await reader.read(); if (done) break; buffer += decoder.decode(value, { stream: true }); const lines = buffer.split('\n'); buffer = lines.pop() || '';
            for (const line of lines) {
                if (line.startsWith('event: ')) { currentTurnId = line.slice(7).trim(); continue; }
                if (!line.startsWith('data: ')) continue;
                const p = line.slice(6);
                try { const evt = JSON.parse(p); try { evt._turnId = currentTurnId;
                    if (evt.type === 'delta') {
                        rawText += evt.content;
                        if (!currentTextSeg) { streamDot.style.display = 'none'; currentTextSeg = createTextSeg(); msgContent.appendChild(currentTextSeg); }
                        currentTextSeg.textContent = rawText.substring(segStart);
                        currentTextSeg.classList.remove('rendered');
                    }
                    else if (evt.type === 'thinking') { if (!thinkChk.checked) continue; if (!thinkBlock) { thinkBlock = document.createElement('details'); thinkBlock.className = 'thinking'; thinkBlock.open = true; thinkBlock.innerHTML = '<summary>thinking...</summary><div class="think-content"></div>'; msgContent.appendChild(thinkBlock); } rawThinking += evt.content; thinkBlock.querySelector('.think-content').textContent = rawThinking; saveThinking(currentSession, streamMsgIndex, rawThinking); }
                    else if (evt.type === 'done') { streamDot.style.display = 'none'; if (currentTextSeg) finalizeTextSeg(currentTextSeg); setTimeout(() => renderAllSegments(msgContent), 0); if (rawThinking && thinkBlock) { thinkBlock.querySelector('.think-content').innerHTML = marked.parse(rawThinking); saveThinking(currentSession, streamMsgIndex, rawThinking); } if (thinkBlock) { thinkBlock.remove(); thinkBlock = null; } llmRound++; }
                    else if (evt.type === 'tool_call_start') {
                        if (currentTextSeg) { finalizeTextSeg(currentTextSeg); currentTextSeg = null; } streamDot.style.display = 'none';
                        segStart = rawText.length;
                        // New LLM turn — flush thinking from previous turn
                        if (thinkBlock && rawThinking) { thinkBlock.querySelector('.think-content').innerHTML = marked.parse(rawThinking); saveThinking(currentSession, streamMsgIndex, rawThinking); thinkBlock.open = false; }
                        thinkingPhase++; rawThinking = '';
                        // Create tool-call block first, then placeholder for next iteration's thinking
                        const tid = `${llmRound}-${evt.toolCallId}`;
                        const tKey = evt.toolName || tid;
                        toolBlocks[tid] = { name: evt.toolName || 'tool', args: '', result: '', isError: false, isSkill: false };
                        toolBlocks[tKey] = toolBlocks[tid];
                        const tcDiv = document.createElement('details');
                        tcDiv.className = 'tool-call'; tcDiv.dataset.toolId = tid; tcDiv.dataset.toolName = evt.toolName || ''; tcDiv.open = true;
                        tcDiv.innerHTML = '<summary>🔧 <strong>' + (evt.toolName || 'tool') + '</strong> <span class="tool-args">running...</span></summary><div class="tool-result"></div>';
                        msgContent.appendChild(tcDiv);
                        // Don't create thinking block here — created lazily when first thinking event arrives
                        thinkBlock = null;
                    }
                    else if (evt.type === 'tool_call_delta') {
                        const tid = `${llmRound}-${evt.toolCallId}`;
                        if (tid && toolBlocks[tid]) {
                            toolBlocks[tid].args += evt.content || '';
                            const tcDiv = msgContent.querySelector('.tool-call[data-tool-id="' + CSS.escape(tid) + '"]');
                            if (tcDiv) { try { const parsed = JSON.parse(toolBlocks[tid].args); tcDiv.querySelector('.tool-args').textContent = JSON.stringify(parsed); } catch (e) {} }
                        }
                    }
                    else if (evt.type === 'tool_call_end') {
                        const tid = `${llmRound}-${evt.toolCallId}`;
                        if (!tid) continue;
                        // Ensure tool block entry exists (tool_call_start may not have fired)
                        if (!toolBlocks[tid]) {
                            toolBlocks[tid] = { name: evt.toolName || 'tool', args: evt.content || '', result: '', isError: false, isSkill: false };
                            if (evt.toolName) toolBlocks[evt.toolName] = toolBlocks[tid];
                        } else if (evt.content) {
                            toolBlocks[tid].args = evt.content;
                        }
                        if (evt.toolName) { toolBlocks[tid].name = evt.toolName; toolBlocks[evt.toolName] = toolBlocks[tid]; }
                        // Ensure DOM element exists
                        let tcDiv = msgContent.querySelector('.tool-call[data-tool-id="' + CSS.escape(tid) + '"]');
                        if (!tcDiv) {
                            tcDiv = document.createElement('details');
                            tcDiv.className = 'tool-call'; tcDiv.dataset.toolId = tid; tcDiv.dataset.toolName = evt.toolName || '';
                            tcDiv.innerHTML = '<summary>🔧 <strong>' + (evt.toolName || 'tool') + '</strong> <span class="tool-args"></span></summary><div class="tool-result"></div>';
                            msgContent.appendChild(tcDiv);
                        }
                        tcDiv.open = true;
                        if (evt.toolName) { tcDiv.dataset.toolName = evt.toolName; const strong = tcDiv.querySelector('summary strong'); if (strong) strong.textContent = evt.toolName; }
                        // Detect skill read
                        if (toolBlocks[tid].name === 'read' && !toolBlocks[tid].isSkill) {
                            try { const parsed = JSON.parse(toolBlocks[tid].args || '{}'); if (parsed.path && parsed.path.endsWith('SKILL.md')) { toolBlocks[tid].isSkill = true; tcDiv.classList.add('skill-read'); } } catch (e) {}
                        }
                        // Update args display
                        try {
                            const parsed = JSON.parse(toolBlocks[tid].args || '{}');
                            tcDiv.querySelector('.tool-args').textContent = JSON.stringify(parsed);
                        } catch (e) { tcDiv.querySelector('.tool-args').textContent = ''; }
                    }
                    else if (evt.type === 'tool_result') {
                        const tid = evt.toolCallId;
                        const tName = evt.toolName;
                        let block = (tid && toolBlocks[tid]) ? toolBlocks[tid] : (tName && toolBlocks[tName]) ? toolBlocks[tName] : null;
                        if (block) {
                            block.result = evt.content || '';
                            block.isError = evt.isError;
                            const tcDiv = msgContent.querySelector('.tool-call[data-tool-id="' + CSS.escape(tid) + '"]')
                                || msgContent.querySelector('.tool-call[data-tool-name="' + CSS.escape(tName || tid) + '"]');
                            if (tcDiv) {
                                // Ensure tool name is displayed (may be missing if tool_call_end didn't fire)
                                if (tName) {
                                    tcDiv.dataset.toolName = tName;
                                    const strong = tcDiv.querySelector('summary strong');
                                    if (strong && strong.textContent === 'tool') strong.textContent = tName;
                                }
                                tcDiv.classList.toggle('tool-error', evt.isError);
                                const truncated = block.result.length > 1000
                                    ? block.result.substring(0, 1000) + '\n... (truncated)'
                                    : block.result;
                                tcDiv.querySelector('.tool-result').textContent = truncated;
                                // If cleanupToolBlocks previously marked this as [no result], clear it
                                tcDiv.classList.remove('tool-error');
                                tcDiv.open = false;
                                const summary = tcDiv.querySelector('summary');
                                const name = block.name;
                                const prefix = evt.isError ? '❌ ' : '';
                                // For skill reads, extract skill name from YAML frontmatter
                                if (block.isSkill && !evt.isError) {
                                    const m = block.result.match(/^---\s*\nname:\s*(\S+)/m);
                                    const skillName = m ? m[1] : name;
                                    summary.innerHTML = prefix + '📚 <strong>' + skillName + '</strong>';
                                } else if (summary) {
                                    let argsHtml = '';
                                    if (block.args) {
                                        try { const parsed = JSON.parse(block.args); argsHtml = ' <span class="tool-args">' + JSON.stringify(parsed) + '</span>'; } catch (e) {}
                                    }
                                    summary.innerHTML = prefix + '🔧 <strong>' + name + '</strong>' + argsHtml;
                                }
                            }
                            if (thinkBlock && rawThinking) { thinkBlock.querySelector('.think-content').innerHTML = marked.parse(rawThinking); saveThinking(currentSession, streamMsgIndex, rawThinking); thinkBlock.open = false; }
                        }
                    }
                    else if (evt.type === 'error') { streamDot.style.display = 'none'; if (currentTextSeg) finalizeTextSeg(currentTextSeg); currentTextSeg = null; msgContent.innerHTML += '<div class="stream-error">⚠️ ' + (evt.content || evt.message || '') + '</div>'; }
                } catch (e) { console.error('SSE handler:', e, p); } } catch {} messages.scrollTop = messages.scrollHeight; }
        }
    } catch (err) { if (err.name === 'AbortError') { streamDot.style.display = 'none'; if (currentTextSeg) finalizeTextSeg(currentTextSeg); } else { msgContent.innerHTML += '<div class="stream-error">' + err.message + '</div>'; showToast(err.message, true); } }
    finally { setStreaming(false); abortCtrl = null; input.focus(); cleanupToolBlocks(msgContent); try { await api('POST', `/api/session/save/${currentSession}`); } catch {} await loadSessions(); }
}

// ── Marked + Mermaid ──
const markedRenderer = new marked.Renderer(); const origCode = markedRenderer.code.bind(markedRenderer);
markedRenderer.code = function({ text, lang }) { if (lang === 'mermaid') return `<pre class="mermaid-placeholder">${text}</pre>`; return origCode({ text, lang }); };
marked.setOptions({ renderer: markedRenderer, breaks: true });
async function deferMermaidRender(container) {
    const pres = container.querySelectorAll('pre.mermaid-placeholder');
    await Promise.all(Array.from(pres).map(async pre => {
        try {
            const id = 'mm-' + Math.random().toString(36).slice(2);
            const { svg } = await mermaid.render(id, pre.textContent);
            pre.outerHTML = `<div class="mermaid-rendered">${svg}</div>`;
        } catch { pre.classList.add('mermaid-error'); }
    }));
}

// ── Theme ──
function applyTheme(key) {
    if (key === 'auto') { const mq = window.matchMedia('(prefers-color-scheme: dark)'); key = mq.matches ? 'warm-charcoal' : 'ivory-paper'; }
    document.documentElement.setAttribute('data-theme', key); localStorage.setItem('picoagent-theme', key);
    document.querySelectorAll('#theme-switcher button').forEach(b => b.classList.toggle('active', b.dataset.themeKey === (localStorage.getItem('picoagent-theme') || 'warm-charcoal').startsWith('auto') ? 'auto' : key));
    mermaid.initialize({ startOnLoad: false, theme: key === 'ivory-paper' ? 'neutral' : 'dark' });
}
const savedTheme = localStorage.getItem('picoagent-theme') || 'warm-charcoal';
applyTheme(savedTheme);
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { if (localStorage.getItem('picoagent-theme') === 'auto') applyTheme('auto'); });
document.querySelectorAll('#theme-switcher button').forEach(btn => btn.addEventListener('click', () => { applyTheme(btn.dataset.themeKey); localStorage.setItem('picoagent-theme', btn.dataset.themeKey); }));
