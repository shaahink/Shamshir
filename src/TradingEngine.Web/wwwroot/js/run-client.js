// iter-21 U1 — live run client. Connects to the RunHub, joins a per-run group, auto-reconnects,
// and dispatches the typed RunProgress envelope to caller-supplied handlers.
//
// Usage:
//   const client = createRunClient('abc123', {
//     onProgress: p => { ... },   // throttled RunProgress frames while running
//     onDone:     p => { ... },   // terminal frame (completed | failed)
//     onStatus:   s => { ... },   // 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
//   });
//   client.start();
//
// The envelope shape is documented in Services/RunProgress.cs and pinned by RunProgressContractTests.
//
// This is an ES module: Monitor.cshtml does `import { createRunClient } from '/js/run-client.js'`.
// A `window.createRunClient` alias is also published for any non-module callers. (Previously this
// file was an IIFE with NO export, so the named import threw "does not provide an export named
// 'createRunClient'" at load time, aborting the whole module script and freezing the Monitor on
// "Connecting to run...".)
export function createRunClient(runId, handlers) {
    handlers = handlers || {};
    const status = s => handlers.onStatus && handlers.onStatus(s);

    if (!window.signalR) {
        console.error('run-client: signalR not loaded (check the CDN script in _Layout)');
        return { start: () => {}, stop: () => Promise.resolve() };
    }

    const connection = new window.signalR.HubConnectionBuilder()
        .withUrl('/hubs/run')
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .build();

    connection.on('onProgress', p => handlers.onProgress && handlers.onProgress(p));
    connection.on('onDone', p => handlers.onDone && handlers.onDone(p));

    connection.onreconnecting(() => status('reconnecting'));
    connection.onreconnected(async () => { await connection.invoke('JoinRun', runId); status('connected'); });
    connection.onclose(() => status('disconnected'));

    async function start() {
        status('connecting');
        try {
            await connection.start();
            await connection.invoke('JoinRun', runId);
            status('connected');
        } catch (err) {
            console.error('run-client: connect failed', err);
            status('disconnected');
            setTimeout(start, 2000); // retry initial connect
        }
    }

    async function stop() {
        try { await connection.invoke('LeaveRun', runId); } catch { /* ignore */ }
        await connection.stop();
    }

    return { start, stop, connection };
}

// Back-compat alias for any non-module callers.
window.createRunClient = createRunClient;
