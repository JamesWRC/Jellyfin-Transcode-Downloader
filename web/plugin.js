(() => {
    const SUPPORTED_LOCALES = ['en-us', 'de', 'fr', 'es', 'zh-cn', 'nl'];

    // Bitrate rungs, calibrated for H.264. Other codecs scale these by their efficiency
    // factor so a given rung means roughly the same picture quality across codecs.
    const QUALITY_TIERS = [
        { resolution: '2160p', bitrate: 120_000_000 },
        { resolution: '2160p', bitrate:  80_000_000 },
        { resolution: '2160p', bitrate:  60_000_000 },
        { resolution: '2160p', bitrate:  40_000_000 },
        { resolution: '2160p', bitrate:  20_000_000 },
        { resolution: '1440p', bitrate:  15_000_000 },
        { resolution: '1440p', bitrate:  10_000_000 },
        { resolution: '1080p', bitrate:   8_000_000 },
        { resolution: '1080p', bitrate:   6_000_000 },
        { resolution: '720p',  bitrate:   4_000_000 },
        { resolution: '720p',  bitrate:   3_000_000 },
        { resolution: '720p',  bitrate:   1_500_000 },
        { resolution: '480p',  bitrate:     720_000 },
        { resolution: '360p',  bitrate:     420_000 },
    ];

    // --- Codecs ---

    // `factor` scales the H.264-calibrated tiers above to a roughly equivalent quality.
    const VIDEO_CODECS = [
        { id: 'h264', label: 'H.264 (AVC)',  tag: 'H264', factor: 1 },
        { id: 'hevc', label: 'HEVC (H.265)', tag: 'HEVC', factor: 0.65 },
        { id: 'av1',  label: 'AV1',          tag: 'AV1',  factor: 0.5 },
    ];

    const AUDIO_CODECS = [
        { id: 'aac',  label: 'AAC',  tag: 'AAC' },
        { id: 'opus', label: 'Opus', tag: 'Opus' },
    ];

    const DEFAULT_VIDEO_CODEC = 'h264';
    const DEFAULT_AUDIO_CODEC = 'aac';

    const STORAGE_VIDEO_CODEC = 'transcodeDownloader.videoCodec';
    const STORAGE_AUDIO_CODEC = 'transcodeDownloader.audioCodec';

    let selectedVideoCodec = DEFAULT_VIDEO_CODEC;
    let selectedAudioCodec = DEFAULT_AUDIO_CODEC;

    // { video: { h264: { encoder, hardware, supported, allowed }, … }, audio: { … } }
    let codecCapabilities = null;
    // false while unknown and whenever the capability probe failed — the UI then stays on
    // H.264/AAC and hides the selectors rather than guessing what the server can do.
    let codecCapabilitiesResolved = false;
    let codecCapabilitiesPromise = null;
    // { paced, rateBps } from the server, or null when unknown. Only used to label tiers.
    let downloadLimits = null;
    // Jellyfin always encodes the audio at 256 kbps; it counts towards the file size.
    const AUDIO_BITRATE = 256_000;

    function findCodec(catalogue, id) {
        return catalogue.find(codec => codec.id === id) || null;
    }

    function codecTag(catalogue, id) {
        const codec = findCodec(catalogue, id);
        return codec ? codec.tag : String(id).toUpperCase();
    }

    function codecLabel(catalogue, id) {
        const codec = findCodec(catalogue, id);
        return codec ? codec.label : String(id).toUpperCase();
    }

    function videoCodecFactor(id) {
        const codec = findCodec(VIDEO_CODECS, id);
        return codec ? codec.factor : 1;
    }

    // Round a scaled bitrate to a readable step so codec-relative labels stay legible.
    // With factor 1 (H.264) every tier round-trips to its original value.
    function scaleBitrate(baseBitrate, factor) {
        const scaled = baseBitrate * factor;
        if (scaled >= 10_000_000) return Math.round(scaled / 1_000_000) * 1_000_000;
        if (scaled >= 1_000_000) return Math.round(scaled / 100_000) * 100_000;
        return Math.round(scaled / 10_000) * 10_000;
    }

    function formatBitrate(bitrate) {
        if (bitrate >= 1_000_000) return `${Number((bitrate / 1_000_000).toFixed(1))} Mbps`;
        return `${Math.round(bitrate / 1000)} kbps`;
    }

    // --- i18n ---

    // Fallback strings used when strings fetch fails or locale is en-us.
    const FALLBACK_STRINGS = {
        ChooseQuality: 'Choose quality',
        LoadingMediaInfo: 'Loading media info…',
        NoTranscodeOptions: 'No transcode options available.',
        ItemNotTranscodable: 'Transcoded downloads are only available for movies and episodes.',
        Waiting: 'Waiting…',
        DownloadTranscode: 'Download (Transcode…)',
        DownloadFailed: 'Download failed.',
        ChooseVideoCodec: 'Video codec',
        ChooseAudioCodec: 'Audio codec',
        CodecUnsupported: "not supported by this server's FFmpeg",
        CodecDisabledByAdmin: 'disabled by the server administrator',
        SoftwareEncodingHint: 'Software encoding — this can take a long time and the file is held in memory until it finishes.',
        DownloadFailedCodec: 'Download failed — the server could not encode with {codec}.',
        DownloadFailedStatus: 'Download failed (HTTP {status}).',
        BrowserHandoffHint: 'The Jellyfin Android app cannot save downloads itself, so this opens in your browser. The file is saved as stream.mp4 — rename it afterwards.',
        BrowserHandoffStarted: 'Handed to your browser — check its downloads.',
        BrowserHandoffFailed: 'Could not open your browser.',
        WaitingForOwnDownload: 'Waiting for your other download to finish…',
        DownloadIncomplete: 'Download incomplete — the server stopped at {done} of {total}. Not saved; try again and keep this page open.',
        DownloadIncompleteNoTime: 'Download incomplete — the server stopped before the end. Not saved; try again and keep this page open.',
        CodecNotApplePlayable: "won't play in Apple's players — use H.264 / AAC",
        IosAppHint: 'Turn off "Transcoded Downloads" in the app\'s settings, or the app ignores the quality you pick here. Finished downloads open in Files.',
        SentToAppDownloads: "Sent to the Jellyfin app's Downloads. It appears there when finished.",
        WaitingForServerSlot: 'Server busy — waiting for a free download slot…',
        NoSpace: 'The server has no room for this download right now. Try a lower quality, or try again later.',
        OwnDownloadRunning: 'You already have a download running. Try again when it finishes.',
        ServerBusyTryLater: 'The server is busy with other downloads. Try again in a minute.',
        TimeLeft: '~{time} left',
    };

    // Populated by initStrings(); null until loaded.
    let loadedStrings = null;

    function t(key) {
        if (loadedStrings && loadedStrings[key]) return loadedStrings[key];
        return FALLBACK_STRINGS[key] || key;
    }

    // Resolve locale → candidate list with fallback chain: exact → base → en-us
    function resolveLocaleCandidates(locale) {
        const lower = (locale || '').toLowerCase().replace('_', '-');
        const base = lower.split('-')[0];
        const candidates = [];
        if (SUPPORTED_LOCALES.includes(lower)) candidates.push(lower);
        if (base !== lower && SUPPORTED_LOCALES.includes(base)) candidates.push(base);
        if (!candidates.includes('en-us')) candidates.push('en-us');
        return candidates;
    }

    async function fetchStrings(locale) {
        const candidates = resolveLocaleCandidates(locale);
        const base = document.querySelector('script[src*="TranscodeDownloader/ClientScript"]');
        const origin = base
            ? new URL(base.src).origin
            : window.location.origin;
        const basePath = base
            ? new URL(base.src).pathname.replace('/TranscodeDownloader/ClientScript', '')
            : '';

        for (const candidate of candidates) {
            try {
                const res = await fetch(`${origin}${basePath}/TranscodeDownloader/strings/${candidate}.json`);
                if (!res.ok) continue;
                const strings = await res.json();
                return { lang: candidate, strings };
            } catch (_) {
                // try next candidate
            }
        }
        return null;
    }

    // Eagerly load strings; resolves once loaded (or on failure).
    let stringsPromise = null;

    function initStrings() {
        stringsPromise = new Promise((resolve) => {
            function tryLoad(attempt) {
                // globalize sets document.documentElement.lang to the normalized locale
                // (e.g. 'de', 'en-us') during its module init — before any deferred script runs.
                const locale = document.documentElement.lang || null;
                if (locale) {
                    fetchStrings(locale).then(result => {
                        if (result) loadedStrings = result.strings;
                        console.log('[TranscodeDownloader] strings loaded for locale:', locale);
                        resolve();
                    }).catch(() => resolve());
                } else if (attempt < 20) {
                    setTimeout(() => tryLoad(attempt + 1), 250);
                } else {
                    console.warn('[TranscodeDownloader] locale not detected, using fallback strings');
                    resolve();
                }
            }
            tryLoad(0);
        });
    }

    // --- Download queue ---
    // Each entry: { id, filename, estimatedBytes, url, stopUrl, abortController, status: 'waiting'|'active' }
    const downloadQueue = [];
    let queueProcessing = false;

    function randomId() {
        const bytes = new Uint8Array(16);
        (window.crypto || window.msCrypto).getRandomValues(bytes);
        return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
    }

    // Jellyfin only deletes a progressive transcode's temp file when it kills the job, and it
    // never kills a job whose ffmpeg already exited (TranscodeManager.PingTimer bails out on
    // HasExited before arming the kill timer). A completed download always ends that way, so the
    // file would sit in the transcode folder until the next server restart. Killing the job
    // explicitly is the only cleanup path — see docs/adr/0004-stop-active-encoding-after-download.md.
    function stopActiveEncoding(entry) {
        if (!entry.stopUrl) return;
        const stopUrl = entry.stopUrl;
        entry.stopUrl = null; // never fire twice for the same job
        try {
            fetch(stopUrl, { method: 'DELETE', keepalive: true, headers: authHeaders(entry.token) }).catch((err) => {
                console.warn('[TranscodeDownloader] failed to stop server transcode:', err);
            });
        } catch (err) {
            console.warn('[TranscodeDownloader] failed to stop server transcode:', err);
        }
    }

    // No 'pagehide' stop. iOS fires pagehide when the user merely switches apps or locks
    // the phone, and stopping there killed a download that was still running (2026-11-14, cut at
    // 21 of 45 min). The server now stops the transcode and deletes its file itself when the
    // download's connection ends, so a page that really closes is cleaned up without our help.

    // Jellyfin 12 disables legacy authorization, so `?api_key=` and X-Emby-Token are
    // rejected with 401 and only `Authorization: MediaBrowser Token="…"` is accepted. Every
    // authenticated request sends the token this way, never in a URL.
    function authHeaders(token) {
        return token ? { Authorization: `MediaBrowser Token="${token}"` } : {};
    }

    // --- Download tickets ---

    // The server hands out a ticket per download: it checks the user's and the server's download
    // slots and that the transcode folder has room for the output, and the ticket on the stream
    // URL is what makes the server pace the transfer. Returns { status, body }.
    async function requestTicket(entry, signal) {
        const res = await fetch(`${entry.baseUrl}/TranscodeDownloader/Tickets`, {
            method: 'POST',
            headers: Object.assign({ 'Content-Type': 'application/json' }, authHeaders(entry.token)),
            body: JSON.stringify({ ItemId: entry.itemId, VideoBitrate: entry.bitrate }),
            signal,
        });
        let body = null;
        try { body = await res.json(); } catch (_) { /* 4xx/5xx without a body */ }
        return { status: res.status, body };
    }

    // The server records the result just after the response ends, so a quick first ask can miss it.
    async function fetchDownloadResult(entry) {
        for (let attempt = 0; attempt < 6; attempt++) {
            try {
                const res = await fetch(`${entry.baseUrl}/TranscodeDownloader/Tickets/${encodeURIComponent(entry.ticket)}`, {
                    headers: authHeaders(entry.token),
                });
                if (res.ok) return await res.json();
                if (res.status !== 404) return null;
            } catch (_) {
                return null;
            }
            await new Promise(r => setTimeout(r, 500));
        }
        return null; // unknown: save rather than throw away a download that may be fine
    }

    // The share of bytes received is the best position estimate: Jellyfin does not keep the
    // encode position once ffmpeg has exited.
    function incompleteMessage(entry, outcome) {
        const runtime = Number(readField(outcome, 'RuntimeSeconds')) || 0;
        const share = entry.estimatedBytes ? Math.min(1, (entry.receivedBytes || 0) / entry.estimatedBytes) : 0;
        return runtime && share
            ? t('DownloadIncomplete').replace('{done}', `~${formatClock(runtime * share)}`).replace('{total}', formatClock(runtime))
            : t('DownloadIncompleteNoTime');
    }

    function formatClock(seconds) {
        const total = Math.max(0, Math.round(Number(seconds) || 0));
        const h = Math.floor(total / 3600);
        const m = Math.floor((total % 3600) / 60);
        const sec = String(total % 60).padStart(2, '0');
        return h ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
    }

    function busyNotice(body) {
        return t(readField(body, 'Reason') === 'user_busy' ? 'OwnDownloadRunning' : 'ServerBusyTryLater');
    }

    function waitFor(ms, signal) {
        return new Promise((resolve, reject) => {
            const timer = setTimeout(resolve, ms);
            if (signal) {
                signal.addEventListener('abort', () => {
                    clearTimeout(timer);
                    reject(new DOMException('Aborted', 'AbortError'));
                }, { once: true });
            }
        });
    }

    function formatSize(bytes) {
        if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`;
        return `${Math.max(1, Math.round(bytes / 1e6))} MB`;
    }

    function formatDuration(seconds) {
        if (seconds < 90) return `${Math.max(1, Math.round(seconds))} s`;
        const minutes = Math.round(seconds / 60);
        if (minutes < 90) return `${minutes} min`;
        const hours = Math.floor(minutes / 60);
        return `${hours} h ${minutes % 60} min`;
    }

    // Size, and how long the transfer takes when this client is paced, for a tier label.
    function tierEstimate(videoBitrate, item) {
        const seconds = item && item.RunTimeTicks ? item.RunTimeTicks / 10_000_000 : 0;
        if (!seconds) return '';
        const bytes = (videoBitrate + AUDIO_BITRATE) * seconds / 8;
        let text = ` · ~${formatSize(bytes)}`;
        if (downloadLimits && downloadLimits.paced && downloadLimits.rateBps > 0) {
            text += ` · ~${formatDuration(bytes * 8 / downloadLimits.rateBps)}`;
        }
        return text;
    }

    function buildStopUrl(baseUrl, deviceId, playSessionId) {
        return `${baseUrl}/Videos/ActiveEncodings?deviceId=${encodeURIComponent(deviceId)}`
            + `&playSessionId=${playSessionId}`;
    }

    // --- Deferred transcode cleanup (browser handoff only) ---

    // A download handed to the system browser (see handOffToBrowser) leaves this page with no
    // completion signal, so the ADR-0004 stop cannot be fired when it ends. The job is recorded
    // instead and stopped on a later page load, once it is old enough that ffmpeg has certainly
    // exited — at which point DELETE /Videos/ActiveEncodings only deletes the leftover temp file.
    // The age threshold is what keeps the stop from killing a transcode the browser is still
    // streaming; leaving the file a few hours longer is the cheaper mistake.
    const STORAGE_PENDING_STOPS = 'transcodeDownloader.pendingStops';
    const PENDING_STOP_MIN_AGE_MS = 6 * 60 * 60 * 1000;
    const PENDING_STOP_MAX = 50;

    function readPendingStops() {
        try {
            const raw = window.localStorage.getItem(STORAGE_PENDING_STOPS);
            const parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed.filter(e => e && e.baseUrl && e.deviceId && e.playSessionId) : [];
        } catch (_) {
            return [];
        }
    }

    function writePendingStops(entries) {
        try {
            window.localStorage.setItem(STORAGE_PENDING_STOPS, JSON.stringify(entries));
        } catch (_) {
            // localStorage unavailable — the temp file then survives until the server restarts,
            // which is the pre-existing behaviour rather than a new failure.
        }
    }

    // The token is deliberately not stored: it can expire or be revoked between the handoff and
    // the flush, so the stop is rebuilt with whatever token the session holds at that point.
    function rememberPendingStop(baseUrl, deviceId, playSessionId) {
        const entries = readPendingStops();
        entries.push({ baseUrl, deviceId, playSessionId, at: Date.now() });
        writePendingStops(entries.slice(-PENDING_STOP_MAX));
    }

    function flushPendingStops() {
        const entries = readPendingStops();
        const now = Date.now();
        const due = entries.filter(entry => now - (entry.at || 0) >= PENDING_STOP_MIN_AGE_MS);
        if (due.length === 0) return;

        getApiClient(0, 10, (client) => {
            const token = client.accessToken();
            const baseUrl = client.serverAddress() || window.location.origin;
            due.forEach((entry) => {
                const url = buildStopUrl(entry.baseUrl || baseUrl, entry.deviceId, entry.playSessionId);
                fetch(url, { method: 'DELETE', headers: authHeaders(token) }).catch((err) => {
                    console.warn('[TranscodeDownloader] deferred transcode stop failed:', err);
                });
            });
            // Re-read rather than reusing `entries`, so a handoff recorded while the ApiClient
            // was still resolving is not dropped. Due entries go whether or not the requests
            // succeed: a stop that fails now will not start succeeding later, and retrying
            // forever would grow the list without bound.
            writePendingStops(readPendingStops().filter(entry => now - (entry.at || 0) < PENDING_STOP_MIN_AGE_MS));
        }, () => {
            // No ApiClient — leave the entries in place and retry on the next page load.
        });
    }

    function enqueue(entry) {
        downloadQueue.push(entry);
        renderQueue();
        if (!queueProcessing) processQueue();
    }

    function removeFromQueue(id) {
        const idx = downloadQueue.findIndex(e => e.id === id);
        if (idx === -1) return;
        const entry = downloadQueue[idx];
        if (entry.status === 'active' && entry.abortController) {
            entry.abortController.abort();
        }
        downloadQueue.splice(idx, 1);
        renderQueue();
        // If we cancelled the active item, processQueue will be called by the fetch .finally
    }

    async function processQueue() {
        if (queueProcessing) return;
        if (downloadQueue.length === 0) return;

        queueProcessing = true;
        while (downloadQueue.length > 0) {
            const entry = downloadQueue[0];
            entry.status = 'active';
            entry.abortController = new AbortController();
            renderQueue();

            try {
                const signal = entry.abortController.signal;
                let ticket = null;
                while (!ticket) {
                    const { status, body } = await requestTicket(entry, signal);
                    if (status === 200 && body) {
                        ticket = body;
                    } else if (status === 429) {
                        const reason = readField(body, 'Reason');
                        updateEntryStatus(entry, t(reason === 'user_busy' ? 'WaitingForOwnDownload' : 'WaitingForServerSlot'));
                        await waitFor((Number(readField(body, 'RetryAfterSeconds')) || 20) * 1000, signal);
                    } else if (status === 507) {
                        const noSpace = new Error('no space');
                        noSpace.userMessage = t('NoSpace');
                        throw noSpace;
                    } else {
                        const httpError = new Error(`HTTP ${status}`);
                        httpError.status = status;
                        throw httpError;
                    }
                }
                entry.estimatedBytes = Number(readField(ticket, 'EstimatedBytes')) || entry.estimatedBytes;
                entry.ticket = readField(ticket, 'Ticket');
                entry.startedAt = Date.now();
                const url = `${entry.url}&TdTicket=${encodeURIComponent(readField(ticket, 'Ticket'))}`;
                const response = await fetch(url, { signal, headers: authHeaders(entry.token) });
                if (!response.ok) {
                    const httpError = new Error(`HTTP ${response.status}`);
                    httpError.status = response.status;
                    throw httpError;
                }
                const blob = await readStream(response.body, entry.estimatedBytes, entry);
                // A transcode stopped part-way still ends the response cleanly, so ask the server
                // whether the encode reached the end before saving what may be half a film.
                const outcome = await fetchDownloadResult(entry);
                if (outcome && readField(outcome, 'Complete') === false) {
                    const incomplete = new Error('incomplete');
                    incomplete.userMessage = incompleteMessage(entry, outcome);
                    throw incomplete;
                }
                triggerBlobDownload(blob, entry.filename);
                updateEntryProgress(entry, 1, entry.estimatedBytes);
                await new Promise(r => setTimeout(r, 1000));
            } catch (err) {
                // The server breaks the connection of a download it knows is incomplete; say why.
                if (err.name !== 'AbortError' && !err.userMessage && !err.status && entry.ticket) {
                    const outcome = await fetchDownloadResult(entry);
                    if (outcome && readField(outcome, 'Complete') === false) err.userMessage = incompleteMessage(entry, outcome);
                }
                if (err.name !== 'AbortError') {
                    console.error('[TranscodeDownloader] Download failed:', err);
                    updateEntryStatus(entry, describeFailure(entry, err));
                    await new Promise(r => setTimeout(r, 3000));
                }
            }

            // Whether the download finished, failed, or was aborted, the server-side job is done
            // with — and it will not clean up its own temp file.
            stopActiveEncoding(entry);

            // Remove this entry (whether done, failed, or aborted)
            const idx = downloadQueue.indexOf(entry);
            if (idx !== -1) downloadQueue.splice(idx, 1);
            renderQueue();
        }
        queueProcessing = false;
    }

    // An HTTP status means the server rejected the request outright, which disproves "the
    // encoder failed to start" — so it always wins over the codec heuristic. Only a request the
    // server accepted and then failed to deliver a single byte for points at an encoder that
    // Jellyfin resolves to but FFmpeg does not actually have.
    function describeFailure(entry, err) {
        if (err && err.userMessage) return err.userMessage;
        if (err && err.status) return t('DownloadFailedStatus').replace('{status}', err.status);
        if (!entry.receivedBytes && entry.videoCodec && entry.videoCodec !== DEFAULT_VIDEO_CODEC) {
            return t('DownloadFailedCodec').replace('{codec}', codecLabel(VIDEO_CODECS, entry.videoCodec));
        }
        return t('DownloadFailed');
    }

    // --- Stream reader ---

    async function readStream(body, estimatedBytes, entry) {
        const reader = body.getReader();
        const chunks = [];
        let received = 0;

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            chunks.push(value);
            received += value.byteLength;
            entry.receivedBytes = received;
            updateEntryProgress(entry, received / estimatedBytes, estimatedBytes, received);
        }

        // A transcode that dies on startup can still return 200 and then close the stream
        // immediately. Without this, the user gets a 0-byte .mp4 reported as a success.
        if (received === 0) throw new Error('Transcode produced no data');

        return new Blob(chunks, { type: 'video/mp4' });
    }

    // --- Queue panel UI ---

    function injectQueuePanel() {
        if (document.getElementById('qd-queue-panel')) return;

        const panel = document.createElement('div');
        panel.id = 'qd-queue-panel';
        panel.style.cssText = 'display:none;position:fixed;bottom:16px;right:16px;background:#1a1a1a;border:1px solid #444;border-radius:6px;color:#fff;font-size:13px;min-width:320px;max-width:400px;z-index:9999;font-family:monospace;overflow:hidden;';

        const list = document.createElement('div');
        list.id = 'qd-queue-list';
        panel.appendChild(list);

        document.body.appendChild(panel);
    }

    function renderQueue() {
        const panel = document.getElementById('qd-queue-panel');
        const list = document.getElementById('qd-queue-list');
        if (!panel || !list) return;

        if (downloadQueue.length === 0) {
            panel.style.display = 'none';
            return;
        }

        panel.style.display = 'block';

        // Remove rows for entries that are no longer in the queue
        Array.from(list.children).forEach(row => {
            const entryId = row.dataset.entryId;
            if (!downloadQueue.find(e => e.id === entryId)) row.remove();
        });

        // Add rows for new entries and update status of existing ones
        downloadQueue.forEach((entry, i) => {
            let row = list.querySelector(`[data-entry-id="${entry.id}"]`);

            if (!row) {
                // Build the row for the first time
                row = document.createElement('div');
                row.dataset.entryId = entry.id;
                row.style.cssText = 'padding:10px 14px;border-bottom:1px solid #2a2a2a;';

                const top = document.createElement('div');
                top.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:4px;';

                const name = document.createElement('div');
                name.style.cssText = 'flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:bold;';
                name.textContent = entry.filename;

                const cancelBtn = document.createElement('button');
                cancelBtn.textContent = '✕';
                cancelBtn.style.cssText = 'padding:2px 8px;border-radius:4px;border:1px solid #666;background:#333;color:#fff;cursor:pointer;font-size:12px;flex-shrink:0;';
                cancelBtn.addEventListener('click', () => removeFromQueue(entry.id));

                top.appendChild(name);
                top.appendChild(cancelBtn);
                row.appendChild(top);

                // Codec + bitrate, so several queued items of the same title stay distinguishable
                if (entry.codecLabel) {
                    const meta = document.createElement('div');
                    meta.style.cssText = 'font-size:11px;color:#888;margin-bottom:4px;';
                    meta.textContent = entry.codecLabel;
                    row.appendChild(meta);
                }

                // Progress bar (always created; hidden for waiting items)
                const track = document.createElement('div');
                track.className = 'qd-track';
                track.style.cssText = 'height:5px;background:#333;border-radius:3px;margin-bottom:4px;display:none;';
                const fill = document.createElement('div');
                fill.id = `qd-fill-${entry.id}`;
                fill.style.cssText = 'height:100%;width:0%;background:#00a4dc;border-radius:3px;transition:width 0.2s;';
                track.appendChild(fill);
                row.appendChild(track);

                const statusEl = document.createElement('div');
                statusEl.id = `qd-status-${entry.id}`;
                statusEl.style.cssText = 'font-size:12px;';
                row.appendChild(statusEl);

                // Insert at correct position
                const sibling = list.children[i];
                if (sibling) list.insertBefore(row, sibling);
                else list.appendChild(row);
            }

            // Update active/waiting appearance without touching the fill width
            const track = row.querySelector('.qd-track');
            const statusEl = row.querySelector(`#qd-status-${entry.id}`);
            if (entry.status === 'active') {
                if (track) track.style.display = 'block';
                if (statusEl) statusEl.style.color = '#aaa';
            } else {
                if (track) track.style.display = 'none';
                if (statusEl) { statusEl.style.color = '#666'; statusEl.textContent = t('Waiting'); }
            }
        });
    }

    function updateEntryProgress(entry, ratio, estimatedBytes, receivedBytes) {
        const fill = document.getElementById(`qd-fill-${entry.id}`);
        const statusEl = document.getElementById(`qd-status-${entry.id}`);
        if (!fill || !statusEl) return;

        const pct = Math.min(Math.round(ratio * 100), 100);
        fill.style.width = pct + '%';

        const receivedMB = receivedBytes != null ? (receivedBytes / 1_048_576).toFixed(1) : null;
        const estimatedMB = (estimatedBytes / 1_048_576).toFixed(1);
        let text = receivedMB != null
            ? `${receivedMB} MB / ~${estimatedMB} MB · ${pct}%`
            : `~${estimatedMB} MB · ${pct}%`;
        // Remaining time from the observed rate, once there is enough of it to mean something.
        const elapsed = entry.startedAt ? (Date.now() - entry.startedAt) / 1000 : 0;
        if (receivedBytes && elapsed > 5 && pct < 100) {
            const remaining = Math.max(0, estimatedBytes - receivedBytes) / (receivedBytes / elapsed);
            text += ` · ${t('TimeLeft').replace('{time}', formatDuration(remaining))}`;
        }
        statusEl.textContent = text;
    }

    function updateEntryStatus(entry, text) {
        const statusEl = document.getElementById(`qd-status-${entry.id}`);
        if (statusEl) statusEl.textContent = text;
    }

    // --- Jellyfin Android app (WebView) handoff ---

    // The Jellyfin Android app hosts jellyfin-web in a WebView that registers no
    // DownloadListener — org.jellyfin.mobile has none anywhere, because its own Download button
    // goes through the NativeInterface.downloadFiles bridge, which takes item ids and resolves
    // the URL natively. A WebView with no DownloadListener silently discards every download the
    // page starts, so the blob <a download> click below vanishes: the transcode streams to 100%,
    // no file is written and nothing is logged. NativeShell.openUrl fires an ACTION_VIEW intent
    // and the system browser downloads the URL instead, which is the only path that works inside
    // the app. See issue #33.
    function isAndroidAppWebView() {
        const shell = window.NativeShell;
        if (!shell || typeof shell.openUrl !== 'function') return false;
        // The desktop shell defines NativeShell too and saves blobs perfectly well; NativeInterface
        // is the Android app's own JS bridge, so it is what distinguishes the two.
        if (!window.NativeInterface || typeof window.NativeInterface.openUrl !== 'function') return false;
        return /Android/i.test(navigator.userAgent || '');
    }

    // Handing the URL over ends this page's involvement: the browser owns the transfer, so there
    // is no progress, no cancel and no filename of ours — the file lands under the request path
    // as stream.mp4 because Jellyfin's stream endpoint sends no Content-Disposition.
    // The browser still needs a ticket, or the server will not pace (or slot) the
    // download. There is no queue here to wait in, so a busy server is reported instead.
    async function handOffWithTicket(request, deviceId, playSessionId) {
        let result;
        try {
            result = await requestTicket(request);
        } catch (err) {
            console.error('[TranscodeDownloader] ticket request failed:', err);
            showTransientNotice(t('DownloadFailed'));
            return;
        }
        if (result.status === 429) { showTransientNotice(busyNotice(result.body)); return; }
        if (result.status === 507) { showTransientNotice(t('NoSpace')); return; }
        if (result.status !== 200 || !result.body) {
            showTransientNotice(t('DownloadFailedStatus').replace('{status}', result.status));
            return;
        }
        const url = `${request.url}&TdTicket=${encodeURIComponent(readField(result.body, 'Ticket'))}`;
        handOffToBrowser(url, request.baseUrl, deviceId, playSessionId);
    }

    // The Jellyfin iOS app (jellyfin-expo) hosts jellyfin-web in a WKWebView that cannot
    // save a page-made download: the blob save at the end left the app on a black screen with no
    // file (2026-11-14). Its NativeShell.downloadFile() takes any URL, downloads it natively into
    // the app's Downloads, and keeps going with the page gone. ExpoAppInfo is only defined there.
    function isIosApp() {
        const shell = window.NativeShell;
        return !!(shell && typeof shell.downloadFile === 'function' && window.ExpoAppInfo);
    }

    async function handOffToIosApp(request, item, filename) {
        let result;
        try {
            result = await requestTicket(request);
        } catch (err) {
            console.error('[TranscodeDownloader] ticket request failed:', err);
            showTransientNotice(t('DownloadFailed'));
            return;
        }
        if (result.status === 429) { showTransientNotice(busyNotice(result.body)); return; }
        if (result.status === 507) { showTransientNotice(t('NoSpace')); return; }
        if (result.status !== 200 || !result.body) {
            showTransientNotice(t('DownloadFailedStatus').replace('{status}', result.status));
            return;
        }
        // The app refuses a URL without an ApiKey parameter, but downloading it needs none:
        // Jellyfin serves /Videos/{id}/stream without auth. A placeholder keeps the user's token
        // out of proxy and server logs. The app only uses the key for its own "Transcoded
        // Downloads" path, which then fails visibly instead of running an unpaced transcode.
        const url = `${request.url}&TdTicket=${encodeURIComponent(readField(result.body, 'Ticket'))}&ApiKey=unused`;
        // Without Path the app names the file from `filename` (ending .mp4) instead of the
        // original's extension (usually .mkv), and files it under ServerId/ItemId.
        const appItem = Object.assign({}, item);
        delete appItem.Path;
        try {
            window.NativeShell.downloadFile({ item: appItem, url, filename });
        } catch (err) {
            console.error('[TranscodeDownloader] iOS app hand-off failed:', err);
            showTransientNotice(t('DownloadFailed'));
            return;
        }
        showTransientNotice(t('SentToAppDownloads'));
    }

    function handOffToBrowser(url, baseUrl, deviceId, playSessionId) {
        try {
            window.NativeShell.openUrl(url);
        } catch (err) {
            console.error('[TranscodeDownloader] browser handoff failed:', err);
            showTransientNotice(t('BrowserHandoffFailed'));
            return;
        }
        rememberPendingStop(baseUrl, deviceId, playSessionId);
        showTransientNotice(t('BrowserHandoffStarted'));
    }

    // The queue panel only renders queued entries, and a handoff never becomes one — this is the
    // only feedback the user gets that anything happened at all.
    function showTransientNotice(text) {
        const existing = document.getElementById('qd-notice');
        if (existing) existing.remove();

        const notice = document.createElement('div');
        notice.id = 'qd-notice';
        notice.textContent = text;
        // left+right with margin-left:auto keeps it hugging the right edge on a desktop viewport
        // without letting it overflow the left edge of a phone narrower than max-width.
        notice.style.cssText = 'position:fixed;bottom:16px;left:16px;right:16px;max-width:400px;margin-left:auto;'
            + 'background:#1a1a1a;border:1px solid #444;border-radius:6px;color:#fff;font-size:13px;'
            + 'font-family:monospace;padding:12px 16px;z-index:9999;';
        document.body.appendChild(notice);
        setTimeout(() => notice.remove(), 8000);
    }

    function triggerBlobDownload(blob, filename) {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
    }

    // --- Item metadata ---

    function extractItemId(hash) {
        const queryStart = hash.indexOf('?');
        if (queryStart === -1) return null;
        const params = new URLSearchParams(hash.slice(queryStart));
        return params.get('id');
    }

    // Item ids are 32 hex digits; menu entries carry a command name in the same attribute
    // (`data-id="download"`), so every candidate has to be shape-checked before it is trusted.
    function isItemId(value) {
        return typeof value === 'string' && /^[0-9a-f]{32}$/i.test(value.replace(/-/g, ''));
    }

    // The item the *next* action sheet will act on, or null when the trigger carried no item.
    let contextMenuItemId = null;

    // An action sheet is not always about the item in the address bar: the episode rows of a
    // season or series page each open one for their own episode, and Jellyfin resolves that
    // item from the closest ancestor carrying data-id (shortcuts.js → getItem). Mirroring that
    // lookup keeps our entry pointed at the same item Jellyfin's own Download entry uses;
    // without it a season page downloads /Videos/{seasonId}/… and the server throws.
    function recordContextMenuTarget(event) {
        const target = event.target;
        const holder = target && typeof target.closest === 'function' ? target.closest('[data-id]') : null;
        const id = holder ? holder.getAttribute('data-id') : null;
        contextMenuItemId = isItemId(id) ? id : null;
    }

    // Consumed on injection: a sheet opened by anything we did not see (so the id would be
    // stale) falls back to the address bar, which is right for detail pages — their More button
    // has no data-id ancestor at all.
    function takeContextMenuItemId() {
        const id = contextMenuItemId;
        contextMenuItemId = null;
        return id;
    }

    function getApiClient(attempt, maxAttempts, callback, onFail) {
        if (window.ApiClient && window.ApiClient.accessToken && window.ApiClient.getCurrentUserId) {
            callback(window.ApiClient);
        } else if (attempt < maxAttempts) {
            setTimeout(() => getApiClient(attempt + 1, maxAttempts, callback, onFail), 500);
        } else {
            console.error('[TranscodeDownloader] ApiClient not available after retries');
            if (onFail) onFail();
        }
    }

    // --- Codec capabilities ---

    // Jellyfin serialises with PascalCase; tolerate camelCase so the contract survives a
    // serializer change without breaking the client.
    function readField(obj, name) {
        if (!obj) return undefined;
        if (obj[name] !== undefined) return obj[name];
        return obj[name.charAt(0).toLowerCase() + name.slice(1)];
    }

    function parseCapabilityList(list) {
        const map = {};
        (list || []).forEach((entry) => {
            const id = readField(entry, 'Codec');
            if (!id) return;
            map[String(id).toLowerCase()] = {
                encoder: readField(entry, 'Encoder') || '',
                hardware: readField(entry, 'IsHardware') === true,
                supported: readField(entry, 'IsSupported') === true,
                allowed: readField(entry, 'IsAllowedByAdmin') === true,
            };
        });
        return map;
    }

    function readStoredCodec(key) {
        try {
            return window.localStorage.getItem(key);
        } catch (_) {
            return null;
        }
    }

    function storeCodec(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch (_) {
            // localStorage can be unavailable (private mode, sandboxed iframe) — preference
            // simply won't stick.
        }
    }

    // Why a codec cannot be used, or null when it can.
    // Apple's own players (Files, QuickTime, Photos) play HEVC in MP4 only when it is
    // tagged hvc1, and Jellyfin writes progressive HEVC as hev1 (checked 2026-11-14: a download
    // opened from the iOS app's Downloads played only in VLC). Opus in MP4 is the same. Jellyfin
    // has no parameter to change the tag, so Apple devices get the formats they can play.
    const APPLE_PLAYABLE = { video: ['h264'], audio: ['aac'] };

    function isApplePlayback() {
        const ua = navigator.userAgent || '';
        // iPadOS Safari reports itself as a Mac; touch support gives it away.
        return isIosApp() || /iPhone|iPad|iPod/.test(ua)
            || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    }

    function codecBlockReason(kind, id) {
        if (isApplePlayback() && !APPLE_PLAYABLE[kind].includes(id)) return 'apple';
        if (!codecCapabilities) return null;
        const capability = codecCapabilities[kind][id];
        if (!capability) return 'unsupported';
        if (!capability.allowed) return 'admin';
        if (!capability.supported) return 'unsupported';
        return null;
    }

    function codecReasonText(reason) {
        if (reason === 'apple') return t('CodecNotApplePlayable');
        return reason === 'admin' ? t('CodecDisabledByAdmin') : t('CodecUnsupported');
    }

    function isCodecSelectable(kind, id) {
        const catalogue = kind === 'video' ? VIDEO_CODECS : AUDIO_CODECS;
        return !!findCodec(catalogue, id) && codecBlockReason(kind, id) === null;
    }

    function firstSelectableCodec(kind) {
        const catalogue = kind === 'video' ? VIDEO_CODECS : AUDIO_CODECS;
        const codec = catalogue.find(candidate => isCodecSelectable(kind, candidate.id));
        return codec ? codec.id : null;
    }

    // A stored preference is only honoured if the server still offers that codec.
    function resolveCodecPreference(kind, storageKey, fallback) {
        const stored = readStoredCodec(storageKey);
        if (stored && isCodecSelectable(kind, stored)) return stored;
        if (isCodecSelectable(kind, fallback)) return fallback;
        return firstSelectableCodec(kind) || fallback;
    }

    function selectCodec(kind, id) {
        if (kind === 'video') {
            selectedVideoCodec = id;
            storeCodec(STORAGE_VIDEO_CODEC, id);
        } else {
            selectedAudioCodec = id;
            storeCodec(STORAGE_AUDIO_CODEC, id);
        }
    }

    // Fetched lazily on first quality-sheet open. Only a *successful* probe is memoised for
    // the page session: a failed one drops the cached promise so the next sheet retries,
    // rather than hiding the picker until the tab is reloaded.
    function fetchCodecCapabilities() {
        if (codecCapabilitiesPromise) return codecCapabilitiesPromise;

        let probeFailed = false;

        const probe = new Promise((resolve) => {
            const giveUp = (err) => {
                console.warn('[TranscodeDownloader] Codec capabilities unavailable, falling back to H.264/AAC:', err);
                codecCapabilities = null;
                codecCapabilitiesResolved = false;
                selectedVideoCodec = DEFAULT_VIDEO_CODEC;
                selectedAudioCodec = DEFAULT_AUDIO_CODEC;
                probeFailed = true;
                resolve();
            };

            getApiClient(0, 10, async (client) => {
                try {
                    const baseUrl = client.serverAddress() || window.location.origin;
                    const res = await fetch(`${baseUrl}/TranscodeDownloader/Codecs`, { headers: authHeaders(client.accessToken()) });
                    if (!res.ok) throw new Error(`HTTP ${res.status}`);
                    const payload = await res.json();

                    codecCapabilities = {
                        video: parseCapabilityList(readField(payload, 'VideoCodecs')),
                        audio: parseCapabilityList(readField(payload, 'AudioCodecs')),
                    };
                    codecCapabilitiesResolved = true;
                    const limits = readField(payload, 'Limits');
                    downloadLimits = limits
                        ? { paced: !!readField(limits, 'Paced'), rateBps: Number(readField(limits, 'RateBps')) || 0 }
                        : null;
                    selectedVideoCodec = resolveCodecPreference('video', STORAGE_VIDEO_CODEC, DEFAULT_VIDEO_CODEC);
                    selectedAudioCodec = resolveCodecPreference('audio', STORAGE_AUDIO_CODEC, DEFAULT_AUDIO_CODEC);
                    resolve();
                } catch (err) {
                    giveUp(err);
                }
            }, () => giveUp(new Error('ApiClient unavailable')));
        });

        codecCapabilitiesPromise = probe;

        // Runs as a microtask, so it is ordered after the assignment above even when the
        // probe fails synchronously inside the executor.
        probe.then(() => {
            if (probeFailed && codecCapabilitiesPromise === probe) codecCapabilitiesPromise = null;
        });

        return probe;
    }

    let currentItemId = null;
    let currentItemPromise = null;

    function fetchItemMetadata(itemId) {
        return new Promise((resolve) => {
            getApiClient(0, 10, async (client) => {
                try {
                    const userId = client.getCurrentUserId();
                    resolve(await client.getItem(userId, itemId));
                } catch (err) {
                    console.error('[TranscodeDownloader] Metadata fetch failed:', err);
                    resolve(null);
                }
            }, () => resolve(null));
        });
    }

    // Prefetched for the item on screen; a sheet opened for any other item fetches its own.
    function itemMetadata(itemId) {
        if (itemId === currentItemId && currentItemPromise) return currentItemPromise;
        return fetchItemMetadata(itemId);
    }

    // A season, a series or a song has no video stream to re-encode. Jellyfin's own Download
    // entry sits next to ours in the same sheet for those items, so the sheet alone is no
    // guarantee that /Videos/{id}/stream.mp4 means anything — a season id makes the server
    // throw while it resolves media sources. An absent MediaType is treated as playable so a
    // sparse DTO never blocks a download that used to work.
    function isTranscodable(item) {
        if (!item || item.IsFolder === true) return false;
        return !item.MediaType || item.MediaType === 'Video';
    }

    function onHashChange() {
        const hash = window.location.hash;
        if (!hash.startsWith('#/details')) return;

        const itemId = extractItemId(hash);
        if (!itemId) return;

        currentItemId = itemId;
        currentItemPromise = fetchItemMetadata(itemId);
    }

    // --- Action sheet menu injection ---

    function makeMenuItem(icon, label, onClick) {
        const btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.setAttribute('type', 'button');
        btn.className = 'listItem listItem-button actionSheetMenuItem emby-button';
        btn.setAttribute('data-id', 'qd-' + label.replace(/\s+/g, '-').toLowerCase());

        const iconSpan = document.createElement('span');
        iconSpan.className = `actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ${icon}`;
        iconSpan.setAttribute('aria-hidden', 'true');

        const body = document.createElement('div');
        body.className = 'listItemBody actionsheetListItemBody';
        const text = document.createElement('div');
        text.className = 'listItemBodyText actionSheetItemText';
        text.textContent = label;
        body.appendChild(text);

        btn.appendChild(iconSpan);
        btn.appendChild(body);
        btn.addEventListener('click', onClick);
        return btn;
    }

    function injectMenuItems(downloadBtn) {
        if (downloadBtn.parentNode.querySelector('[data-id^="qd-"]')) return;

        // Bound now, while the sheet's trigger is still known — by the time the entry is
        // clicked the click target is the entry itself.
        const itemId = takeContextMenuItemId() || extractItemId(window.location.hash);
        if (!itemId) return;

        const transcodeBtn = makeMenuItem('video_settings', t('DownloadTranscode'), () => {
            closeActiveSheet();
            showQualitySheet(itemId);
        });

        downloadBtn.insertAdjacentElement('afterend', transcodeBtn);
    }

    function closeActiveSheet() {
        const backdrop = document.querySelector('.actionSheetScrim, .dialogBackdrop, .mdl-overlay');
        if (backdrop) backdrop.click();
    }

    // --- Codec selector ---

    function makeCodecChip(kind, codec, onSelect) {
        const reason = codecBlockReason(kind, codec.id);
        const selected = (kind === 'video' ? selectedVideoCodec : selectedAudioCodec) === codec.id;

        const chip = document.createElement('button');
        chip.type = 'button';
        chip.setAttribute('data-id', `qd-codec-${kind}-${codec.id}`);
        chip.textContent = codec.label;
        chip.style.cssText = 'padding:6px 14px;border-radius:16px;font-size:13px;font-family:inherit;color:#fff;'
            + `border:1px solid ${selected ? '#00a4dc' : '#444'};`
            + `background:${selected ? 'rgba(0,164,220,0.2)' : 'transparent'};`
            + (reason ? 'opacity:0.4;cursor:not-allowed;' : 'cursor:pointer;');

        if (reason) {
            chip.disabled = true;
            chip.title = codecReasonText(reason);
        } else {
            chip.setAttribute('aria-pressed', selected ? 'true' : 'false');
            chip.addEventListener('click', () => onSelect(codec.id));
        }

        return chip;
    }

    function buildCodecRow(catalogue, kind, label, onSelect) {
        const wrap = document.createElement('div');
        wrap.style.cssText = 'margin-bottom:10px;';

        const title = document.createElement('div');
        title.style.cssText = 'font-size:13px;font-weight:600;color:#fff;opacity:0.7;margin-bottom:6px;';
        title.textContent = label;
        wrap.appendChild(title);

        const chips = document.createElement('div');
        chips.style.cssText = 'display:flex;flex-wrap:wrap;gap:8px;';
        catalogue.forEach(codec => chips.appendChild(makeCodecChip(kind, codec, onSelect)));
        wrap.appendChild(chips);

        // Unavailable codecs stay visible with the reason spelled out — a silent absence is
        // impossible for a user to self-diagnose.
        catalogue.forEach((codec) => {
            const reason = codecBlockReason(kind, codec.id);
            if (!reason) return;
            const note = document.createElement('div');
            note.style.cssText = 'font-size:12px;color:#888;margin-top:6px;';
            note.textContent = `${codec.label} — ${codecReasonText(reason)}`;
            wrap.appendChild(note);
        });

        return wrap;
    }

    // Warn when the selected codec resolves to a software encoder. x264 is the status quo and
    // fast enough; libx265 and especially libsvtav1 can run for hours, and the whole file is
    // held in memory until the transcode finishes.
    function buildSoftwareEncodingHint() {
        if (!codecCapabilities || selectedVideoCodec === DEFAULT_VIDEO_CODEC) return null;
        const capability = codecCapabilities.video[selectedVideoCodec];
        if (!capability || capability.hardware) return null;

        const hint = document.createElement('div');
        hint.style.cssText = 'font-size:12px;color:#d0a24c;margin-top:2px;';
        hint.textContent = t('SoftwareEncodingHint');
        return hint;
    }

    function buildCodecSection(onCodecChange) {
        const section = document.createElement('div');
        section.style.cssText = 'padding:8px 20px 4px;';

        const render = () => {
            section.textContent = '';
            const onSelect = (kind) => (id) => {
                selectCodec(kind, id);
                render();
                onCodecChange();
            };
            section.appendChild(buildCodecRow(VIDEO_CODECS, 'video', t('ChooseVideoCodec'), onSelect('video')));
            section.appendChild(buildCodecRow(AUDIO_CODECS, 'audio', t('ChooseAudioCodec'), onSelect('audio')));
            const hint = buildSoftwareEncodingHint();
            if (hint) section.appendChild(hint);
        };

        render();
        return section;
    }

    // --- Quality overlay ---

    async function showQualitySheet(itemId) {
        if (document.getElementById('qd-quality-sheet')) return;

        // Ensure strings are loaded before rendering UI
        if (stringsPromise) await stringsPromise;

        const metadataPromise = itemMetadata(itemId);

        // Runs alongside the metadata fetch; memoised for the rest of the page session.
        const capabilitiesPromise = fetchCodecCapabilities();

        const scrim = document.createElement('div');
        scrim.id = 'qd-quality-sheet';
        scrim.style.cssText = 'position:fixed;inset:0;z-index:10000;display:flex;align-items:flex-end;justify-content:center;background:rgba(0,0,0,0.5);';
        scrim.addEventListener('click', (e) => { if (e.target === scrim) scrim.remove(); });

        const sheet = document.createElement('div');
        sheet.style.cssText = 'background:#1a1a1a;border-radius:12px 12px 0 0;width:100%;max-width:600px;max-height:80vh;overflow-y:auto;padding:8px 0 24px;';

        const header = document.createElement('div');
        header.style.cssText = 'padding:16px 20px 8px;font-size:15px;font-weight:600;color:#fff;opacity:0.7;';
        header.textContent = t('ChooseQuality');
        sheet.appendChild(header);

        const loadingEl = document.createElement('div');
        loadingEl.style.cssText = 'padding:16px 20px;color:#aaa;font-size:14px;';
        loadingEl.textContent = t('LoadingMediaInfo');
        sheet.appendChild(loadingEl);

        scrim.appendChild(sheet);
        document.body.appendChild(scrim);

        const item = await metadataPromise;
        await capabilitiesPromise;
        if (!document.getElementById('qd-quality-sheet')) return;

        loadingEl.remove();

        // A sheet can be opened for an item with nothing to transcode (a season, a series);
        // saying so beats firing a request the server can only fail. A metadata fetch that
        // failed outright is reported as "no options" instead of a wrong claim about the type.
        if (!isTranscodable(item)) {
            const notice = document.createElement('div');
            notice.style.cssText = 'padding:16px 20px;color:#aaa;font-size:14px;';
            notice.textContent = item ? t('ItemNotTranscodable') : t('NoTranscodeOptions');
            sheet.appendChild(notice);
            return;
        }

        const source = item && item.MediaSources && item.MediaSources[0];
        // Filtered on the H.264-calibrated base bitrate so the set of rungs on offer stays
        // stable when the codec changes — only their encoder targets move.
        const tiers = source && source.Bitrate
            ? QUALITY_TIERS.filter(tier => tier.bitrate < source.Bitrate)
            : QUALITY_TIERS;

        const tierList = document.createElement('div');

        const renderTiers = () => {
            tierList.textContent = '';
            const factor = videoCodecFactor(selectedVideoCodec);

            for (const tier of tiers) {
                const bitrate = scaleBitrate(tier.bitrate, factor);
                const btn = makeMenuItem('video_settings', `${tier.resolution} · ${formatBitrate(bitrate)}${tierEstimate(bitrate, item)}`, () => {
                    scrim.remove();
                    onTranscodeMenuClick(bitrate, item.Id || itemId, item);
                });
                tierList.appendChild(btn);
            }

            if (tiers.length === 0) {
                const empty = document.createElement('div');
                empty.style.cssText = 'padding:16px 20px;color:#aaa;font-size:14px;';
                empty.textContent = t('NoTranscodeOptions');
                tierList.appendChild(empty);
            }
        };

        // Only offered when the server told us what it can encode; otherwise the flow stays
        // exactly as it was, on H.264/AAC.
        if (codecCapabilitiesResolved) {
            sheet.insertBefore(buildCodecSection(renderTiers), header);
        }

        // Said before the choice, not after: picking a tier in the app leaves for the browser
        // immediately, and a transcode that surprises the user has already cost server time.
        // With the app's "Transcoded Downloads" switch on, the app ignores the URL it is
        // handed and runs its own unpaced transcode at a quality it picks (seen 2026-11-14:
        // 2.4 GB at ~171 Mbps for a 164 MB pick). The page cannot read that setting.
        if (isIosApp()) {
            const appHint = document.createElement('div');
            appHint.style.cssText = 'padding:0 20px 8px;font-size:12px;color:#d0a24c;';
            appHint.textContent = t('IosAppHint');
            sheet.appendChild(appHint);
        }

        if (isAndroidAppWebView()) {
            const handoffHint = document.createElement('div');
            handoffHint.style.cssText = 'padding:0 20px 8px;font-size:12px;color:#d0a24c;';
            handoffHint.textContent = t('BrowserHandoffHint');
            sheet.appendChild(handoffHint);
        }

        renderTiers();
        sheet.appendChild(tierList);
    }

    // --- Download actions ---

    function onTranscodeMenuClick(bitrate, itemId, item) {
        if (!item) return;
        // Captured now: the sheet is gone, but the selection must not drift before the
        // ApiClient callback runs.
        const videoCodec = selectedVideoCodec;
        const audioCodec = selectedAudioCodec;
        getApiClient(0, 5, (client) => {
            const token = client.accessToken();
            const baseUrl = client.serverAddress() || window.location.origin;
            addToQueue(baseUrl, itemId, token, bitrate, item, videoCodec, audioCodec);
        });
    }

    function filenameFor(item, videoCodec, audioCodec, bitrate) {
        const suffix = ` [${codecTag(VIDEO_CODECS, videoCodec)} ${formatBitrate(bitrate).replace(' ', '')} ${codecTag(AUDIO_CODECS, audioCodec)}]`;
        const pad = (n) => String(n).padStart(2, '0');
        if (item.Type === 'Movie') return `${item.Name} (${item.ProductionYear})${suffix}.mp4`;
        if (item.Type === 'Episode') {
            return `${item.SeriesName} S${pad(item.ParentIndexNumber)}E${pad(item.IndexNumber)} - ${item.Name}${suffix}.mp4`;
        }
        return `download${suffix}.mp4`;
    }

    function addToQueue(baseUrl, itemId, token, selectedBitrate, item, videoCodec, audioCodec) {
        const mediaSourceId = item.MediaSources && item.MediaSources[0] && item.MediaSources[0].Id
            ? item.MediaSources[0].Id
            : itemId;
        // Identifies the server-side transcoding job so it can be killed (and its temp file
        // deleted) once the download ends. Both are per-download and synthetic: the pair also
        // seeds the transcode's output filename, so a fresh id keeps a new download from picking
        // up a stale file, and a device id of our own keeps this out of the real playback
        // session's transcoding info in the dashboard.
        const playSessionId = randomId();
        const deviceId = `transcode-downloader-${playSessionId}`;
        // No token in the URL. The fetch below sends the Authorization header, and on
        // Jellyfin 12.3 /Videos/{id}/stream answers without one anyway (checked 2026-11-14),
        // which is also what the Android browser handoff relies on. Keeping the token out of
        // the URL keeps it out of Traefik's access log and the browser's history.
        // A single codec is sent deliberately: a comma-separated list lets the server silently
        // downgrade while the UI still claims the codec the user picked.
        const url = `${baseUrl}/Videos/${itemId}/stream.mp4?MediaSourceId=${mediaSourceId}&VideoBitrate=${selectedBitrate}&VideoCodec=${videoCodec}&AudioCodec=${audioCodec}&MaxAudioChannels=2&allowVideoStreamCopy=false&allowAudioStreamCopy=false&Static=false&PlaySessionId=${playSessionId}&DeviceId=${encodeURIComponent(deviceId)}`;
        // Nothing below this point applies inside the Android app: there is no queue entry, no
        // progress and no filename of ours once the browser owns the transfer.
        if (isAndroidAppWebView()) {
            handOffWithTicket({ baseUrl, itemId, token, bitrate: selectedBitrate, url }, deviceId, playSessionId);
            return;
        }
        if (isIosApp()) {
            handOffToIosApp({ baseUrl, itemId, token, bitrate: selectedBitrate, url }, item, filenameFor(item, videoCodec, audioCodec, selectedBitrate));
            return;
        }

        const stopUrl = buildStopUrl(baseUrl, deviceId, playSessionId);

        const videoTag = codecTag(VIDEO_CODECS, videoCodec);
        const audioTag = codecTag(AUDIO_CODECS, audioCodec);
        const filename = filenameFor(item, videoCodec, audioCodec, selectedBitrate);

        const durationSeconds = item.RunTimeTicks / 10_000_000;
        const estimatedBytes = (selectedBitrate * durationSeconds) / 8;

        enqueue({
            id: `${itemId}-${Date.now()}`,
            filename,
            estimatedBytes,
            url,
            stopUrl,
            token,
            baseUrl,
            itemId,
            bitrate: selectedBitrate,
            startedAt: 0,
            videoCodec,
            audioCodec,
            codecLabel: `${videoTag} · ${formatBitrate(selectedBitrate)} · ${audioTag}`,
            receivedBytes: 0,
            abortController: null,
            status: 'waiting',
        });
    }

    // Capture phase, so the trigger is recorded before Jellyfin's own handler opens the sheet.
    document.addEventListener('click', recordContextMenuTarget, true);
    document.addEventListener('contextmenu', recordContextMenuTarget, true);

    const _menuObserver = new MutationObserver(() => {
        const downloadBtn = document.querySelector('.actionSheetMenuItem[data-id="download"]');
        if (downloadBtn) injectMenuItems(downloadBtn);
    });
    _menuObserver.observe(document.body, { childList: true, subtree: true });

    console.log('[TranscodeDownloader] plugin loaded');
    window.addEventListener('hashchange', onHashChange);

    if (window.location.hash.startsWith('#/details')) {
        onHashChange();
    }

    injectQueuePanel();
    initStrings();
    flushPendingStops();

    // Re-fetch strings whenever Jellyfin changes the UI language (updates document.documentElement.lang).
    new MutationObserver(() => {
        const locale = document.documentElement.lang;
        if (!locale) return;
        fetchStrings(locale).then(result => {
            if (result) {
                loadedStrings = result.strings;
                console.log('[TranscodeDownloader] strings reloaded for locale:', locale);
            }
        }).catch(() => {});
    }).observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });
})();
