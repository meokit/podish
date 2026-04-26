import {useCallback, useEffect, useMemo, useRef, useState} from 'react'
import {Terminal} from '@xterm/xterm'
import {FitAddon} from '@xterm/addon-fit'
import {WebglAddon} from '@xterm/addon-webgl'
import {
    callWorker,
    decoder,
    encoder,
    onWorkerNetworkActivityChange,
    podishWorker,
    startSessionRunLoop,
    wakeSessionRuntime,
    writeSessionInput,
    writeSessionResize
} from './worker-client'
import {getTerminalTheme} from './color-scheme'

const CONTROL_SESSION_EXITED = 2

function logStartup(message, ...args) {
    console.info(`[podish/browser] ${message}`, ...args)
}

function getImageUrlBase() {
    const configuredBase = globalThis.ImageUrlBase
    const base = typeof configuredBase === 'string' && configuredBase.trim().length > 0
        ? configuredBase.trim()
        : '/rootfs/'
    return base.endsWith('/') ? base : `${base}/`
}

function getDefaultImageJsonUrl() {
    return new URL('image.json', new URL(getImageUrlBase(), globalThis.location.href)).toString()
}

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
    idle: {text: 'Idle', color: 'status-idle', dot: 'status-dot-idle'},
    downloading: {text: 'Downloading', color: 'status-downloading', dot: 'status-dot-downloading animate-pulse'},
    booting: {text: 'Booting', color: 'status-booting', dot: 'status-dot-booting animate-pulse'},
    running: {text: 'Running', color: 'status-running', dot: 'status-dot-running animate-pulse-slow'},
    stopped: {text: 'Stopped', color: 'status-stopped', dot: 'status-dot-stopped'},
    error: {text: 'Error', color: 'status-error', dot: 'status-dot-error'},
}

function StatusBadge({status, networkActive = false}) {
    const cfg = statusConfig[status] || statusConfig.idle
    const diskLampActive = networkActive && status !== 'idle' && status !== 'error' && status !== 'stopped'
    return (
        <span
            className={`status-badge inline-flex items-center gap-2 rounded-full px-2.5 py-1 text-xs font-medium ${cfg.color} ${
                diskLampActive ? 'status-badge-network' : ''
            }`}
        >
            <span className={`h-2 w-2 rounded-full ${cfg.dot} ${diskLampActive ? 'status-dot-network' : ''}`}/>
            <span className="hidden min-[420px]:inline">{cfg.text}</span>
        </span>
    )
}

function ImportRootfsModal({open, busy, selectedFileName, onPickFile, onCancel, onConfirm}) {
    if (!open)
        return null

    return (
        <div className="modal-overlay fixed inset-0 z-50 flex items-center justify-center p-4 backdrop-blur-sm">
            <div className="glass-panel border-theme w-full max-w-lg rounded-2xl p-6 shadow-2xl">
                <div className="flex items-start justify-between gap-4">
                    <div className="space-y-2">
                        <p className="text-theme-accent-soft text-xs font-semibold uppercase tracking-[0.24em]">
                            Import Rootfs
                        </p>
                        <h2 className="text-theme-primary text-xl font-semibold">
                            Choose a custom Linux rootfs
                        </h2>
                        <p className="text-theme-muted text-sm leading-6">
                            Select a <span className="text-theme-primary font-mono">.tar</span>, <span
                            className="text-theme-primary font-mono">.tar.gz</span>, or <span
                            className="text-theme-primary font-mono">.tgz</span> archive. Once confirmed, this tab will boot directly from that rootfs.
                        </p>
                    </div>
                    <button
                        type="button"
                        onClick={onCancel}
                        disabled={busy}
                        className="modal-dismiss rounded-full px-3 py-1 text-xs font-medium transition disabled:cursor-not-allowed disabled:opacity-40"
                    >
                        Cancel
                    </button>
                </div>

                <label
                    className={`mt-6 flex cursor-pointer flex-col items-center justify-center gap-3 rounded-2xl border border-dashed px-5 py-10 text-center transition ${
                        busy
                            ? 'file-dropzone-disabled cursor-not-allowed opacity-60'
                            : 'file-dropzone'
                    }`}
                >
                    <input
                        type="file"
                        accept=".tar,.tar.gz,.tgz,application/x-tar,application/gzip"
                        className="hidden"
                        disabled={busy}
                        onChange={event => onPickFile(event.target.files?.[0] ?? null)}
                    />
                    <span className="text-theme-primary text-sm font-medium">
                        {selectedFileName || 'Click to choose a rootfs archive'}
                    </span>
                    <span className="text-theme-subtle text-xs">
                        Supports plain tar archives and gzip-compressed tarballs.
                    </span>
                </label>

                <div className="mt-6 flex items-center justify-between gap-4">
                    <p className="text-theme-subtle text-xs">
                        No file selected means this tab will fall back to the default rootfs.
                    </p>
                    <div className="flex items-center gap-3">
                        <button
                            type="button"
                            onClick={onCancel}
                            disabled={busy}
                            className="button-secondary rounded-xl px-4 py-2 text-sm font-medium transition disabled:cursor-not-allowed disabled:opacity-40"
                        >
                            Use Default Instead
                        </button>
                        <button
                            type="button"
                            onClick={onConfirm}
                            disabled={busy || !selectedFileName}
                            className="button-primary rounded-xl px-4 py-2 text-sm font-semibold transition disabled:cursor-not-allowed disabled:opacity-40"
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
    const networkFetchCountRef = useRef(0)

    const [status, setStatus] = useState('idle')
    const [workerReady, setWorkerReady] = useState(false)
    const [sessionMessage, setSessionMessage] = useState('No active container')
    const [importModalOpen, setImportModalOpen] = useState(false)
    const [selectedImportFile, setSelectedImportFile] = useState(null)
    const [importFallbackPending, setImportFallbackPending] = useState(false)
    const [networkActive, setNetworkActive] = useState(false)

    const searchParams = useMemo(() => new URLSearchParams(globalThis.location?.search || ''), [])
    const importMode = searchParams.get('import') === '1'
    const isBusy = status === 'downloading' || status === 'booting'

    const focusTerminal = useCallback(() => {
        xtermRef.current?.focus()
    }, [])

    const trackNetworkRequest = useCallback(async callback => {
        networkFetchCountRef.current += 1
        setNetworkActive(true)
        try {
            return await callback()
        } finally {
            networkFetchCountRef.current = Math.max(0, networkFetchCountRef.current - 1)
            if (networkFetchCountRef.current === 0)
                setNetworkActive(false)
        }
    }, [])

    const settleTerminalLayout = useCallback(async () => {
        fitRef.current?.fit()
        await new Promise(resolve => globalThis.requestAnimationFrame(() => resolve()))
        fitRef.current?.fit()
        return xtermRef.current || {rows: 24, cols: 80}
    }, [])

    const bootWithBytes = useCallback(async (tarBytes, label) => {
        setStatus('booting')
        logStartup('booting imported rootfs', {label, bytes: tarBytes.byteLength})
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
        logStartup('preparing default browser rootfs')
        try {
            const imageJsonUrl = getDefaultImageJsonUrl()
            const imageResp = await trackNetworkRequest(() => fetch(imageJsonUrl))
            if (!imageResp.ok)
                throw new Error(`HTTP ${imageResp.status}: ${imageResp.statusText}`)

            const {rows, cols} = await settleTerminalLayout()
            logStartup('found OCI metadata, booting streamed rootfs', {imageJsonUrl, rows, cols})
            const result = JSON.parse(await callWorker('StartStoredImageShell', [imageJsonUrl, rows, cols]))
            if (!result.ok) {
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
    }, [settleTerminalLayout, trackNetworkRequest])

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
        focusTerminal()
        void bootDefault()
    }, [bootDefault, focusTerminal, importFallbackPending])

    const confirmImportRootfs = useCallback(async () => {
        if (!selectedImportFile)
            return

        setImportModalOpen(false)
        setStatus('booting')
        xtermRef.current?.writeln(`\x1b[38;5;244mReading ${selectedImportFile.name}...\x1b[0m`)
        focusTerminal()
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
    }, [bootWithBytes, focusTerminal, selectedImportFile])

    useEffect(() => {
        let webglAddon = null
        const terminal = new Terminal({
            cursorBlink: true,
            convertEol: true,
            fontFamily: "'JetBrains Mono', monospace",
            fontSize: 14,
            theme: getTerminalTheme(),
        })
        const fitAddon = new FitAddon()
        terminal.loadAddon(fitAddon)
        terminal.open(terminalRef.current)

        try {
            webglAddon = new WebglAddon()
            terminal.loadAddon(webglAddon)
            webglAddon.onContextLoss(() => {
                try {
                    webglAddon?.dispose()
                } catch {
                }
                webglAddon = null
                fitAddon.fit()
            })
        } catch {
        }

        fitAddon.fit()
        terminal.focus()

        terminal.writeln('\x1b[1;38;5;99m  ____           _ _     _     \x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m |  _ \\ ___   __| (_)___| |__  \x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m | |_) / _ \\ / _` | / __| \'_ \\ \x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m |  __/ (_) | (_| | \\__ \\ | | |\x1b[0m')
        terminal.writeln('\x1b[1;38;5;99m |_|   \\___/ \\__,_|_|___/_| |_|\x1b[0m')
        terminal.writeln('')
        terminal.writeln('\x1b[38;5;244m x86 Linux in your browser — powered by WebAssembly\x1b[0m')
        terminal.writeln('\x1b[38;5;244m GitHub: https://github.com/meokit/podish\x1b[0m')
        terminal.writeln('\x1b[38;5;244m AltStore: https://github.com/meokit/podish-altstore\x1b[0m')
        terminal.writeln('')

        xtermRef.current = terminal
        fitRef.current = fitAddon

        podishWorker.ready
            .then(async () => {
                logStartup('.NET runtime ready')
                const info = await callWorker('GetRuntimeInfo')
                logStartup('runtime info', info)
                if (importMode)
                    terminal.writeln('\x1b[38;5;244mPlease choose a custom rootfs to continue.\x1b[0m')
                setWorkerReady(true)
            })
            .catch(error => {
                terminal.writeln(`\x1b[31m✗\x1b[0m Worker bootstrap failed: ${error}`)
                setStatus('error')
            })

        if (globalThis.document?.fonts?.ready) {
            void globalThis.document.fonts.ready.then(() => {
                fitAddon.fit()
                terminal.focus()
            })
        }

        const wakeRuntime = () => wakeSessionRuntime()
        const onResize = () => fitAddon.fit()
        const onPointerDown = () => {
            terminal.focus()
            wakeRuntime()
        }
        const onWindowFocus = () => wakeRuntime()
        const onPageShow = () => wakeRuntime()
        const onVisibilityChange = () => {
            if (globalThis.document?.visibilityState === 'visible')
                wakeRuntime()
        }
        globalThis.addEventListener('resize', onResize)
        globalThis.addEventListener('focus', onWindowFocus)
        globalThis.addEventListener('pageshow', onPageShow)
        globalThis.document?.addEventListener('visibilitychange', onVisibilityChange)
        terminalRef.current?.addEventListener('pointerdown', onPointerDown)

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
        onWorkerNetworkActivityChange(setNetworkActive)

        const resizeDisposable = terminal.onResize(() => {
            const {rows, cols} = terminal
            void writeSessionResize(rows, cols).catch(() => {
            })
        })

        return () => {
            globalThis.removeEventListener('resize', onResize)
            globalThis.removeEventListener('focus', onWindowFocus)
            globalThis.removeEventListener('pageshow', onPageShow)
            globalThis.document?.removeEventListener('visibilitychange', onVisibilityChange)
            terminalRef.current?.removeEventListener('pointerdown', onPointerDown)
            dataDisposable.dispose()
            resizeDisposable.dispose()
            onWorkerNetworkActivityChange(null)
            try {
                webglAddon?.dispose()
            } catch {
            }
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
        <div className="app-shell flex h-dvh min-h-0 flex-col overflow-hidden">
            <ImportRootfsModal
                open={importModalOpen}
                busy={isBusy}
                selectedFileName={selectedImportFile?.name || ''}
                onPickFile={setSelectedImportFile}
                onCancel={handleImportFallback}
                onConfirm={confirmImportRootfs}
            />

            <header className="glass z-10 flex items-center justify-between px-4 py-3 sm:px-6">
                <div className="flex min-w-0 items-center gap-3">
                    <h1 className="app-wordmark hidden bg-clip-text text-lg font-bold tracking-tight text-transparent sm:block">
                        Podish
                    </h1>
                    <span className="text-theme-muted hidden truncate text-xs font-mono md:inline">
                        x86 Linux in WebAssembly
                    </span>
                    <span className="text-theme-subtle hidden text-xs md:inline">•</span>
                    <span className="text-theme-muted hidden truncate text-xs font-mono lg:inline">
                        {sessionMessage}
                    </span>
                </div>
                <div className="flex items-center gap-3">
                    {importMode && !importFallbackPending && status === 'idle' && (
                        <span className="text-theme-accent-soft hidden text-[11px] uppercase tracking-[0.2em] sm:inline">
                            Import Mode
                        </span>
                    )}
                    <StatusBadge status={status} networkActive={networkActive}/>
                    <button
                        type="button"
                        onClick={openImportTab}
                        className="import-link hidden text-sm font-medium underline underline-offset-4 transition sm:inline-block"
                    >
                        Import rootfs
                    </button>
                    <a
                        href="https://github.com/meokit/podish"
                        target="_blank"
                        rel="noopener noreferrer"
                        className="text-theme-muted hover:text-theme-primary transition flex items-center justify-center ml-2"
                        title="View source on GitHub"
                    >
                        <svg viewBox="0 0 24 24" className="h-5 w-5" fill="currentColor">
                            <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z"/>
                        </svg>
                    </a>
                </div>
            </header>

            <main className="flex min-h-0 flex-1 flex-col px-0 pb-0 pt-0 sm:px-4 sm:pb-4 sm:pt-3">
                <div
                    ref={terminalRef}
                    onClick={focusTerminal}
                    className="terminal-shell terminal-container animate-glow flex-1 overflow-hidden rounded-none sm:rounded-xl"
                />
            </main>
        </div>
    )
}
