// ── Config page logic ──
const providerList = document.getElementById('provider-list');
const addForm = document.getElementById('add-form');
const btnAdd = document.getElementById('btn-add');
const btnTestAdd = document.getElementById('btn-test-add');
const btnCancelAdd = document.getElementById('btn-cancel-add');
const btnSaveAll = document.getElementById('btn-save-all');
const defaultModel = document.getElementById('default-model');
const statusMsg = document.getElementById('status-msg');
const skipLink = document.getElementById('skip-link');

// ⚠️ DOM-only: apiKey values live in JavaScript memory for the duration of this
// page visit only. They are never persisted to localStorage or sent anywhere
// except to /api/config/validate and /api/config (both localhost).
let providers = {};     // { name: { apiKey, baseUrl, apiFormat } }
let allModels = [];     // [{ id, ownedBy }]
let editingName = null;

(async function init() {
    try {
        const r = await fetch('/api/config/status');
        if ((await r.json()).configured) skipLink.style.display = 'block';
    } catch {}
    await loadProviders();
    await loadModels();
    renderProviderList();
})();

async function loadModels() {
    try {
        allModels = await (await fetch('/api/models')).json();
        defaultModel.innerHTML = '<option value="">Select...</option>';
        for (const m of allModels) {
            const o = document.createElement('option'); o.value = m.id; o.textContent = `${m.id} (${m.ownedBy})`; defaultModel.appendChild(o);
        }
    } catch {}
}

async function loadProviders() {
    try {
        const r = await fetch('/api/config/status');
        const s = await r.json();
        // s.providers is string[] from backend. Load existing names.
        // For editing, we only have names. User must re-enter keys (security).
        if (s.providers && s.providers.length) {
            for (const name of s.providers) {
                if (!providers[name]) providers[name] = { apiKey: '', baseUrl: '', apiFormat: 'openai' };
            }
        }
    } catch {}
}

function renderProviderList() {
    providerList.innerHTML = '';
    const names = Object.keys(providers);
    if (!names.length) { providerList.innerHTML = '<p style="color:var(--fg-muted);font-size:0.85em">No providers yet. Add one to get started.</p>'; return; }
    for (const name of names) {
        const p = providers[name];
        const card = document.createElement('div'); card.className = 'provider-card';
        card.innerHTML = `<div class="name">${esc(name)}</div>
            <div class="detail">${esc(p.baseUrl || '')} · ${esc(p.apiFormat || 'openai')}</div>
            <div class="detail">API Key: ${p.apiKey ? '••••' + p.apiKey.slice(-4) : '(none)'}</div>`;
        const actions = document.createElement('div'); actions.className = 'actions';
        const editBtn = document.createElement('button'); editBtn.textContent = 'Edit'; editBtn.addEventListener('click', () => startEdit(name));
        const delBtn = document.createElement('button'); delBtn.textContent = '×'; delBtn.className = 'danger'; delBtn.style.padding = '4px 8px'; delBtn.style.fontSize = '0.75em'; delBtn.style.margin = '0';
        delBtn.addEventListener('click', async () => {
            if (Object.keys(providers).length <= 1) { setStatus('Need at least one provider', true); return; }
            delete providers[name]; renderProviderList(); setStatus(`Removed ${name}`, false);
        });
        actions.appendChild(editBtn); actions.appendChild(delBtn);
        card.appendChild(actions);
        providerList.appendChild(card);
    }
}

btnAdd.addEventListener('click', () => { editingName = null; clearAddForm(); addForm.classList.add('active'); });
btnCancelAdd.addEventListener('click', () => addForm.classList.remove('active'));

function startEdit(name) {
    editingName = name;
    const p = providers[name];
    document.getElementById('add-name').value = name;
    document.getElementById('add-apikey').value = p.apiKey || '';
    document.getElementById('add-baseurl').value = p.baseUrl || '';
    document.getElementById('add-apiformat').value = p.apiFormat || 'openai';
    addForm.classList.add('active');
    btnTestAdd.textContent = 'Test & Save';
}

function clearAddForm() {
    document.getElementById('add-name').value = '';
    document.getElementById('add-apikey').value = '';
    document.getElementById('add-baseurl').value = '';
    document.getElementById('add-apiformat').value = 'openai';
}

btnTestAdd.addEventListener('click', async () => {
    const name = document.getElementById('add-name').value.trim();
    const apiKey = document.getElementById('add-apikey').value.trim();
    const baseUrl = document.getElementById('add-baseurl').value.trim();
    const apiFormat = document.getElementById('add-apiformat').value;
    if (!name || !apiKey) { setStatus('Name and API Key required', true); return; }

    setStatus('Testing...'); btnTestAdd.disabled = true;
    try {
        const vr = await fetch('/api/config/validate', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ provider: name, apiKey, baseUrl: baseUrl || undefined, apiFormat }),
        });
        if (!vr.ok) { const err = await vr.json().catch(() => ({})); setStatus(`Connection failed: ${err.message || vr.status}`, true); return; }
        const models = await vr.json();

        if (editingName && editingName !== name) delete providers[editingName];
        providers[name] = { apiKey, baseUrl, apiFormat };
        renderProviderList();
        addForm.classList.remove('active');
        setStatus(`Connected! Found ${models.length} models.`, false);
        await loadModels();
    } catch (e) { setStatus(`Error: ${e.message}`, true); }
    finally { btnTestAdd.disabled = false; }
});

btnSaveAll.addEventListener('click', async () => {
    if (!Object.keys(providers).length) { setStatus('Need at least one provider', true); return; }
    setStatus('Saving...'); btnSaveAll.disabled = true;
    try {
        const firstProv = Object.keys(providers)[0];
        const first = providers[firstProv];
        const config = {
            thinkingEnabled: true, thinkingLevel: 'medium',
            model: defaultModel.value || null,
            maxTokens: 4096,
            providers,
        };
        // Ensure the config uses the right default provider
        const r = await fetch('/api/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(config) });
        if (r.ok) { setStatus('Saved! Redirecting...', false); window.location.href = '/'; }
        else { setStatus('Save failed', true); }
    } catch (e) { setStatus(`Error: ${e.message}`, true); }
    finally { btnSaveAll.disabled = false; }
});

skipLink.addEventListener('click', () => { window.location.href = '/'; });

function setStatus(msg, isError) { statusMsg.textContent = msg; statusMsg.className = isError ? 'error' : 'success'; }
function esc(s) { return (s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
