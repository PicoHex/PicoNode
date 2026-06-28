const input = document.getElementById('input');
const sendBtn = document.getElementById('send');
const messages = document.getElementById('messages');
const status = document.getElementById('status');

fetch('/api/health').then(r => r.json()).then(h => {
    status.textContent = `model: ${h.model}`;
});

let currentAssistant = null;

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

    while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        const lines = buffer.split('\n');
        buffer = lines.pop() || '';
        for (const line of lines) {
            if (!line.startsWith('data: ')) continue;
            const data = line.slice(6);
            if (data === '[DONE]') { currentAssistant = null; return; }
            const evt = JSON.parse(data);
            switch (evt.type) {
                case 'delta':
                    currentAssistant.textContent += evt.content;
                    break;
                case 'thinking':
                    thinkingBlock.querySelector('.think-content').textContent += evt.content;
                    break;
                case 'error':
                    currentAssistant.textContent += `\n[Error: ${evt.message}]`;
                    break;
            }
            messages.scrollTop = messages.scrollHeight;
        }
    }
}

function appendMessage(role, text) {
    const el = document.createElement('div');
    el.className = `message ${role}`;
    el.textContent = text;
    messages.appendChild(el);
    return el;
}

function appendThinkingBlock(parent) {
    const el = document.createElement('details');
    el.className = 'thinking';
    el.innerHTML = '<summary>thinking...</summary><div class="think-content"></div>';
    parent.appendChild(el);
    return el;
}

sendBtn.addEventListener('click', sendMessage);
input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); } });
