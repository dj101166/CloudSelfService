// Chunked, resumable uploads to /api/uploads (see Endpoints/UploadEndpoints.cs). File bytes never pass through the
// Blazor circuit: the page only learns about progress through the throttled callbacks below.
//
// CSP-safe: nothing evaluated from strings, no inline handlers, no HTML written from strings; every request is same-origin.
// Requires spark-md5 (wwwroot/lib/spark-md5) loaded first, because WebCrypto has no MD5.
(function () {
    'use strict';

    var RETRY_DELAYS = [1000, 2000, 4000, 8000, 16000];
    var MAX_BUSY_WAITS = 10;
    var DEFAULT_RETRY_AFTER_SECONDS = 5;
    // The first-piece fingerprint is always the file's first 4 MiB (ChunkMath.FingerprintBytes), whatever piece size
    // Begin later returns: it is hashed before Begin, and a resumed session must match on the same bytes.
    var FINGERPRINT_BYTES = 4194304;
    var MAX_RETRY_AFTER_SECONDS = 300;
    var MAX_RESTARTS = 3;
    var PROGRESS_INTERVAL_MS = 250;
    var ACTIVITY_INTERVAL_MS = 60000;

    var SESSION_EXPIRED = 'Your session has expired. Sign in again, then select the file again to resume.';
    var INTERRUPTED = 'The connection dropped. Select Resume, or select the same file again, to carry on where it stopped.';
    var IN_PROGRESS = 'Someone else is uploading this file right now.';
    var GAVE_UP = 'The upload kept being restarted by the server. Try again later.';

    var uploads = {};
    var queue = [];
    var running = null;
    var counter = 0;
    var tokenPromise = null;
    var dirty = {};
    var flushTimer = null;
    var lastActivity = 0;
    var antiforgeryMessage = null;

    function UploadError(kind, message, code, retryAfter) {
        this.kind = kind;
        this.message = message || '';
        this.code = code || '';
        this.retryAfter = retryAfter;
    }

    function sleep(ms, signal) {
        return new Promise(function (resolve, reject) {
            if (signal && signal.aborted) { reject(new UploadError('aborted')); return; }
            var t = setTimeout(function () {
                if (signal) { signal.removeEventListener('abort', onAbort); }
                resolve();
            }, ms);
            function onAbort() { clearTimeout(t); reject(new UploadError('aborted')); }
            if (signal) { signal.addEventListener('abort', onAbort, { once: true }); }
        });
    }

    function notify(u, method) {
        var args = Array.prototype.slice.call(arguments, 2);
        if (!u.ref) { return; }
        u.ref.invokeMethodAsync.apply(u.ref, [method, u.id].concat(args)).catch(function () {
            // The page went away; the upload is suspended by detach().
        });
    }

    function scheduleProgress(u) {
        dirty[u.id] = true;
        if (flushTimer) { return; }
        flushTimer = setTimeout(flushProgress, PROGRESS_INTERVAL_MS);
    }

    function flushProgress() {
        flushTimer = null;
        var ids = Object.keys(dirty);
        dirty = {};
        ids.forEach(function (id) {
            var u = uploads[id];
            if (u) { notify(u, 'OnProgress', u.file.name, u.sent, u.file.size); }
        });
    }

    function setStatus(u, status, message) {
        u.status = status;
        notify(u, 'OnStatus', status, message || null);
    }

    // session-guard.js signs out after a stretch with no mouse or keyboard input, which a multi-hour upload can outlast.
    // While pieces are landing this counts as activity, at most once a minute. session-guard.js is shared by the whole
    // suite and has no public hook, so this raises one of the events it already listens for; its handler resets the idle
    // timer and slides the cookie through /keepalive.
    function markActivity() {
        var now = Date.now();
        if (now - lastActivity < ACTIVITY_INTERVAL_MS) { return; }
        lastActivity = now;
        document.dispatchEvent(new Event('scroll'));
    }

    function getToken() {
        if (!tokenPromise) {
            tokenPromise = fetch('/api/antiforgery', { credentials: 'same-origin', redirect: 'manual', cache: 'no-store' })
                .then(function (r) {
                    if (r.type === 'opaqueredirect' || r.status === 401) { throw new UploadError('expired', SESSION_EXPIRED); }
                    if (!r.ok) { throw new UploadError('transient', 'Could not prepare the upload.', 'unavailable'); }
                    return r.json();
                })
                .then(function (body) {
                    return { header: body.headerName || 'RequestVerificationToken', value: body.token };
                })
                .catch(function (e) {
                    tokenPromise = null;
                    throw e instanceof UploadError ? e : new UploadError('transient', 'Could not prepare the upload.', 'unavailable');
                });
        }
        return tokenPromise;
    }

    // Every API call goes through here, so each failure arrives as one UploadError whose kind says what to do next.
    // A token the server no longer accepts (the antiforgery cookie was rotated, e.g. by signing in again in another tab)
    // is fetched afresh and the call tried once more.
    function api(method, url, options, signal) {
        return apiOnce(method, url, options, signal).catch(function (e) {
            if (e instanceof UploadError && e.code === 'invalid' && antiforgeryMessage && e.message === antiforgeryMessage) {
                tokenPromise = null;
                return apiOnce(method, url, options, signal);
            }
            throw e;
        });
    }

    function apiOnce(method, url, options, signal) {
        return getToken().then(function (token) {
            var headers = {};
            headers[token.header] = token.value;
            var init = { method: method, credentials: 'same-origin', redirect: 'manual', cache: 'no-store', headers: headers, signal: signal };
            if (options && options.json !== undefined) {
                headers['Content-Type'] = 'application/json';
                init.body = JSON.stringify(options.json);
            } else if (options && options.body !== undefined) {
                headers['Content-Type'] = 'application/octet-stream';
                headers['Content-MD5'] = options.md5;
                init.body = options.body;
            }
            return fetch(url, init).then(function (r) {
                return classify(r);
            }, function () {
                if (signal && signal.aborted) { throw new UploadError('aborted'); }
                throw new UploadError('transient', INTERRUPTED, 'network');
            });
        });
    }

    function classify(r) {
        // A cookie that expired answers with a redirect to the sign-in page, or a bare 401 with no JSON.
        if (r.type === 'opaqueredirect' || r.status === 401 || (r.status >= 300 && r.status < 400)) {
            throw new UploadError('expired', SESSION_EXPIRED);
        }
        if (r.status === 204) { return null; }
        return r.text().then(function (text) {
            var body = null;
            try { body = text ? JSON.parse(text) : null; } catch (e) { body = null; }
            if (r.ok) { return body; }

            var code = body && typeof body.code === 'string' ? body.code : '';
            var message = body && typeof body.error === 'string' && body.error ? body.error : 'The upload failed (' + r.status + ').';
            if (!code) {
                if (r.status === 429) { code = 'busy'; }
                else if (r.status >= 500) { code = 'unavailable'; }
                else if (r.status === 403) { code = 'denied'; }
                else if (r.status === 404) { code = 'not_found'; }
                else if (r.status === 413) { code = 'too_large'; }
                else { code = 'invalid'; }
            }
            var retryAfter = parseInt(r.headers.get('Retry-After') || '', 10);
            retryAfter = isNaN(retryAfter) ? DEFAULT_RETRY_AFTER_SECONDS : Math.min(retryAfter, MAX_RETRY_AFTER_SECONDS);
            throw toError(code, message, retryAfter);
        });
    }

    function toError(code, message, retryAfter) {
        switch (code) {
            case 'exists': return new UploadError('conflict', message, code);
            case 'in_progress': return new UploadError('fatal', IN_PROGRESS, code);
            case 'restart': return new UploadError('restart', message, code);
            case 'not_found': return new UploadError('restart', message, code);
            case 'retry': return new UploadError('transient', message, code);
            case 'unavailable': return new UploadError('transient', message, code);
            case 'busy': return new UploadError('busy', message, code, retryAfter);
            case 'wait': return new UploadError('wait', message, code, retryAfter);
            case 'denied': return new UploadError('denied', message, code);
            case 'invalid': return new UploadError('fatal', message, code);
            case 'too_large': return new UploadError('fatal', message, code);
            default: return new UploadError('fatal', message, code);
        }
    }

    // Retries transient failures on the agreed backoff. A busy answer waits Retry-After and only starts counting as an
    // attempt after MAX_BUSY_WAITS of them. A wait answer (the server's memory for large pieces is full) is no failure at
    // all: it waits Retry-After for as long as it takes, until the piece goes or the upload is aborted, and clears the
    // busy count so busy answers met during a long wait never add up to a failure.
    function withRetry(u, work, signal) {
        var attempt = 0;
        var busyWaits = 0;
        function run() {
            return work().catch(function (e) {
                if (!(e instanceof UploadError)) { e = new UploadError('transient', INTERRUPTED, 'network'); }
                if (e.kind === 'wait') {
                    busyWaits = 0;
                    setStatus(u, 'waiting', e.message);
                    return sleep(Math.max(1, e.retryAfter) * 1000, signal).then(resumeStatus).then(run);
                }
                if (e.kind === 'busy') {
                    busyWaits++;
                    if (busyWaits > MAX_BUSY_WAITS) {
                        if (attempt >= RETRY_DELAYS.length) { throw new UploadError('interrupted', e.message, e.code); }
                        attempt++;
                    }
                    setStatus(u, 'waiting', e.message);
                    return sleep(Math.max(1, e.retryAfter) * 1000, signal).then(resumeStatus).then(run);
                }
                if (e.kind !== 'transient') { throw e; }
                if (attempt >= RETRY_DELAYS.length) { throw new UploadError('interrupted', INTERRUPTED, e.code); }
                var delay = RETRY_DELAYS[attempt++];
                setStatus(u, 'waiting', e.message);
                return sleep(delay, signal).then(resumeStatus).then(run);
            });
        }
        function resumeStatus() { setStatus(u, 'uploading'); }
        return run();
    }

    function base64Md5(buffer) {
        return btoa(SparkMD5.ArrayBuffer.hash(buffer, true));
    }

    function chunkLength(u, index) {
        return Math.max(0, Math.min(u.chunkSize, u.file.size - index * u.chunkSize));
    }

    function putChunk(u, index, signal) {
        var start = index * u.chunkSize;
        var blob = u.file.slice(start, start + chunkLength(u, index));
        return withRetry(u, function () {
            // Read inside the retry: a file the user changed on disk fails the read, which is not worth retrying.
            return blob.arrayBuffer().then(function (buffer) {
                return api('PUT', '/api/uploads/' + u.sessionId + '/chunks/' + index,
                    { body: buffer, md5: base64Md5(buffer) }, signal);
            }, function () {
                throw new UploadError('fatal', 'The file could not be read. It may have been changed or moved; select it again.');
            });
        }, signal).then(function () {
            u.sent += chunkLength(u, index);
            markActivity();
            scheduleProgress(u);
        });
    }

    // The first piece's MD5, sent at Begin so the server resumes a session only for the same file, not merely one with
    // the same name and size. An empty file has no piece and sends none.
    function firstPieceMd5(u) {
        if (u.firstPieceMd5 !== undefined) { return Promise.resolve(u.firstPieceMd5); }
        if (u.file.size === 0) { u.firstPieceMd5 = null; return Promise.resolve(null); }
        return u.file.slice(0, Math.min(FINGERPRINT_BYTES, u.file.size)).arrayBuffer().then(function (buffer) {
            u.firstPieceMd5 = base64Md5(buffer);
            return u.firstPieceMd5;
        }, function () {
            throw new UploadError('fatal', 'The file could not be read. It may have been changed or moved; select it again.');
        });
    }

    function begin(u, signal) {
        return firstPieceMd5(u).then(function (md5) {
            return withRetry(u, function () {
                var body = { targetId: u.targetId, folder: u.folder, fileName: u.file.name, size: u.file.size, overwrite: u.overwrite };
                if (md5) { body.firstPieceMd5 = md5; }
                return api('POST', '/api/uploads', { json: body }, signal);
            }, signal);
        }).catch(function (e) {
            // Begin has no session to lose, so restart / not_found here would only loop.
            if (e instanceof UploadError && e.kind === 'restart') { throw new UploadError('fatal', e.message, e.code); }
            throw e;
        });
    }

    function sendChunks(u, pending, parallel, signal) {
        var next = 0;
        var failed = null;
        var inner = new AbortController();
        function onAbort() { inner.abort(); }
        signal.addEventListener('abort', onAbort, { once: true });
        function worker() {
            if (failed || next >= pending.length) { return Promise.resolve(); }
            var index = pending[next++];
            return putChunk(u, index, inner.signal).then(worker, function (e) {
                if (!failed) { failed = e; inner.abort(); }
            });
        }
        var workers = [];
        for (var i = 0; i < Math.max(1, Math.min(parallel, pending.length)); i++) { workers.push(worker()); }
        return Promise.all(workers).then(function () {
            signal.removeEventListener('abort', onAbort);
            if (signal.aborted) { throw new UploadError('aborted'); }
            if (failed) { throw failed; }
        });
    }

    function complete(u, signal) {
        return withRetry(u, function () {
            return api('POST', '/api/uploads/' + u.sessionId + '/complete', null, signal);
        }, signal);
    }

    // One pass: Begin (which resumes an open session for the same file and size), send what is missing, Complete.
    // restart / not_found mean the server ended the session, so the pass starts again from a fresh Begin.
    function runOnce(u, signal) {
        setStatus(u, 'uploading');
        return begin(u, signal).then(function (session) {
            u.sessionId = session.sessionId;
            u.chunkSize = session.chunkSize;
            var received = {};
            (session.received || []).forEach(function (i) { received[i] = true; });
            var pending = [];
            u.sent = 0;
            for (var i = 0; i < session.chunkCount; i++) {
                if (received[i]) { u.sent += chunkLength(u, i); } else { pending.push(i); }
            }
            scheduleProgress(u);
            return sendChunks(u, pending, session.parallelChunks || 4, signal);
        }).then(function () {
            setStatus(u, 'finishing');
            return complete(u, signal);
        });
    }

    function runFile(u) {
        var controller = new AbortController();
        u.controller = controller;
        var restarts = 0;
        function attempt() {
            return runOnce(u, controller.signal).catch(function (e) {
                if (e instanceof UploadError && e.kind === 'restart' && !controller.signal.aborted) {
                    restarts++;
                    u.sessionId = null;
                    if (restarts > MAX_RESTARTS) { throw new UploadError('fatal', e.message || GAVE_UP); }
                    return attempt();
                }
                throw e;
            });
        }
        return attempt().then(function () {
            u.sent = u.file.size;
            finish(u, true, null);
        }, function (e) {
            if (!(e instanceof UploadError)) { e = new UploadError('interrupted', INTERRUPTED); }
            if (e.kind === 'aborted') { return; }
            if (e.kind === 'conflict') {
                setStatus(u, 'conflict', e.message);
                notify(u, 'OnConflict', u.file.name);
                return;
            }
            if (e.kind === 'expired') {
                expireAll();
                return;
            }
            if (e.kind === 'interrupted' || e.kind === 'transient') {
                setStatus(u, 'interrupted', e.message || INTERRUPTED);
                return;
            }
            if (e.kind === 'denied') {
                notify(u, 'OnDenied');
            }
            finish(u, false, e.message);
        });
    }

    function finish(u, ok, message) {
        u.status = ok ? 'done' : 'failed';
        flushNow(u);
        notify(u, 'OnFileDone', u.file.name, ok, message);
    }

    function flushNow(u) {
        delete dirty[u.id];
        notify(u, 'OnProgress', u.file.name, u.sent, u.file.size);
    }

    function expireAll() {
        Object.keys(uploads).forEach(function (id) {
            var u = uploads[id];
            if (u.status === 'done' || u.status === 'failed') { return; }
            if (u.controller) { u.controller.abort(); }
            removeFromQueue(u);
            finish(u, false, SESSION_EXPIRED);
        });
    }

    function removeFromQueue(u) {
        var i = queue.indexOf(u);
        if (i >= 0) { queue.splice(i, 1); }
    }

    // Files go one at a time, each with parallelChunks pieces in flight: the server limits a user's concurrent
    // requests, and one file finishing early is more useful than several all half done.
    function pump() {
        if (running || queue.length === 0) { return; }
        var u = queue.shift();
        running = u;
        runFile(u).then(function () {
            u.controller = null;
            running = null;
            pump();
        });
    }

    function enqueue(u) {
        removeFromQueue(u);
        queue.push(u);
        setStatus(u, 'queued');
        pump();
    }

    function isActive(u) {
        return u.status === 'queued' || u.status === 'uploading' || u.status === 'waiting' || u.status === 'finishing';
    }

    function sameFile(u, owner, targetId, folder, file) {
        return u.owner === owner && u.targetId === targetId && u.folder === folder && u.file.name === file.name
            && u.file.size === file.size && u.file.lastModified === file.lastModified;
    }

    // ownerKey identifies the calling component across calls: Blazor hands JS a fresh wrapper for the same
    // DotNetObjectReference on every interop call, so the reference itself cannot be compared.
    // antiforgeryRejectedMessage is UploadEndpoints.AntiforgeryMessage, so a stale token is told apart from other 400s.
    function start(dotnetRef, targetId, folder, inputElementId, overwrite, ownerKey, antiforgeryRejectedMessage) {
        var owner = ownerKey || '';
        if (antiforgeryRejectedMessage) { antiforgeryMessage = antiforgeryRejectedMessage; }
        var input = document.getElementById(inputElementId);
        if (!input || !input.files) { return []; }
        var files = Array.prototype.slice.call(input.files);
        // Cleared so that choosing the same file again fires change again, which is how a user resumes.
        input.value = '';
        var result = [];
        files.forEach(function (file) {
            var existing = null;
            Object.keys(uploads).forEach(function (id) {
                if (sameFile(uploads[id], owner, targetId, folder, file) && uploads[id].status !== 'done') { existing = uploads[id]; }
            });
            if (existing) {
                existing.file = file;
                existing.firstPieceMd5 = undefined;
                existing.ref = dotnetRef;
                if (!isActive(existing)) { existing.overwrite = !!overwrite; enqueue(existing); }
                result.push({ id: existing.id, name: file.name, size: file.size, existing: true });
                return;
            }
            var u = {
                id: 'u' + (++counter), ref: dotnetRef, owner: owner, targetId: targetId, folder: folder || '', file: file,
                overwrite: !!overwrite, sent: 0, status: 'new', sessionId: null, chunkSize: 0, controller: null
            };
            uploads[u.id] = u;
            result.push({ id: u.id, name: file.name, size: file.size, existing: false });
            enqueue(u);
        });
        return result;
    }

    function pause(id) {
        var u = uploads[id];
        if (!u || !isActive(u)) { return; }
        removeFromQueue(u);
        if (u.controller) { u.controller.abort(); }
        setStatus(u, 'paused');
    }

    function resume(id) {
        var u = uploads[id];
        if (!u || isActive(u) || u.status === 'done') { return; }
        enqueue(u);
    }

    function overwrite(id) {
        var u = uploads[id];
        if (!u || u.status === 'done') { return; }
        u.overwrite = true;
        enqueue(u);
    }

    function cancel(id) {
        var u = uploads[id];
        if (!u) { return; }
        removeFromQueue(u);
        if (u.controller) { u.controller.abort(); }
        var sessionId = u.sessionId;
        u.sessionId = null;
        delete uploads[id];
        if (sessionId) {
            api('DELETE', '/api/uploads/' + sessionId, null).catch(function () {
                // The expiry sweep removes anything this could not.
            });
        }
    }

    function forget(id) {
        var u = uploads[id];
        if (u && !isActive(u)) { delete uploads[id]; }
    }

    // The page is going away. In-flight requests stop, but the server keeps each session, so choosing the same file
    // again later resumes it.
    function detach(ownerKey) {
        Object.keys(uploads).forEach(function (id) {
            var u = uploads[id];
            if (u.owner !== ownerKey) { return; }
            removeFromQueue(u);
            if (u.controller) { u.controller.abort(); }
            u.ref = null;
            delete uploads[id];
        });
    }

    function attachDropZone(zoneId, inputElementId) {
        var zone = document.getElementById(zoneId);
        if (!zone || zone.dataset.csspDrop === '1') { return; }
        zone.dataset.csspDrop = '1';
        var depth = 0;
        zone.addEventListener('dragenter', function (e) {
            if (!hasFiles(e)) { return; }
            e.preventDefault();
            depth++;
            zone.classList.add('is-dragover');
        });
        zone.addEventListener('dragover', function (e) {
            if (!hasFiles(e)) { return; }
            e.preventDefault();
            e.dataTransfer.dropEffect = 'copy';
        });
        zone.addEventListener('dragleave', function () {
            depth = Math.max(0, depth - 1);
            if (depth === 0) { zone.classList.remove('is-dragover'); }
        });
        zone.addEventListener('drop', function (e) {
            depth = 0;
            zone.classList.remove('is-dragover');
            if (!hasFiles(e)) { return; }
            e.preventDefault();
            var input = document.getElementById(inputElementId);
            if (!input) { return; }
            // Handing the dropped files to the file input makes a drop exactly the same as choosing them.
            input.files = e.dataTransfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        });
    }

    function hasFiles(e) {
        return !!(e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types || [], 'Files') >= 0);
    }

    window.csspUploader = {
        start: start,
        pause: pause,
        resume: resume,
        overwrite: overwrite,
        cancel: cancel,
        forget: forget,
        detach: detach,
        attachDropZone: attachDropZone
    };
})();
