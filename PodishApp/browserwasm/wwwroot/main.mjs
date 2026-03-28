import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.create();
await runtime.run();

const exports = await runtime.getAssemblyExports('PodishApp.BrowserWasm');
const browserExports = exports.PodishApp.BrowserWasm.BrowserExports;

const React = globalThis.React;
const ReactDOM = globalThis.ReactDOM;
const { useEffect, useMemo, useRef } = React;
const { Terminal } = globalThis;
const { FitAddon } = globalThis.FitAddon;

function App() {
  const terminalRef = useRef(null);
  const xtermRef = useRef(null);
  const fitRef = useRef(null);

  const api = useMemo(() => ({
    printRuntimeInfo() {
      xtermRef.current?.writeln(browserExports.GetRuntimeInfo());
    },
    probeNative() {
      xtermRef.current?.writeln(browserExports.ProbeNative());
    },
    clear() {
      xtermRef.current?.clear();
    }
  }), []);

  useEffect(() => {
    const terminal = new Terminal({
      cursorBlink: true,
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
    terminal.writeln('');
    terminal.write('$ ');
    xtermRef.current = terminal;
    fitRef.current = fitAddon;

    const onResize = () => fitAddon.fit();
    globalThis.addEventListener('resize', onResize);

    return () => {
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
