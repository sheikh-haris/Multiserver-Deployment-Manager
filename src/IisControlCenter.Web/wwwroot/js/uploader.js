// Chunked, resumable package upload. Plain HTTP calls (not Blazor's file stream, which would go through the SignalR circuit).
// Every call carries X-ICC-Request: the server rejects calls without it, which stops other websites from posting here on a user's behalf.
window.iccUpload = (function () {
    const H = { 'X-ICC-Request': '1' };

    async function sha256Hex(buf) {
        const d = await crypto.subtle.digest('SHA-256', buf);
        return Array.from(new Uint8Array(d)).map(b => b.toString(16).padStart(2, '0')).join('').toUpperCase();
    }

    async function json(resp) { try { return await resp.json(); } catch { return {}; } }
    const sleep = ms => new Promise(r => setTimeout(r, ms));

    async function sendChunk(id, index, blob) {
        const buf = await blob.arrayBuffer();
        const hash = await sha256Hex(buf);
        let last = 'unknown error';
        for (let attempt = 1; attempt <= 5; attempt++) {
            try {
                const r = await fetch(`/api/uploads/${id}/chunks/${index}`, { method: 'PUT', headers: { ...H, 'X-Chunk-Sha256': hash, 'Content-Type': 'application/octet-stream' }, body: buf });
                if (r.ok) return;
                const j = await json(r);
                last = j.error || r.statusText;
                if (r.status >= 400 && r.status < 500 && r.status !== 408 && !/damaged/.test(last)) throw new Error(last);
            } catch (e) { last = e.message || String(e); if (/^(The|This|Chunk|You)/.test(last)) throw e; }
            await sleep(1000 * attempt);
        }
        throw new Error('Chunk ' + index + ' failed: ' + last);
    }

    // inputId: <input type=file>; ref: DotNetObjectReference with OnUploadProgress(int pct, string text) and OnUploadDone(string infoJson, string error)
    async function begin(inputId, componentId, environment, ref) {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[0];
        if (!file) { await ref.invokeMethodAsync('OnUploadDone', null, 'Choose a .zip file first.'); return; }
        try {
            const key = `icc-upload:${componentId}:${environment}:${file.name}:${file.size}:${file.lastModified}`;
            let session = null, received = new Set();
            const saved = sessionStorage.getItem(key);
            if (saved) {
                const r = await fetch(`/api/uploads/${saved}`);
                if (r.ok) { const info = await r.json(); if (info.status === 'Receiving' || info.status === 0) { session = { sessionId: saved, chunkSize: info.chunkSize, totalChunks: info.totalChunks }; received = new Set(info.received); } }
            }
            if (!session) {
                const r = await fetch('/api/uploads', { method: 'POST', headers: { ...H, 'Content-Type': 'application/json' }, body: JSON.stringify({ componentId, environment, fileName: file.name, size: file.size }) });
                const j = await json(r);
                if (!r.ok) throw new Error(j.error || 'The upload could not be started.');
                session = j; received = new Set();
                sessionStorage.setItem(key, session.sessionId);
            }
            const total = session.totalChunks;
            for (let i = 0; i < total; i++) {
                if (!received.has(i)) await sendChunk(session.sessionId, i, file.slice(i * session.chunkSize, Math.min(file.size, (i + 1) * session.chunkSize)));
                await ref.invokeMethodAsync('OnUploadProgress', Math.round(100 * (i + 1) / total), `Sent ${i + 1} of ${total} parts`);
            }
            const c = await fetch(`/api/uploads/${session.sessionId}/complete`, { method: 'POST', headers: H });
            if (!c.ok) throw new Error((await json(c)).error || 'Could not finish the upload.');
            sessionStorage.removeItem(key);
            await ref.invokeMethodAsync('OnUploadProgress', 100, 'Checking the package…');
            for (let n = 0; n < 1800; n++) {
                await sleep(1000);
                const r = await fetch(`/api/uploads/${session.sessionId}`);
                if (!r.ok) continue;
                const info = await r.json();
                // 3 = Ready, 4 = Rejected (enum order) — the server serializes names or numbers depending on options, so accept both.
                const st = String(info.status);
                if (st === 'Ready' || st === '3') { await ref.invokeMethodAsync('OnUploadDone', JSON.stringify(info), null); return; }
                if (st === 'Rejected' || st === '4' || st === 'Expired' || st === '5') { await ref.invokeMethodAsync('OnUploadDone', JSON.stringify(info), info.error || 'The package was refused.'); return; }
            }
            throw new Error('The check is taking too long. Reload the page and look at your uploads.');
        } catch (e) {
            await ref.invokeMethodAsync('OnUploadDone', null, e.message || String(e));
        }
    }
    return { begin };
})();
