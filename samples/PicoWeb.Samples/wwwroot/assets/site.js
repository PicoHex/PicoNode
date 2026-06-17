document.addEventListener('DOMContentLoaded', () => {
    const fe = (e) => e instanceof Error ? e.message : String(e);
    const rr = async (r) => (r.headers.get('content-type') || '').includes('application/json') ? r.json() : r.text();
    const out = (id, meta, c, err) => {
        const m = document.getElementById(`meta-${id}`), o = document.getElementById(`out-${id}`);
        if (!m || !o) return;
        m.textContent = meta; m.style.backgroundColor = err ? 'var(--accent)' : 'var(--border-color)';
        o.style.color = err ? 'var(--accent)' : 'var(--fg-code)';
        o.textContent = typeof c === 'object' ? JSON.stringify(c, null, 2) : c;
    };
    const fetch_ = async (id, url, opts) => {
        try { out(id, 'FETCHING...', ''); const s = performance.now(), r = await fetch(url, opts), d = await rr(r); out(id, `${r.status} | ${Math.round(performance.now() - s)}ms`, d); }
        catch (e) { out(id, 'ERROR', fe(e), true); }
    };
    const $ = (id) => document.getElementById(id);
    const v = (e) => e ? e.value : '';

    // REST panels
    $('btn-info')?.addEventListener('click', () => fetch_('info', '/api/info'));
    $('btn-health')?.addEventListener('click', () => fetch_('health', '/api/health'));

    const convBtn = $('btn-convention'), convId = $('convention-id');
    if (convBtn) convBtn.addEventListener('click', () => fetch_('convention', `/api/users/user/${v(convId) || 42}`));

    const raBtn = $('btn-route-attr'), raCat = $('route-category'), raId = $('route-id');
    if (raBtn) raBtn.addEventListener('click', () => fetch_('route-attr', `/api/v2/products/${v(raCat) || 'electronics'}/${v(raId) || 99}`));

    const vId = $('verb-id'), vid = () => v(vId) || 7;
    $('btn-verb-delete')?.addEventListener('click', () => fetch_('verb-attr', `/api/posts/${vid()}`, { method: 'DELETE' }));
    $('btn-verb-patch')?.addEventListener('click', () => fetch_('verb-attr', `/api/posts/${vid()}`, { method: 'PATCH' }));

    $('btn-fetch-showcase')?.addEventListener('click', () => fetch_('showcase', '/api/showcase'));
    $('btn-fetch-content')?.addEventListener('click', async () => {
        try { out('content', 'FETCHING...', ''); const s = performance.now(), r = await fetch('/api/content'), t = await r.text(); out('content', `${r.status} | ${r.headers.get('content-encoding') || 'identity'} | ${Math.round(performance.now() - s)}ms`, t.substring(0, 500)); }
        catch (e) { out('content', 'ERROR', fe(e), true); }
    });

    // Theme / cookies
    const applyTheme = (t) => document.documentElement.setAttribute('data-theme', t);
    document.querySelectorAll('[data-theme-set]').forEach(b => {
        b.addEventListener('click', async (e) => {
            const t = e.target.getAttribute('data-theme-set');
            try { out('state', 'SETTING...', ''); const r = await fetch(`/api/preferences/${t}`, { method: 'POST' }); const d = await rr(r); if (r.ok) applyTheme(t); out('state', `${r.status}`, d); }
            catch (e) { out('state', 'ERROR', fe(e), true); }
        });
    });
    $('btn-check-prefs')?.addEventListener('click', async () => {
        try { out('state', 'FETCHING...', ''); const r = await fetch('/api/preferences'); const d = await rr(r); if (d && d.theme) applyTheme(d.theme); out('state', `${r.status}`, d); }
        catch (e) { out('state', 'ERROR', fe(e), true); }
    });
    fetch('/api/preferences').then(r => r.json()).then(d => { if (d && d.theme) applyTheme(d.theme); }).catch(() => {});

    // Upload
    const fi = $('file-input'), dz = document.querySelector('.file-input-wrapper'), uf = $('upload-form');
    if (dz && fi && uf) {
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(n => dz.addEventListener(n, e => { e.preventDefault(); e.stopPropagation(); }));
        ['dragenter', 'dragover'].forEach(n => dz.addEventListener(n, () => dz.classList.add('dragover')));
        ['dragleave'].forEach(n => dz.addEventListener(n, () => dz.classList.remove('dragover')));
        dz.addEventListener('drop', e => { dz.classList.remove('dragover'); const files = e.dataTransfer?.files; if (files && files.length > 0) { const dt = new DataTransfer(); for (const f of files) dt.items.add(f); fi.files = dt.files; } });
        fi.addEventListener('change', () => { const c = fi.files.length; const t = document.querySelector('.upload-text'); if (t) t.textContent = c > 0 ? `[ ${c} FILE(S) ]` : 'Select files'; });
        uf.addEventListener('submit', async (e) => {
            e.preventDefault();
            try { out('upload', 'UPLOADING...', ''); const s = performance.now(), r = await fetch('/api/uploads', { method: 'POST', body: new FormData(uf) }); const d = await rr(r); out('upload', `${r.status} | ${Math.round(performance.now() - s)}ms`, d); }
            catch (e) { out('upload', 'ERROR', fe(e), true); }
        });
    }

    // ── WebSocket panel ──
    let ws = null;
    const wsOut = (m, c) => {
        const o = $('out-ws');
        if (!o) return;
        if (c) o.textContent = c + '\n' + o.textContent;
        else o.textContent = m + '\n' + o.textContent;
    };

    $('btn-ws-connect')?.addEventListener('click', () => {
        if (ws) { wsOut('Already connected'); return; }
        ws = new WebSocket('ws://localhost:7004/ws/echo');
        ws.onopen = () => { out('ws', 'Connected ✅', ''); $('btn-ws-send').disabled = false; };
        ws.onmessage = (e) => { out('ws', 'Echo received', ''); wsOut('', '← ' + e.data); };
        ws.onerror = () => { out('ws', 'ERROR', 'Connection failed', true); ws = null; $('btn-ws-send').disabled = true; };
        ws.onclose = () => { out('ws', 'Disconnected', ''); ws = null; $('btn-ws-send').disabled = true; };
    });

    $('btn-ws-close')?.addEventListener('click', () => { if (ws) { ws.close(); ws = null; $('btn-ws-send').disabled = true; } });

    $('btn-ws-send')?.addEventListener('click', () => {
        const msg = v($('ws-msg')) || 'hello';
        if (ws && ws.readyState === WebSocket.OPEN) { ws.send(msg); wsOut('', '→ ' + msg); }
        else { out('ws', 'ERROR', 'Not connected', true); }
    });
});
