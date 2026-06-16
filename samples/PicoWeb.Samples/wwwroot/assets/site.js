document.addEventListener('DOMContentLoaded', () => {
    const formatError = (e) => e instanceof Error ? e.message : String(e);
    const readResponse = async (r) => {
        const ct = r.headers.get('content-type') || '';
        return ct.includes('application/json') ? r.json() : r.text();
    };
    const out = (id, meta, content, err) => {
        const m = document.getElementById(`meta-${id}`);
        const o = document.getElementById(`out-${id}`);
        if (!m || !o) return;
        m.textContent = meta;
        m.style.backgroundColor = err ? 'var(--accent)' : 'var(--border-color)';
        o.style.color = err ? 'var(--accent)' : 'var(--fg-code)';
        o.textContent = typeof content === 'object' ? JSON.stringify(content, null, 2) : content;
    };
    const fetch_ = async (id, url, opts) => {
        try {
            out(id, 'FETCHING...', '');
            const start = performance.now();
            const res = await fetch(url, opts);
            const data = await readResponse(res);
            out(id, `${res.status} | ${Math.round(performance.now() - start)}ms`, data);
        } catch (e) { out(id, 'ERROR', formatError(e), true); }
    };

    const byId = (id) => document.getElementById(id);
    const val = (el) => el ? el.value : '';

    byId('btn-health')?.addEventListener('click', () => fetch_('health', '/api/health'));

    const convBtn = byId('btn-convention');
    const convId = byId('convention-id');
    if (convBtn) convBtn.addEventListener('click', () => fetch_('convention', `/api/users/user/${val(convId) || 42}`));

    const raBtn = byId('btn-route-attr');
    const raCat = byId('route-category');
    const raId = byId('route-id');
    if (raBtn) raBtn.addEventListener('click', () => fetch_('route-attr', `/api/v2/products/${val(raCat) || 'electronics'}/${val(raId) || 99}`));

    const vId = byId('verb-id');
    const vid = () => val(vId) || 7;
    byId('btn-verb-delete')?.addEventListener('click', () => fetch_('verb-attr', `/api/posts/${vid()}`, { method: 'DELETE' }));
    byId('btn-verb-patch')?.addEventListener('click', () => fetch_('verb-attr', `/api/posts/${vid()}`, { method: 'PATCH' }));
});
