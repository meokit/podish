import {useCallback, useEffect, useRef, useState} from 'react'
import {Terminal} from '@xterm/xterm'
import {FitAddon} from '@xterm/addon-fit'
import {
    callWorker,
    decoder,
    encoder,
    podishWorker,
    startSessionRunLoop,
    stopSessionViaSab,
    writeSessionInput,
    writeSessionResize
} from './worker-client'

const CONTROL_SESSION_EXITED = 2

// ── Gzip decompression ──────────────────────────────────────────────────────

async function decompressGzip(compressedBytes) {
    const ds = new DecompressionStream('gzip')
    const blob = new Blob([compressedBytes])
    const decompressed = blob.stream().pipeThrough(ds)
    const reader = decompressed.getReader()
    const chunks = []
    let totalLength = 0
    while (true) {
        const {done, value} = await reader.read()
        if (done) break
        chunks.push(value)
        totalLength += value.byteLength
    }
    const result = new Uint8Array(totalLength)
    let offset = 0
    for (const chunk of chunks) {
        result.set(chunk, offset)
        offset += chunk.byteLength
    }
    return result
}

// ── StatusBadge ─────────────────────────────────────────────────────────────

const statusConfig = {
    idle: {text: 'Idle', color: 'bg-slate-600', dot: 'bg-slate-400'},
    downloading: {text: 'Downloading', color: 'bg-amber-900/60', dot: 'bg-amber-400 animate-pulse'},
    booting: {text: 'Booting', color: 'bg-brand-900/60', dot: 'bg-brand-400 animate-pulse'},
    running: {text: 'Running', color: 'bg-emerald-900/60', dot: 'bg-emerald-400 animate-pulse-slow'},
    stopped: {text: 'Stopped', color: 'bg-red-900/60', dot: 'bg-red-400'},
    error: {text: 'Error', color: 'bg-red-900/60', dot: 'bg-red-500'},
}

function StatusBadge({status}) {
    const cfg = statusConfig[status] || statusConfig.idle
    return (
        <span className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium ${cfg.color}`}>
      <span className={`w-2 h-2 rounded-full ${cfg.dot}`}/>
            {cfg.text}
    </span>
    )
}

// ── ActionButton ────────────────────────────────────────────────────────────

const variants = {
    primary: 'bg-brand-600 hover:bg-brand-500 text-white shadow-lg shadow-brand-600/20 hover:shadow-brand-500/30',
    secondary: 'bg-slate-700/80 hover:bg-slate-600/80 text-slate-200 border border-slate-600/50',
    danger: 'bg-red-600/80 hover:bg-red-500/80 text-white',
}

function ActionButton({onClick, disabled, variant = 'primary', className = '', children}) {
    return (
        <button
            onClick={onClick}
            disabled={disabled}
            className={`px-4 py-2 rounded-lg text-sm font-medium transition-all duration-200
        disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer
        ${variants[variant] || variants.primary} ${className}`}
        >
            {children}
        </button>
    )
}

// ── App ─────────────────────────────────────────────────────────────────────

export default function App() {
    const terminalRef = useRef(null)
    const xtermRef = useRef(null)
    const fitRef = useRef(null)
    const [status, setStatus] = useState('idle')
    const [workerReady, setWorkerReady] = useState(false)
    const [downloadProgress, setDownloadProgress] = useState(null)
    const [sessionMessage, setSessionMessage] = useState('No active container')

    const settleTerminalLayout = useCallback(async () => {
        fitRef.current?.fit()
        await new Promise(resolve => globalThis.requestAnimationFrame(() => resolve()))
        fitRef.current?.fit()
        return xtermRef.current || {rows: 24, cols: 80}
    }, [])

    // ── Terminal init ──

    useEffect(() => {
        const terminal = new Terminal({
            cursorBlink: true,
            convertEol: true,
            fontFamily: "'JetBrains Mono', monospace",
            fontSize: 14,
            theme: {
                background: '#020617',
                foreground: '#e2e8f0',
                cursor: '#818cf8',
                selectionBackground: 'rgba(99,102,241,0.3)',
                black: '#0f172a',
                red: '#f87171',
                green: '#4ade80',
                yellow: '#facc15',
                blue: '#60a5fa',
                magenta: '#c084fc',
                cyan: '#22d3ee',
                white: '#e2e8f0',
                brightBlack: '#475569',
                brightRed: '#fca5a5',
                brightGreen: '#86efac',
                brightYellow: '#fde68a',
                brightBlue: '#93c5fd',
                brightMagenta: '#d8b4fe',
                brightCyan: '#67e8f9',
                brightWhite: '#f8fafc',
            },
        })
        const fitAddon = new FitAddon()
        terminal.loadAddon(fitAddon)
        terminal.open(terminalRef.current)
        fitAddon.fit()

        terminal.writeln('\x1b[1;38;5;99m  ____           _ _     _     \x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m |  _ \\ ___   __| (_)___| |__  \x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m | |_) / _ \\ / _` | / __| \'_ \\ \x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m |  __/ (_) | (_| | \\__ \\ | | |\x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m |_|   \\___/ \\__,_|_|___/_| |_|\x1b[0m')
        terminal.writeln('')
        terminal.writeln('\x1b[38;5;244m x86 Linux in your browser — powered by WebAssembly\x1b[0m')
        terminal.writeln('')

        xtermRef.current = terminal
        fitRef.current = fitAddon

        podishWorker.ready
            .then(async () => {
                setWorkerReady(true)
                terminal.writeln('\x1b[32m✓\x1b[0m .NET runtime ready')
                const info = await callWorker('GetRuntimeInfo')
                terminal.writeln(`\x1b[32m✓\x1b[0m ${info}`)
                terminal.writeln('')
                terminal.writeln('\x1b[38;5;244mPress "Boot Default" to start, or import your own rootfs.\x1b[0m')
            })
            .catch(error => {
                terminal.writeln(`\x1b[31m✗\x1b[0m Worker bootstrap failed: ${error}`)
                setStatus('error')
            })

        if (globalThis.document?.fonts?.ready) {
            void globalThis.document.fonts.ready.then(() => {
                fitAddon.fit()
            })
        }

        const onResize = () => fitAddon.fit()
        globalThis.addEventListener('resize', onResize)

        const onData = data => {
            const bytes = encoder.encode(data)
            void writeSessionInput(bytes).catch(() => {
            })
        }
        const dataDisposable = terminal.onData(onData)

        podishWorker.onOutput((kind, chunk) => {
            if (chunk?.length)
                xtermRef.current?.write(decoder.decode(chunk, {stream: true}))
        })
        podishWorker.onControl(payload => {
            if (!payload?.length)
                return
            if (payload[0] !== CONTROL_SESSION_EXITED || payload.length < 5)
                return
            const exitCode = new DataView(payload.buffer, payload.byteOffset, payload.byteLength).getInt32(1, true)
            xtermRef.current?.writeln(`\r\n\x1b[38;5;244m[process exited: ${exitCode}]\x1b[0m`)
            stopStatusMonitor()
            setStatus('stopped')
            setSessionMessage(`Process exited with code ${exitCode}`)
        })

        const resizeToGuest = () => {
            const {rows, cols} = terminal
            void writeSessionResize(rows, cols).catch(() => {
            })
        }
        resizeToGuest()
        const resizeDisposable = terminal.onResize(() => {
            const {rows, cols} = terminal
            void writeSessionResize(rows, cols).catch(() => {
            })
        })

        return () => {
            stopStatusMonitor()
            globalThis.removeEventListener('resize', onResize)
            dataDisposable.dispose()
            resizeDisposable.dispose()
            terminal.dispose()
            xtermRef.current = null
            fitRef.current = null
        }
    }, [])

    // ── Session monitoring ──

    function stopStatusMonitor() {
    }

    // ── Actions ──

    const bootWithBytes = useCallback(async (tarBytes, label) => {
        setStatus('booting')
        xtermRef.current?.writeln(`\x1b[38;5;244mBooting ${label} (${(tarBytes.byteLength / 1024 / 1024).toFixed(1)} MB)...\x1b[0m`)
        try {
            const {rows, cols} = await settleTerminalLayout()
            const bytes = new Uint8Array(tarBytes)
            const result = JSON.parse(await callWorker('StartRootfsTarShell', [bytes, rows, cols], [bytes.buffer]))
            if (!result.ok) {
                xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m Boot failed: ${result.error}`)
                setStatus('error')
                setSessionMessage('Boot failed')
                return
            }
            setStatus('running')
            setSessionMessage(`Container started: ${result.containerId}`)
            void writeSessionResize(rows, cols).catch(() => {
            })
            startSessionRunLoop()
        } catch (error) {
            xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m ${error}`)
            setStatus('error')
            setSessionMessage('Boot failed')
        }
    }, [settleTerminalLayout])

    const bootDefault = useCallback(async () => {
        setStatus('downloading')
        setDownloadProgress(null)
        xtermRef.current?.writeln('\x1b[38;5;244mChecking default browser rootfs...\x1b[0m')
        try {
            const imageJsonUrl = new URL('./image.json', globalThis.location.href).toString()
            const imageResp = await fetch(imageJsonUrl)
            if (imageResp.ok) {
                const {rows, cols} = await settleTerminalLayout()
                xtermRef.current?.writeln('\x1b[32m✓\x1b[0m Found OCI layer metadata, booting streamed rootfs...')
                const result = JSON.parse(await callWorker('StartStoredImageShell', [imageJsonUrl, rows, cols]))
                if (!result.ok) {
                    xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m Boot failed: ${result.error}`)
                    setStatus('error')
                    setSessionMessage('Boot failed')
                    return
                }
                setStatus('running')
                setSessionMessage(`Container started: ${result.containerId}`)
                void writeSessionResize(rows, cols).catch(() => {
                })
                startSessionRunLoop()
                return
            }

            xtermRef.current?.writeln('\x1b[31m✗\x1b[0m OCI metadata not found; rootfs.tar.gz fallback is disabled by design.\x1b[0m')
            setStatus('error')
            setSessionMessage('OCI image.json required')
            return

            if (!resp.ok) throw new Error(`HTTP ${resp.status}: ${resp.statusText}`)

            const contentLength = parseInt(resp.headers.get('content-length') || '0', 10)
            const reader = resp.body.getReader()
            const chunks = []
            let loaded = 0
            while (true) {
                const {done, value} = await reader.read()
                if (done) break
                chunks.push(value)
                loaded += value.byteLength
                if (contentLength > 0) setDownloadProgress({loaded, total: contentLength})
            }
            const compressed = new Uint8Array(loaded)
            let offset = 0
            for (const c of chunks) {
                compressed.set(c, offset);
                offset += c.byteLength
            }

            xtermRef.current?.writeln(`\x1b[32m✓\x1b[0m Downloaded ${(loaded / 1024 / 1024).toFixed(1)} MB, decompressing...`)
            setDownloadProgress(null)
            const tarBytes = await decompressGzip(compressed)
            await bootWithBytes(tarBytes, 'default rootfs')
        } catch (error) {
            xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m Download failed: ${error}`)
            setStatus('error')
            setDownloadProgress(null)
            setSessionMessage('Download failed')
        }
    }, [bootWithBytes])

    const importRootfs = useCallback(async () => {
        const input = document.createElement('input')
        input.type = 'file'
        input.accept = '.tar,.tar.gz,.tgz,application/x-tar,application/gzip'
        input.onchange = async () => {
            const file = input.files?.[0]
            if (!file) return
            setStatus('booting')
            xtermRef.current?.writeln(`\x1b[38;5;244mReading ${file.name}...\x1b[0m`)
            try {
                let tarBytes = new Uint8Array(await file.arrayBuffer())
                if (file.name.endsWith('.gz') || file.name.endsWith('.tgz')) {
                    xtermRef.current?.writeln('\x1b[38;5;244mDecompressing gzip...\x1b[0m')
                    tarBytes = await decompressGzip(tarBytes)
                }
                await bootWithBytes(tarBytes, file.name)
            } catch (error) {
                xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m Import failed: ${error}`)
                setStatus('error')
                setSessionMessage('Import failed')
            }
        }
        input.click()
    }, [bootWithBytes])

    const stopSession = useCallback(async () => {
        await stopSessionViaSab().catch(error => {
            xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m ${error}`)
        })
        stopStatusMonitor()
        setStatus('stopped')
        setSessionMessage('Stopping container...')
    }, [])

    const isBusy = status === 'downloading' || status === 'booting'

    // ── Render ──

    return (
        <div className="flex flex-col h-screen">
            {/* Header */}
            <header className="glass flex items-center justify-between px-6 py-3 z-10">
                <div className="flex items-center gap-3">
                    <h1 className="text-lg font-bold tracking-tight bg-gradient-to-r from-brand-400 to-brand-300 bg-clip-text text-transparent">
                        Podish
                    </h1>
                    <span className="text-xs text-slate-500 font-mono">
            x86 Linux in WebAssembly
          </span>
                </div>
                <StatusBadge status={status}/>
            </header>

            {/* Body */}
            <div className="flex flex-1 min-h-0">
                {/* Sidebar */}
                <aside className="glass-panel w-64 shrink-0 flex flex-col p-4 gap-4 border-r border-slate-700/50">
                    {/* Boot */}
                    <div className="space-y-2">
                        <h2 className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Boot</h2>
                        <ActionButton onClick={bootDefault} disabled={isBusy || !workerReady} className="w-full">
                            {status === 'downloading' ? '⟳ Downloading...'
                                : status === 'booting' ? '⟳ Booting...'
                                    : '⚡ Boot Default Rootfs'}
                        </ActionButton>
                        {downloadProgress && (
                            <div className="space-y-1">
                                <div className="w-full bg-slate-700 rounded-full h-1.5 overflow-hidden">
                                    <div
                                        className="bg-brand-500 h-1.5 rounded-full transition-all duration-300"
                                        style={{width: `${Math.round((downloadProgress.loaded / downloadProgress.total) * 100)}%`}}
                                    />
                                </div>
                                <p className="text-xs text-slate-500 text-right">
                                    {(downloadProgress.loaded / 1024 / 1024).toFixed(1)} / {(downloadProgress.total / 1024 / 1024).toFixed(1)} MB
                                </p>
                            </div>
                        )}
                    </div>

                    <div className="border-t border-slate-700/50"/>

                    {/* Custom rootfs */}
                    <div className="space-y-2">
                        <h2 className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Custom Rootfs</h2>
                        <ActionButton onClick={importRootfs} disabled={isBusy || !workerReady} variant="secondary"
                                      className="w-full">
                            📁 Import .tar / .tar.gz
                        </ActionButton>
                    </div>

                    <div className="border-t border-slate-700/50"/>

                    {/* Session */}
                    <div className="space-y-2">
                        <h2 className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Session</h2>
                        <ActionButton onClick={stopSession} disabled={status !== 'running'} variant="danger"
                                      className="w-full">
                            ■ Stop
                        </ActionButton>
                        <ActionButton onClick={() => xtermRef.current?.clear()} variant="secondary" className="w-full">
                            🗑 Clear Terminal
                        </ActionButton>
                        <p className="text-xs text-slate-500 break-all">{sessionMessage}</p>
                    </div>

                    {/* Spacer */}
                    <div className="flex-1"/>

                    {/* Info */}
                    <div className="text-xs text-slate-600 space-y-1">
                        <p>Default rootfs: Alpine Linux i386</p>
                        <p>python3 · luajit · gcc · vim</p>
                        <p>Powered by .NET + Wasm</p>
                    </div>
                </aside>

                {/* Terminal */}
                <main className="flex-1 flex flex-col p-4 min-w-0">
                    <div className="flex items-center gap-2 mb-2">
                        <div className="flex gap-1.5">
                            <span className="w-3 h-3 rounded-full bg-red-500/80"/>
                            <span className="w-3 h-3 rounded-full bg-yellow-500/80"/>
                            <span className="w-3 h-3 rounded-full bg-green-500/80"/>
                        </div>
                        <span className="text-xs text-slate-500 font-mono ml-2">terminal</span>
                    </div>
                    <div
                        ref={terminalRef}
                        className="terminal-container flex-1 rounded-xl border border-slate-700/50 overflow-hidden bg-[#020617] animate-glow"
                    />
                </main>
            </div>
        </div>
    )
}
