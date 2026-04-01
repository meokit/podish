import {useCallback, useEffect, useMemo, useRef, useState} from 'react'
import {Terminal} from '@xterm/xterm'
import {FitAddon} from '@xterm/addon-fit'
import {
    callWorker,
    decoder,
    encoder,
    podishWorker,
    startSessionRunLoop,
    writeSessionInput,
    writeSessionResize
} from './worker-client'

const CONTROL_SESSION_EXITED = 2

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

function ImportRootfsModal({open, busy, selectedFileName, onPickFile, onCancel, onConfirm}) {
    if (!open)
        return null

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/72 backdrop-blur-sm p-4">
            <div className="glass-panel w-full max-w-lg rounded-2xl border border-slate-700/70 p-6 shadow-2xl shadow-slate-950/50">
                <div className="flex items-start justify-between gap-4">
                    <div className="space-y-2">
                        <p className="text-xs font-semibold uppercase tracking-[0.24em] text-brand-200/80">
                            Import Rootfs
                        </p>
                        <h2 className="text-xl font-semibold text-slate-100">
                            Choose a custom Linux rootfs
                        </h2>
                        <p className="text-sm leading-6 text-slate-400">
                            Select a <span className="font-mono text-slate-300">.tar</span>, <span
                            className="font-mono text-slate-300">.tar.gz</span>, or <span
                            className="font-mono text-slate-300">.tgz</span> archive. Once confirmed, this tab will boot directly from that rootfs.
                        </p>
                    </div>
                    <button
                        type="button"
                        onClick={onCancel}
                        disabled={busy}
                        className="rounded-full border border-slate-700/80 px-3 py-1 text-xs font-medium text-slate-400 transition hover:border-slate-500 hover:text-slate-200 disabled:cursor-not-allowed disabled:opacity-40"
                    >
                        Cancel
                    </button>
                </div>

                <label
                    className={`mt-6 flex cursor-pointer flex-col items-center justify-center gap-3 rounded-2xl border border-dashed px-5 py-10 text-center transition ${
                        busy
                            ? 'cursor-not-allowed border-slate-800 bg-slate-900/40 opacity-60'
                            : 'border-slate-600/70 bg-slate-900/50 hover:border-brand-300/60 hover:bg-slate-900/70'
                    }`}
                >
                    <input
                        type="file"
                        accept=".tar,.tar.gz,.tgz,application/x-tar,application/gzip"
                        className="hidden"
                        disabled={busy}
                        onChange={event => onPickFile(event.target.files?.[0] ?? null)}
                    />
                    <span className="text-sm font-medium text-slate-100">
                        {selectedFileName || 'Click to choose a rootfs archive'}
                    </span>
                    <span className="text-xs text-slate-500">
                        Supports plain tar archives and gzip-compressed tarballs.
                    </span>
                </label>

                <div className="mt-6 flex items-center justify-between gap-4">
                    <p className="text-xs text-slate-500">
                        No file selected means this tab will fall back to the default rootfs.
                    </p>
                    <div className="flex items-center gap-3">
                        <button
                            type="button"
                            onClick={onCancel}
                            disabled={busy}
                            className="rounded-xl border border-slate-700/80 px-4 py-2 text-sm font-medium text-slate-300 transition hover:border-slate-500 hover:text-slate-100 disabled:cursor-not-allowed disabled:opacity-40"
                        >
                            Use Default Instead
                        </button>
                        <button
                            type="button"
                            onClick={onConfirm}
                            disabled={busy || !selectedFileName}
                            className="rounded-xl bg-brand-500 px-4 py-2 text-sm font-semibold text-white shadow-lg shadow-brand-900/30 transition hover:bg-brand-400 disabled:cursor-not-allowed disabled:opacity-40"
                        >
                            {busy ? 'Starting...' : 'Start from Rootfs'}
                        </button>
                    </div>
                </div>
            </div>
        </div>
    )
}

export default function App() {
    const terminalRef = useRef(null)
    const xtermRef = useRef(null)
    const fitRef = useRef(null)
    const bootFlowStartedRef = useRef(false)

    const [status, setStatus] = useState('idle')
    const [workerReady, setWorkerReady] = useState(false)
    const [sessionMessage, setSessionMessage] = useState('No active container')
    const [importModalOpen, setImportModalOpen] = useState(false)
    const [selectedImportFile, setSelectedImportFile] = useState(null)
    const [importFallbackPending, setImportFallbackPending] = useState(false)

    const searchParams = useMemo(() => new URLSearchParams(globalThis.location?.search || ''), [])
    const importMode = searchParams.get('import') === '1'
    const isBusy = status === 'downloading' || status === 'booting'

    const settleTerminalLayout = useCallback(async () => {
        fitRef.current?.fit()
        await new Promise(resolve => globalThis.requestAnimationFrame(() => resolve()))
        fitRef.current?.fit()
        return xtermRef.current || {rows: 24, cols: 80}
    }, [])

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
        xtermRef.current?.writeln('\x1b[38;5;244mPreparing default browser rootfs...\x1b[0m')
        try {
            const imageJsonUrl = new URL('./image.json', globalThis.location.href).toString()
            const imageResp = await fetch(imageJsonUrl)
            if (!imageResp.ok)
                throw new Error(`HTTP ${imageResp.status}: ${imageResp.statusText}`)

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
        } catch (error) {
            xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m Download failed: ${error}`)
            setStatus('error')
            setSessionMessage('Download failed')
        }
    }, [settleTerminalLayout])

    const openImportTab = useCallback(() => {
        const importUrl = new URL(globalThis.location.href)
        importUrl.searchParams.set('import', '1')
        globalThis.open(importUrl.toString(), '_blank', 'noopener,noreferrer')
    }, [])

    const handleImportFallback = useCallback(() => {
        if (importFallbackPending)
            return

        setImportFallbackPending(true)
        setImportModalOpen(false)
        setSelectedImportFile(null)
        xtermRef.current?.writeln('\x1b[38;5;244mImport cancelled, starting default rootfs instead...\x1b[0m')
        void bootDefault()
    }, [bootDefault, importFallbackPending])

    const confirmImportRootfs = useCallback(async () => {
        if (!selectedImportFile)
            return

        setImportModalOpen(false)
        setStatus('booting')
        xtermRef.current?.writeln(`\x1b[38;5;244mReading ${selectedImportFile.name}...\x1b[0m`)
        try {
            let tarBytes = new Uint8Array(await selectedImportFile.arrayBuffer())
            if (selectedImportFile.name.endsWith('.gz') || selectedImportFile.name.endsWith('.tgz')) {
                xtermRef.current?.writeln('\x1b[38;5;244mDecompressing gzip...\x1b[0m')
                tarBytes = await decompressGzip(tarBytes)
            }
            await bootWithBytes(tarBytes, selectedImportFile.name)
        } catch (error) {
            xtermRef.current?.writeln(`\x1b[31m✗\x1b[0m Import failed: ${error}`)
            setStatus('error')
            setSessionMessage('Import failed')
        }
    }, [bootWithBytes, selectedImportFile])

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
                terminal.writeln('\x1b[32m✓\x1b[0m .NET runtime ready')
                const info = await callWorker('GetRuntimeInfo')
                terminal.writeln(`\x1b[32m✓\x1b[0m ${info}`)
                terminal.writeln('')
                if (importMode)
                    terminal.writeln('\x1b[38;5;244mPlease choose a custom rootfs to continue.\x1b[0m')
                else
                    terminal.writeln('\x1b[38;5;244mPreparing the default rootfs automatically...\x1b[0m')
                setWorkerReady(true)
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
            setStatus('stopped')
            setSessionMessage(`Process exited with code ${exitCode}`)
        })

        const resizeDisposable = terminal.onResize(() => {
            const {rows, cols} = terminal
            void writeSessionResize(rows, cols).catch(() => {
            })
        })

        return () => {
            globalThis.removeEventListener('resize', onResize)
            dataDisposable.dispose()
            resizeDisposable.dispose()
            terminal.dispose()
            xtermRef.current = null
            fitRef.current = null
        }
    }, [importMode])

    useEffect(() => {
        if (!workerReady || bootFlowStartedRef.current)
            return

        bootFlowStartedRef.current = true
        if (importMode) {
            setImportModalOpen(true)
            return
        }

        void bootDefault()
    }, [bootDefault, importMode, workerReady])

    return (
        <div className="flex h-screen flex-col overflow-hidden">
            <ImportRootfsModal
                open={importModalOpen}
                busy={isBusy}
                selectedFileName={selectedImportFile?.name || ''}
                onPickFile={setSelectedImportFile}
                onCancel={handleImportFallback}
                onConfirm={confirmImportRootfs}
            />

            <header className="glass z-10 flex items-center justify-between px-6 py-3">
                <div className="flex min-w-0 items-center gap-3">
                    <h1 className="bg-gradient-to-r from-brand-400 to-brand-300 bg-clip-text text-lg font-bold tracking-tight text-transparent">
                        Podish
                    </h1>
                    <span className="truncate text-xs font-mono text-slate-500">
                        x86 Linux in WebAssembly
                    </span>
                    <span className="hidden text-xs text-slate-600 sm:inline">•</span>
                    <span className="hidden truncate text-xs font-mono text-slate-500 sm:inline">
                        {sessionMessage}
                    </span>
                </div>
                <div className="flex items-center gap-3">
                    {importMode && !importFallbackPending && status === 'idle' && (
                        <span className="hidden text-[11px] uppercase tracking-[0.2em] text-brand-200/70 sm:inline">
                            Import Mode
                        </span>
                    )}
                    <StatusBadge status={status}/>
                    <button
                        type="button"
                        onClick={openImportTab}
                        className="text-sm font-medium text-slate-300/70 underline decoration-slate-500/40 underline-offset-4 transition hover:text-slate-100 hover:decoration-slate-200/70"
                    >
                        Import your rootfs
                    </button>
                </div>
            </header>

            <main className="flex min-h-0 flex-1 flex-col px-4 pb-4 pt-3">
                <div
                    ref={terminalRef}
                    className="terminal-container animate-glow flex-1 overflow-hidden rounded-xl border border-slate-700/50 bg-[#020617]"
                />
            </main>
        </div>
    )
}
