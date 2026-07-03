// ── Config page logic ──
const providerSel = document.getElementById('provider');
const apiKeyInput = document.getElementById('apikey');
const baseUrlInput = document.getElementById('baseurl');
const apiFormatSel = document.getElementById('apiformat');
const customFields = document.getElementById('custom-fields');
const btnConnect = document.getElementById('btn-connect');
const statusMsg = document.getElementById('status-msg');
const skipLink = document.getElementById('skip-link');

let discoveredModels = [];

(async function init() {
    // Check if already configured
    try {
        const r = await fetch('/api/config/status');
        const s = await r.json();
        if (s.configured) {
            showSkip();
        }
    } catch {}

    // Load provider templates
    try {
        const r = await fetch('/api/config/providers');
        const providers = await r.json();
        providerSel.innerHTML = '<option value="">Select a provider...</option>';
        for (const p of providers) {
            const o = document.createElement('option');
            o.value = JSON.stringify({ name: p.name, baseUrl: p.baseUrl, apiFormat: p.apiFormat });
            o.textContent = p.label;
            providerSel.appendChild(o);
        }
        providerSel.innerHTML += '<option value="__custom__">Custom...</option>';
    } catch (e) {
        setStatus('Failed to load providers', true);
    }
})();

providerSel.addEventListener('change', () => {
    const val = providerSel.value;
    if (val === '__custom__') {
        customFields.style.display = 'block';
        baseUrlInput.value = '';
        apiFormatSel.value = 'openai';
    } else if (val) {
        try {
            const p = JSON.parse(val);
            customFields.style.display = 'none';
            baseUrlInput.value = p.baseUrl;
            apiFormatSel.value = p.apiFormat;
        } catch { customFields.style.display = 'none'; }
    }
});

// ── Single button: validate → save → redirect ──
btnConnect.addEventListener('click', async () => {
    const provider = getProvider();
    const apiKey = apiKeyInput.value.trim();
    if (!provider || !apiKey) { setStatus('Provider and API Key required', true); return; }

    setStatus('Connecting...');
    btnConnect.disabled = true;
    try {
        // Step 1: validate
        const vr = await fetch('/api/config/validate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                provider, apiKey,
                baseUrl: baseUrlInput.value.trim() || undefined,
                apiFormat: apiFormatSel.value,
            }),
        });
        if (!vr.ok) {
            const err = await vr.json().catch(() => ({}));
            setStatus(`Connection failed: ${err.message || vr.status}`, true);
            return;
        }
        const models = await vr.json();
        discoveredModels = models;
        setStatus(`Connected! Found ${models.length} models. Saving...`, false);

        // Step 2: save config immediately
        const config = {
            thinkingEnabled: true,
            thinkingLevel: 'medium',
            model: models[0]?.id || null,
            maxTokens: 4096,
            providers: {
                [provider]: {
                    apiKey,
                    baseUrl: baseUrlInput.value.trim() || undefined,
                    apiFormat: apiFormatSel.value,
                },
            },
        };
        const sr = await fetch('/api/config', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(config),
        });
        if (!sr.ok) {
            setStatus('Save failed after successful validation', true);
            return;
        }

        // Step 3: redirect
        setStatus('Ready! Redirecting...', false);
        window.location.href = '/';
    } catch (e) {
        setStatus(`Error: ${e.message}`, true);
    } finally {
        btnConnect.disabled = false;
    }
});

function getProvider() {
    const val = providerSel.value;
    if (val === '__custom__') return baseUrlInput.value.trim() ? 'custom' : null;
    if (!val) return null;
    try { return JSON.parse(val).name; } catch { return null; }
}

function setStatus(msg, isError) {
    statusMsg.textContent = msg;
    statusMsg.className = isError ? 'error' : 'success';
}

function showSkip() {
    skipLink.style.display = 'block';
    skipLink.addEventListener('click', () => { window.location.href = '/'; });
}
