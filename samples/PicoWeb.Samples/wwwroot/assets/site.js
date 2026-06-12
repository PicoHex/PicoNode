document.addEventListener('DOMContentLoaded', () => {
    const formatError = (error) => error instanceof Error ? error.message : String(error);

    const readResponse = async (response) => {
        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
            return await response.json();
        }

        return await response.text();
    };

    const updateOutput = (id, meta, content, isError = false) => {
        const metaEl = document.getElementById(`meta-${id}`);
        const outEl = document.getElementById(`out-${id}`);
        
        if (!metaEl || !outEl) return;
        
        metaEl.textContent = meta;
        
        if (isError) {
            metaEl.style.backgroundColor = 'var(--accent)';
            outEl.style.color = 'var(--accent)';
        } else {
            metaEl.style.backgroundColor = 'var(--border-color)';
            outEl.style.color = 'var(--fg-code)';
        }
        
        if (typeof content === 'object') {
            outEl.textContent = JSON.stringify(content, null, 2);
        } else {
            outEl.textContent = content;
        }
    };

    const fetchShowcaseBtn = document.getElementById('btn-fetch-showcase');
    if (fetchShowcaseBtn) {
        fetchShowcaseBtn.addEventListener('click', async () => {
            try {
                updateOutput('showcase', 'SYS: FETCHING...', 'INITIALIZING REQUEST SEQUENCE');
                const start = performance.now();
                const res = await fetch('/api/showcase');
                const data = await readResponse(res);
                
                const ms = Math.round(performance.now() - start);
                updateOutput('showcase', `STATUS: ${res.status} | LATENCY: ${ms}ms`, data);
            } catch (e) {
                updateOutput('showcase', 'SYS: ERROR', formatError(e), true);
            }
        });
    }

    const fetchContentBtn = document.getElementById('btn-fetch-content');
    if (fetchContentBtn) {
        fetchContentBtn.addEventListener('click', async () => {
            try {
                updateOutput('content', 'SYS: FETCHING...', 'REQUESTING COMPRESSED PAYLOAD');
                
                const start = performance.now();
                const res = await fetch('/api/content');
                const text = await res.text();
                const ms = Math.round(performance.now() - start);
                
                const encoding = res.headers.get('content-encoding') || 'identity';
                const type = res.headers.get('content-type') || 'unknown';
                
                const preview = text.substring(0, 500) + (text.length > 500 ? '\n\n... [PAYLOAD TRUNCATED FOR DISPLAY]' : '');
                updateOutput('content', `STATUS: ${res.status} | ENCODING: ${encoding} | TYPE: ${type} | LATENCY: ${ms}ms`, preview);
            } catch (e) {
                updateOutput('content', 'SYS: ERROR', formatError(e), true);
            }
        });
    }

    const applyTheme = (theme) => {
        document.documentElement.setAttribute('data-theme', theme);
    };

    document.querySelectorAll('[data-theme-set]').forEach(btn => {
        btn.addEventListener('click', async (e) => {
            const theme = e.target.getAttribute('data-theme-set');
            try {
                updateOutput('state', 'SYS: MUTATING STATE...', `SETTING THEME -> ${theme.toUpperCase()}`);
                const res = await fetch(`/api/preferences/${theme}`, { method: 'POST' });
                const body = await readResponse(res);
                
                if (res.ok) {
                    applyTheme(theme);
                    updateOutput('state', `STATUS: ${res.status}`, body);
                } else {
                    updateOutput('state', `STATUS: ${res.status}`, body, true);
                }
            } catch (err) {
                updateOutput('state', 'SYS: ERROR', formatError(err), true);
            }
        });
    });

    const checkPrefsBtn = document.getElementById('btn-check-prefs');
    if (checkPrefsBtn) {
        checkPrefsBtn.addEventListener('click', async () => {
            try {
                updateOutput('state', 'SYS: QUERYING...', 'READING COOKIE STATE FROM SERVER');
                const res = await fetch('/api/preferences');
                const data = await readResponse(res);
                
                if (data && data.theme) {
                    applyTheme(data.theme);
                }
                updateOutput('state', `STATUS: ${res.status}`, data);
            } catch (e) {
                updateOutput('state', 'SYS: ERROR', formatError(e), true);
            }
        });
    }

    fetch('/api/preferences')
        .then(r => r.json())
        .then(d => {
            if (d && d.theme) applyTheme(d.theme);
        })
        .catch((error) => {
            updateOutput('state', 'SYS: INIT WARNING', formatError(error), true);
        });

    const fileInput = document.getElementById('file-input');
    const dropZone = document.querySelector('.file-input-wrapper');
    const uploadForm = document.getElementById('upload-form');

    if (dropZone && fileInput && uploadForm) {
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, (e) => {
                e.preventDefault();
                e.stopPropagation();
            }, false);
        });

        ['dragenter', 'dragover'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.add('dragover'), false);
        });

        ['dragleave'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.remove('dragover'), false);
        });

        dropZone.addEventListener('drop', (e) => {
            dropZone.classList.remove('dragover');
            const files = e.dataTransfer?.files;
            if (files && files.length > 0) {
                // Transfer dropped files to the hidden file input
                const dt = new DataTransfer();
                for (const file of files) {
                    dt.items.add(file);
                }
                fileInput.files = dt.files;

                // Update UI
                const count = fileInput.files.length;
                const textEl = document.querySelector('.upload-text');
                if (textEl) {
                    textEl.textContent = count > 0
                        ? `[ ${count} FILE(S) READY FOR TRANSFER ]`
                        : 'SELECT FILES OR DRAG & DROP';
                }
            }
        }, false);

        fileInput.addEventListener('change', (e) => {
            const count = e.target.files.length;
            const textEl = document.querySelector('.upload-text');
            if (textEl) {
                textEl.textContent = count > 0 ? `[ ${count} FILE(S) READY FOR TRANSFER ]` : 'SELECT FILES OR DRAG & DROP';
            }
        });

        uploadForm.addEventListener('submit', async (e) => {
            e.preventDefault();
            const formData = new FormData(uploadForm);
            
            try {
                updateOutput('upload', 'SYS: TRANSMITTING...', 'MULTIPART FORM DATA UPLOAD IN PROGRESS');
                const start = performance.now();
                const res = await fetch('/api/uploads', {
                    method: 'POST',
                    body: formData
                });
                
                let data;
                try {
                    data = await readResponse(res);
                } catch (parseError) {
                    data = formatError(parseError);
                }
                
                const ms = Math.round(performance.now() - start);
                updateOutput('upload', `STATUS: ${res.status} | LATENCY: ${ms}ms`, data);
            } catch (err) {
                updateOutput('upload', 'SYS: ERROR', formatError(err), true);
            }
        });
    }
});
