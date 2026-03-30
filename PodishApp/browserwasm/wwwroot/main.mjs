const React = globalThis.React;
const ReactDOM = globalThis.ReactDOM;
const { useEffect, useMemo, useRef, useState } = React;
const { Terminal } = globalThis;
const { FitAddon } = globalThis.FitAddon;
const encoder = new TextEncoder();
const decoder = new TextDecoder();

function createPodishWorkerClient() {
  const worker = new Worker(new URL('./podish-worker.mjs', import.meta.url), { type: 'module' });
  let nextRequestId = 1;
  const pending = new Map();

  const ready = new Promise((resolve, reject) => {
    const onMessage = event => {
      const message = event.data;
      if (message?.type === 'ready') {
        worker.removeEventListener('message', onMessage);
        resolve();
      } else if (message?.type === 'ready-error') {
        worker.removeEventListener('message', onMessage);
        reject(new Error(message.error ?? 'Worker failed to initialize.'));
      }
    };
    worker.addEventListener('message', onMessage);
    worker.addEventListener('error', event => reject(event.error ?? new Error(event.message)));
  });

  worker.addEventListener('message', event => {
    const message = event.data;
    if (message?.type !== 'response')
      return;

    const entry = pending.get(message.id);
    if (!entry)
      return;

    pending.delete(message.id);
    if (message.ok) {
      entry.resolve(message.result);
    } else {
      entry.reject(new Error(message.error ?? 'Worker invocation failed.'));
    }
  });

  function invoke(method, args = [], transfer = []) {
    return ready.then(() => new Promise((resolve, reject) => {
      const id = nextRequestId++;
      pending.set(id, { resolve, reject });
      worker.postMessage({ type: 'invoke', id, method, args }, transfer);
    }));
  }

  return {
    ready,
    invoke,
    terminate() {
      worker.terminate();
    }
  };
}

const podishWorker = createPodishWorkerClient();

async function callWorker(method, args = [], transfer = []) {
  return podishWorker.invoke(method, args, transfer);
}

function App() {
  const terminalRef = useRef(null);
  const xtermRef = useRef(null);
  const fitRef = useRef(null);
  const pumpRef = useRef(null);
  const [rootfsFile, setRootfsFile] = useState(null);
  const [busy, setBusy] = useState(false);

  const api = useMemo(() => ({
    async printRuntimeInfo() {
      xtermRef.current?.writeln(await callWorker('GetRuntimeInfo'));
    },
    async probeNative() {
      xtermRef.current?.writeln(await callWorker('ProbeNative'));
    },
    clear() {
      xtermRef.current?.clear();
    },
    async startRootfsShell() {
      if (!rootfsFile) {
        xtermRef.current?.writeln('No rootfs tar selected.');
        return;
      }

      setBusy(true);
      try {
        const bytes = new Uint8Array(await rootfsFile.arrayBuffer());
        xtermRef.current?.writeln(`Uploading ${rootfsFile.name} (${bytes.byteLength} bytes) ...`);
        const result = JSON.parse(await callWorker('StartRootfsTarShell', [bytes], [bytes.buffer]));
        if (!result.ok) {
          xtermRef.current?.writeln(`Start failed: ${result.error}`);
          return;
        }

        xtermRef.current?.writeln(`Container started: ${result.containerId}`);
        xtermRef.current?.write('\r\n');
        startOutputPump();
      } finally {
        setBusy(false);
      }
    },
    async stopSession() {
      setBusy(true);
      try {
        const result = JSON.parse(await callWorker('StopSession'));
        if (result.message) {
          xtermRef.current?.writeln(result.message);
        } else {
          xtermRef.current?.writeln('Session stopped.');
        }
      } finally {
        stopOutputPump();
        setBusy(false);
      }
    }
  }), [rootfsFile]);

  function stopOutputPump() {
    if (pumpRef.current !== null) {
      globalThis.clearInterval(pumpRef.current);
      pumpRef.current = null;
    }
  }

  function startOutputPump() {
    stopOutputPump();
    pumpRef.current = globalThis.setInterval(async () => {
      try {
        const chunk = await callWorker('ReadSessionOutput', [8192]);
        if (chunk?.length) {
          xtermRef.current?.write(decoder.decode(chunk, { stream: true }));
        }

        const status = JSON.parse(await callWorker('GetSessionStatus'));
        if (status.hasSession && !status.running) {
          xtermRef.current?.writeln(`\r\n[process exited: ${status.exitCode}]`);
          stopOutputPump();
        }
      } catch (error) {
        xtermRef.current?.writeln(`\r\n[worker error] ${error}`);
        stopOutputPump();
      }
    }, 50);
  }

  useEffect(() => {
    const terminal = new Terminal({
      cursorBlink: true,
      convertEol: true,
      theme: {
        background: '#020617',
        foreground: '#e2e8f0'
      }
    });
    const fitAddon = new FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.open(terminalRef.current);
    fitAddon.fit();
    terminal.writeln('Podish browser-wasm shell');
    terminal.writeln('React.js + xterm.js frontend ready');
    terminal.writeln('Starting .NET runtime inside a Web Worker...');
    terminal.writeln('');
    terminal.write('$ ');
    xtermRef.current = terminal;
    fitRef.current = fitAddon;

    podishWorker.ready
      .then(async () => {
        terminal.writeln(await callWorker('GetRuntimeInfo'));
        terminal.writeln('Choose a rootfs .tar, then start /bin/sh from tmpfs.');
      })
      .catch(error => {
        terminal.writeln(`[worker bootstrap failed] ${error}`);
      });

    const onResize = () => fitAddon.fit();
    globalThis.addEventListener('resize', onResize);
    const onData = data => {
      const bytes = encoder.encode(data);
      void callWorker('WriteSessionInput', [bytes], [bytes.buffer]).catch(error => {
        terminal.writeln(`\r\n[input error] ${error}`);
      });
    };
    const dataDisposable = terminal.onData(onData);

    const resizeToGuest = () => {
      const { rows, cols } = terminal;
      void callWorker('ResizeSessionTerminal', [rows, cols]).catch(() => { });
    };
    resizeToGuest();
    const resizeDisposable = terminal.onResize(({ rows, cols }) => {
      void callWorker('ResizeSessionTerminal', [rows, cols]).catch(() => { });
    });

    return () => {
      stopOutputPump();
      globalThis.removeEventListener('resize', onResize);
      dataDisposable.dispose();
      resizeDisposable.dispose();
      terminal.dispose();
      xtermRef.current = null;
      fitRef.current = null;
    };
  }, []);

  return React.createElement(
    'div',
    { className: 'shell' },
    React.createElement(
      'div',
      { className: 'toolbar' },
      React.createElement('h1', null, 'Podish Browser Wasm'),
      React.createElement('input', {
        type: 'file',
        accept: '.tar,application/x-tar',
        onChange: event => setRootfsFile(event.target.files?.[0] ?? null)
      }),
      React.createElement('button', { onClick: () => void api.startRootfsShell(), disabled: busy || !rootfsFile }, 'Start /bin/sh'),
      React.createElement('button', { className: 'secondary', onClick: () => void api.stopSession(), disabled: busy }, 'Stop'),
      React.createElement('button', { onClick: () => void api.printRuntimeInfo() }, 'Print Podish.Core'),
      React.createElement('button', { onClick: () => void api.probeNative() }, 'Probe libfibercpu'),
      React.createElement('button', { className: 'secondary', onClick: api.clear }, 'Clear')
    ),
    React.createElement(
      'div',
      { className: 'terminal-host' },
      React.createElement('div', { ref: terminalRef, className: 'terminal' })
    )
  );
}

const root = ReactDOM.createRoot(document.getElementById('app'));
root.render(React.createElement(App));
