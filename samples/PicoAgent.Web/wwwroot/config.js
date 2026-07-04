// ── Config page — simplified: pick provider → enter key → go
let templates = [];        // from /api/config/providers
let selected = null;       // { name, baseUrl, apiFormat }

const providerButtons = document.getElementById('provider-buttons');
const keySection      = document.getElementById('key-section');
const apiKeyInput     = document.getElementById('api-key');
const baseUrlInput    = document.getElementById('base-url');
const customNameInput = document.getElementById('custom-name');
const btnConnect      = document.getElementById('btn-connect');
const statusMsg       = document.getElementById('status-msg');
const skipLink        = document.getElementById('skip-link');

(async function init() {
    try {
        const r = await fetch('/api/config/providers');
        templates = await r.json();
    } catch {}
    // Also check if already configured (skip link)
    try {
        const r = await fetch('/api/config/status');
        if ((await r.json()).configured) skipLink.style.display = 'block';
    } catch {}
    renderButtons();
})();

function renderButtons() {
    providerButtons.innerHTML = '';
    for (const t of templates) {
        const btn = document.createElement('div');
        btn.className = 'prov-btn';
        btn.innerHTML = `<div><div class="label">${esc(t.label)}</div><div class="url">${esc(t.baseUrl)}</div></div><span style="font-size:1.2em">→</span>`;
        btn.addEventListener('click', () => selectProvider(t));
        providerButtons.appendChild(btn);
    }
    // Custom provider option
    const custom = document.createElement('div');
    custom.className = 'prov-btn custom';
    custom.innerHTML = '<div><div class="label">Other (OpenAI compatible)</div><div class="url">Custom base URL</div></div><span style="font-size:1.2em">→</span>';
    custom.addEventListener('click', () => selectProvider({ name: '', label: 'Custom', baseUrl: '', apiFormat: 'openai' }));
    providerButtons.appendChild(custom);
}

function selectProvider(t) {
    selected = t;
    document.querySelectorAll('.prov-btn').forEach(b => b.classList.remove('selected'));
    const btns = providerButtons.querySelectorAll('.prov-btn');
    for (const b of btns) {
        if (b.querySelector('.label').textContent === t.label) b.classList.add('selected');
    }
    if (!t.name && !t.baseUrl) {
        // "Other" — last button
        btns[btns.length - 1].classList.add('selected');
    }
    baseUrlInput.value = t.baseUrl || '';
    customNameInput.value = t.name || '';
    customNameInput.style.display = t.name ? 'none' : '';
    apiKeyInput.value = '';
    keySection.classList.add('active');
    apiKeyInput.focus();
    statusMsg.textContent = '';
}

skipLink.addEventListener('click', () => { window.location.href = '/'; });

apiKeyInput.addEventListener('keydown', e => { if (e.key === 'Enter') doConnect(); });
baseUrlInput.addEventListener('keydown', e => { if (e.key === 'Enter') apiKeyInput.focus(); });
customNameInput.addEventListener('keydown', e => { if (e.key === 'Enter') apiKeyInput.focus(); });

btnConnect.addEventListener('click', doConnect);

async function doConnect() {
    const apiKey = apiKeyInput.value.trim();
    if (!apiKey) { setStatus('Please enter your API key', true); return; }
    if (!selected) { setStatus('Please select a provider', true); return; }

    const name = selected.name || customNameInput.value.trim() || 'custom';
    const baseUrl = baseUrlInput.value.trim() || selected.baseUrl;
    const apiFormat = selected.apiFormat || 'openai';

    setStatus('Connecting...'); btnConnect.disabled = true;
    try {
        const vr = await fetch('/api/config/validate', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ provider: name, apiKey, baseUrl: baseUrl || undefined, apiFormat }),
        });
        if (!vr.ok) {
            const err = await vr.json().catch(() => ({}));
            setStatus(`Connection failed: ${err.message || vr.status}`, true);
            return;
        }
        const models = await vr.json();
        if (!models || !models.length) {
            setStatus('No models found. Check your API key or base URL.', true);
            return;
        }
        // Pick first model as default
        const modelId = models[0].id;
        const config = {
            providers: { [name]: { apiKey, baseUrl: baseUrl || undefined, apiFormat } },
            model: modelId,
            thinkingEnabled: true,
            thinkingLevel: 'medium',
            maxTokens: 8192,
        };
        setStatus('Saving...');
        const sr = await fetch('/api/config', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(config),
        });
        if (sr.ok) {
            setStatus(`Connected! ${models.length} models found. Redirecting...`, false);
            setTimeout(() => { window.location.href = '/'; }, 800);
        } else {
            setStatus('Save failed — please try again', true);
        }
    } catch (e) {
        setStatus(`Error: ${e.message}`, true);
    } finally {
        btnConnect.disabled = false;
    }
}

function setStatus(msg, isError) {
    statusMsg.textContent = msg;
    statusMsg.className = isError ? 'error' : 'success';
}
function esc(s) { return (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
