import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports('PodishApp.BrowserWasm');
const browserExports = exports.PodishApp.BrowserWasm.BrowserExports;

const React = globalThis.React;
const ReactDOM = globalThis.ReactDOM;
const { useEffect, useMemo, useRef, useState } = React;
const { Terminal } = globalThis;
const { FitAddon } = globalThis.FitAddon;
const encoder = new TextEncoder();
const decoder = new TextDecoder();

function App() {
  const terminalRef = useRef(null);
  const xtermRef = useRef(null);
  const fitRef = useRef(null);
  const pumpRef = useRef(null);
  const [rootfsFile, setRootfsFile] = useState(null);
  const [busy, setBusy] = useState(false);

  const api = useMemo(() => ({
    printRuntimeInfo() {
      xtermRef.current?.writeln(browserExports.GetRuntimeInfo());
    },
    probeNative() {
      xtermRef.current?.writeln(browserExports.ProbeNative());
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
        const result = JSON.parse(await browserExports.StartRootfsTarShell(bytes));
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
        const result = JSON.parse(await browserExports.StopSession());
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
    pumpRef.current = globalThis.setInterval(() => {
      const chunk = browserExports.ReadSessionOutput(8192);
      if (chunk?.length) {
        xtermRef.current?.write(decoder.decode(chunk, { stream: true }));
      }

      const status = JSON.parse(browserExports.GetSessionStatus());
      if (status.hasSession && !status.running) {
        xtermRef.current?.writeln(`\r\n[process exited: ${status.exitCode}]`);
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
    terminal.writeln(browserExports.GetRuntimeInfo());
    terminal.writeln('Choose a rootfs .tar, then start /bin/sh from tmpfs.');
    terminal.writeln('');
    terminal.write('$ ');
    xtermRef.current = terminal;
    fitRef.current = fitAddon;

    const onResize = () => fitAddon.fit();
    globalThis.addEventListener('resize', onResize);
    const onData = data => browserExports.WriteSessionInput(encoder.encode(data));
    terminal.onData(onData);

    const resizeToGuest = () => {
      const { rows, cols } = terminal;
      browserExports.ResizeSessionTerminal(rows, cols);
    };
    resizeToGuest();
    terminal.onResize(({ rows, cols }) => browserExports.ResizeSessionTerminal(rows, cols));

    return () => {
      stopOutputPump();
      globalThis.removeEventListener('resize', onResize);
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
      React.createElement('button', { onClick: api.startRootfsShell, disabled: busy || !rootfsFile }, 'Start /bin/sh'),
      React.createElement('button', { className: 'secondary', onClick: api.stopSession, disabled: busy }, 'Stop'),
      React.createElement('button', { onClick: api.printRuntimeInfo }, 'Print Podish.Core'),
      React.createElement('button', { onClick: api.probeNative }, 'Probe libfibercpu'),
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
