import {
    attachQueue,
    createQueueStorage,
    EVENT_CONTROL,
    EVENT_INPUT_BYTES,
    EVENT_OUTPUT_BYTES,
    EVENT_RESIZE,
} from '../public/sab-ring.mjs'
import {
    attachInterruptController,
    createInterruptControllerStorage,
    IRQ_HTTP_RPC,
    IRQ_INPUT_READY,
    IRQ_LOG_READY,
    IRQ_OUTPUT_DRAINED,
    IRQ_OUTPUT_READY,
    IRQ_TIMER,
    IRQ_TIMER_CONTROL,
} from '../public/interrupt-controller.mjs'
import {
    attachHttpRpcController,
    createHttpRpcStorage,
    HTTP_RPC_CHUNK_CAPACITY,
    HTTP_RPC_OPCODE_STREAM_GET,
    HTTP_RPC_RANGE_MODE_BOUNDED,
    HTTP_RPC_RANGE_MODE_NONE,
    HTTP_RPC_RANGE_MODE_OPEN_ENDED,
    HTTP_RPC_RESULT_INVALID_REQUEST,
    HTTP_RPC_RESULT_NETWORK_ERROR,
    HTTP_RPC_RESULT_TIMEOUT,
} from '../public/http-rpc-shared.mjs'

const encoder = new TextEncoder()
const decoder = new TextDecoder()
const INPUT_QUEUE_CAPACITY = 64 * 1024
const OUTPUT_QUEUE_CAPACITY = 1024 * 1024
const LOG_QUEUE_CAPACITY = 512 * 1024
const CONTROL_STOP_SESSION = 1
const EVENT_LOG_MESSAGE = 5
const LOG_FLAG_BEGIN = 1 << 0
const LOG_FLAG_END = 1 << 1
const TIMER_COMMAND_NONE = 0
const TIMER_COMMAND_ARM = 1
const TIMER_COMMAND_CANCEL = 2

function createTimerControlStorage() {
    return new SharedArrayBuffer(3 * 4)
}

function attachTimerControl(buffer) {
    const i32 = new Int32Array(buffer, 0, 3)
    return {
        buffer,
        readCommand() {
            return Atomics.exchange(i32, 0, TIMER_COMMAND_NONE)
        },
        readDelayMs() {
            return Atomics.load(i32, 1) | 0
        },
    }
}

function createPodishWorkerClient() {
    const workerUrl = new URL('/podish-worker.mjs', globalThis.location?.href ?? 'http://localhost/')
    const pageSearch = globalThis.location?.search
    if (pageSearch) {
        const rawWasm = new URLSearchParams(pageSearch).get('rawwasm')
        if (rawWasm === '1' || rawWasm === 'true')
            workerUrl.searchParams.set('rawwasm', rawWasm)
    }
    const worker = new Worker(workerUrl, {type: 'module'})
    let nextRequestId = 1
    const pending = new Map()
    const sabState = {
        input: attachQueue(createQueueStorage(INPUT_QUEUE_CAPACITY), INPUT_QUEUE_CAPACITY),
        output: attachQueue(createQueueStorage(OUTPUT_QUEUE_CAPACITY), OUTPUT_QUEUE_CAPACITY),
        log: attachQueue(createQueueStorage(LOG_QUEUE_CAPACITY), LOG_QUEUE_CAPACITY),
        timer: attachTimerControl(createTimerControlStorage()),
        irq: attachInterruptController(createInterruptControllerStorage()),
        httpRpc: attachHttpRpcController(createHttpRpcStorage()),
    }
    let onOutputCallback = null
    let onControlCallback = null
    let runtimePumpStarted = false
    let activeTimerId = null
    let pendingLogChunks = []
    let pendingLogBytes = 0
    const activeHttpStreams = new Map()
    const openingHttpRequests = new Set()
    let httpRpcKickInFlight = false
    let httpRpcKickQueued = false
    let networkActivityCount = 0
    let onNetworkActivityChange = null

    function updateNetworkActivity(delta) {
        networkActivityCount = Math.max(0, networkActivityCount + delta)
        onNetworkActivityChange?.(networkActivityCount > 0)
    }

    const ready = new Promise((resolve, reject) => {
        const onMessage = event => {
            const message = event.data
            if (message?.type === 'ready') {
                worker.removeEventListener('message', onMessage)
                resolve()
            } else if (message?.type === 'ready-error') {
                worker.removeEventListener('message', onMessage)
                reject(new Error(message.error ?? 'Worker failed to initialize.'))
            }
        }
        worker.addEventListener('message', onMessage)
        worker.addEventListener('error', event => reject(event.error ?? new Error(event.message)))
    })

    worker.postMessage({
        type: 'init-sab',
        inputBuffer: sabState.input.buffer,
        outputBuffer: sabState.output.buffer,
        logBuffer: sabState.log.buffer,
        timerControlBuffer: sabState.timer.buffer,
        irqBuffer: sabState.irq.buffer,
        httpRpcBuffer: sabState.httpRpc.buffer,
        inputCapacity: INPUT_QUEUE_CAPACITY,
        outputCapacity: OUTPUT_QUEUE_CAPACITY,
        logCapacity: LOG_QUEUE_CAPACITY,
    })

    async function readStreamChunk(streamState, requestedLength) {
        const chunkLength = Math.max(1, Math.min(requestedLength || 1, HTTP_RPC_CHUNK_CAPACITY))

        if (streamState.remainingBytes === 0)
            return null

        while (true) {
            if (streamState.pendingChunk) {
                if (streamState.skipBytesRemaining > 0n) {
                    const pendingRemaining = streamState.pendingChunk.length - streamState.pendingOffset
                    const bytesToSkip = Number(streamState.skipBytesRemaining > BigInt(pendingRemaining)
                        ? BigInt(pendingRemaining)
                        : streamState.skipBytesRemaining)
                    streamState.pendingOffset += bytesToSkip
                    streamState.skipBytesRemaining -= BigInt(bytesToSkip)
                    if (streamState.pendingOffset >= streamState.pendingChunk.length) {
                        streamState.pendingChunk = null
                        streamState.pendingOffset = 0
                        continue
                    }
                }

            const remaining = streamState.pendingChunk.length - streamState.pendingOffset
            const toCopy = Math.min(
                chunkLength,
                remaining,
                streamState.remainingBytes >= 0 ? streamState.remainingBytes : remaining,
            )
            const payload = streamState.pendingChunk.subarray(
                streamState.pendingOffset,
                streamState.pendingOffset + toCopy,
            ).slice()
            streamState.pendingOffset += toCopy
            if (streamState.remainingBytes >= 0)
                streamState.remainingBytes -= toCopy
            if (streamState.pendingOffset >= streamState.pendingChunk.length) {
                streamState.pendingChunk = null
                streamState.pendingOffset = 0
            }
            return payload
            }

            let timeoutId = null
            try {
                const readPromise = streamState.reader.read()
                const result = streamState.timeoutMs >= 0
                    ? await Promise.race([
                        readPromise,
                        new Promise((_, reject) => {
                            timeoutId = globalThis.setTimeout(() => reject(new Error('timeout')), streamState.timeoutMs)
                        }),
                    ])
                    : await readPromise

                if (result.done)
                    return null

                let value = result.value instanceof Uint8Array ? result.value : new Uint8Array(result.value)
                if (streamState.skipBytesRemaining > 0n) {
                    const bytesToSkip = Number(streamState.skipBytesRemaining > BigInt(value.length)
                        ? BigInt(value.length)
                        : streamState.skipBytesRemaining)
                    streamState.skipBytesRemaining -= BigInt(bytesToSkip)
                    if (bytesToSkip >= value.length) {
                        continue
                    }
                    value = value.subarray(bytesToSkip)
                }

                const allowedLength = streamState.remainingBytes >= 0
                    ? Math.min(value.length, streamState.remainingBytes)
                    : value.length
                const limitedValue = allowedLength === value.length ? value : value.subarray(0, allowedLength)
                if (limitedValue.length <= chunkLength) {
                    if (streamState.remainingBytes >= 0)
                        streamState.remainingBytes -= limitedValue.length
                    return limitedValue
                }

                streamState.pendingChunk = limitedValue
                streamState.pendingOffset = chunkLength
                if (streamState.remainingBytes >= 0)
                    streamState.remainingBytes -= chunkLength
                return limitedValue.subarray(0, chunkLength).slice()
            } finally {
                if (timeoutId !== null)
                    globalThis.clearTimeout(timeoutId)
            }
        }
    }

    async function disposeHttpStream(requestId) {
        const streamState = activeHttpStreams.get(requestId)
        if (!streamState)
            return
        activeHttpStreams.delete(requestId)
        try {
            await streamState.reader.cancel()
        } catch {
        }
    }

    async function processHttpRpcKick() {
        if (httpRpcKickInFlight) {
            httpRpcKickQueued = true
            return
        }

        httpRpcKickInFlight = true
        try {
            do {
                httpRpcKickQueued = false

                for (const requestId of Array.from(activeHttpStreams.keys())) {
                    if (sabState.httpRpc.findSlotByRequestId(requestId) >= 0)
                        continue
                    await disposeHttpStream(requestId)
                }

                for (let slotId = 0; slotId < sabState.httpRpc.slotCount; slotId += 1) {
                    const beginRequest = sabState.httpRpc.readBeginRequest(slotId)
                    if (beginRequest && beginRequest.opcode === HTTP_RPC_OPCODE_STREAM_GET
                        && !activeHttpStreams.has(beginRequest.requestId)
                        && !openingHttpRequests.has(beginRequest.requestId)) {
                        openingHttpRequests.add(beginRequest.requestId)
                        updateNetworkActivity(1)
                        try {
                            const url = decoder.decode(beginRequest.urlBytes)
                            const controller = new AbortController()
                            const rangeStart = (BigInt(beginRequest.rangeStartHigh) << 32n) | BigInt(beginRequest.rangeStartLow)
                            let headers = undefined
                            if (beginRequest.rangeMode === HTTP_RPC_RANGE_MODE_OPEN_ENDED) {
                                headers = {Range: `bytes=${rangeStart.toString()}-`}
                            } else if (beginRequest.rangeMode === HTTP_RPC_RANGE_MODE_BOUNDED) {
                                const rangeEnd = rangeStart + BigInt(Math.max(0, beginRequest.rangeLength) - 1)
                                headers = {Range: `bytes=${rangeStart.toString()}-${rangeEnd.toString()}`}
                            }
                            let timeoutId = null
                            let response
                            try {
                                if (beginRequest.timeoutMs >= 0) {
                                    timeoutId = globalThis.setTimeout(() => controller.abort(), beginRequest.timeoutMs)
                                }
                                response = await fetch(url, {headers, signal: controller.signal})
                            } finally {
                                if (timeoutId !== null)
                                    globalThis.clearTimeout(timeoutId)
                            }

                            if (!response.ok || !response.body) {
                                sabState.httpRpc.completeFailure(slotId, beginRequest.requestId, -(response?.status || HTTP_RPC_RESULT_NETWORK_ERROR))
                                sabState.irq.signal(IRQ_HTTP_RPC)
                                continue
                            }

                            const skipBytesRemaining = beginRequest.rangeMode !== HTTP_RPC_RANGE_MODE_NONE && response.status === 200 && rangeStart > 0n
                                ? rangeStart
                                : 0n

                            if (sabState.httpRpc.findSlotByRequestId(beginRequest.requestId) < 0) {
                                try {
                                    await response.body.cancel()
                                } catch {
                                }
                                continue
                            }

                            activeHttpStreams.set(beginRequest.requestId, {
                                requestId: beginRequest.requestId,
                                reader: response.body.getReader(),
                                pendingChunk: null,
                                pendingOffset: 0,
                                skipBytesRemaining,
                                remainingBytes: beginRequest.rangeMode === HTTP_RPC_RANGE_MODE_BOUNDED ? beginRequest.rangeLength : -1,
                                timeoutMs: beginRequest.timeoutMs,
                            })
                            if (skipBytesRemaining > 0n)
                                console.warn(`[http-rpc/worker] range-fallback requestId=${beginRequest.requestId} url=${url} status=${response.status} skipBytes=${skipBytesRemaining} rangeMode=${beginRequest.rangeMode} rangeLength=${beginRequest.rangeLength}`)
                            sabState.httpRpc.markStarted(slotId, beginRequest.requestId, response.status | 0)
                            sabState.irq.signal(IRQ_HTTP_RPC)
                        } catch (error) {
                            const resultCode = error?.name === 'AbortError' || error?.message === 'timeout'
                                ? HTTP_RPC_RESULT_TIMEOUT
                                : HTTP_RPC_RESULT_NETWORK_ERROR
                            sabState.httpRpc.completeFailure(slotId, beginRequest.requestId, resultCode)
                            sabState.irq.signal(IRQ_HTTP_RPC)
                        } finally {
                            updateNetworkActivity(-1)
                            openingHttpRequests.delete(beginRequest.requestId)
                        }
                        continue
                    }

                    const readRequest = sabState.httpRpc.readPendingChunkRequest(slotId)
                    if (!readRequest)
                        continue

                    const streamState = activeHttpStreams.get(readRequest.requestId)
                    if (!streamState) {
                        if (openingHttpRequests.has(readRequest.requestId))
                            continue
                        sabState.httpRpc.completeFailure(slotId, readRequest.requestId, HTTP_RPC_RESULT_INVALID_REQUEST)
                        sabState.irq.signal(IRQ_HTTP_RPC)
                        continue
                    }

                    try {
                        const payload = await readStreamChunk(streamState, readRequest.requestedLength)
                        if (payload === null) {
                            await disposeHttpStream(readRequest.requestId)
                            sabState.httpRpc.markEof(slotId, readRequest.requestId)
                            sabState.irq.signal(IRQ_HTTP_RPC)
                            continue
                        }

                        sabState.httpRpc.writeChunk(slotId, readRequest.requestId, payload)
                        sabState.irq.signal(IRQ_HTTP_RPC)
                    } catch (error) {
                        await disposeHttpStream(readRequest.requestId)
                        const resultCode = error?.name === 'AbortError' || error?.message === 'timeout'
                            ? HTTP_RPC_RESULT_TIMEOUT
                            : HTTP_RPC_RESULT_NETWORK_ERROR
                        sabState.httpRpc.completeFailure(slotId, readRequest.requestId, resultCode)
                        sabState.irq.signal(IRQ_HTTP_RPC)
                    }
                }
            } while (httpRpcKickQueued)
        } finally {
            httpRpcKickInFlight = false
        }
    }

    function emitBrowserLog(payload) {
        if (!payload?.length)
            return

        const flags = payload[0] | 0
        const chunk = payload.length > 1 ? payload.subarray(1) : new Uint8Array(0)

        if (flags & LOG_FLAG_BEGIN) {
            pendingLogChunks = []
            pendingLogBytes = 0
        }

        if (chunk.length > 0) {
            pendingLogChunks.push(chunk.slice())
            pendingLogBytes += chunk.length
        }

        if ((flags & LOG_FLAG_END) === 0)
            return

        if (pendingLogBytes === 0) {
            pendingLogChunks = []
            return
        }

        const merged = new Uint8Array(pendingLogBytes)
        let offset = 0
        for (const part of pendingLogChunks) {
            merged.set(part, offset)
            offset += part.length
        }
        pendingLogChunks = []
        pendingLogBytes = 0

        const level = merged[0] | 0
        const text = merged.length > 1 ? decoder.decode(merged.subarray(1)) : ''
        switch (level) {
            case 0:
            case 1:
                console.debug(text)
                break
            case 2:
                console.info(text)
                break
            case 3:
                console.warn(text)
                break
            case 4:
            case 5:
                console.error(text)
                break
            default:
                console.log(text)
                break
        }
    }

    function drainOutputQueue() {
        let drainedAny = false
        while (true) {
            const packet = sabState.output.tryReadPacket()
            if (!packet)
                break
            if (packet.eventType !== EVENT_OUTPUT_BYTES) {
                if (packet.eventType === EVENT_CONTROL)
                    onControlCallback?.(packet.payload)
                continue
            }
            drainedAny = true
            onOutputCallback?.(0, packet.payload)
        }
        if (drainedAny)
            sabState.irq.signal(IRQ_OUTPUT_DRAINED)
    }

    function drainLogQueue() {
        while (true) {
            const packet = sabState.log.tryReadPacket()
            if (!packet)
                break
            if (packet.eventType === EVENT_LOG_MESSAGE)
                emitBrowserLog(packet.payload)
        }
    }

    function handleTimerControl() {
        const command = sabState.timer.readCommand()
        if (command === TIMER_COMMAND_NONE)
            return
        if (command === TIMER_COMMAND_CANCEL) {
            if (activeTimerId !== null) {
                globalThis.clearTimeout(activeTimerId)
                activeTimerId = null
            }
            return
        }
        if (command === TIMER_COMMAND_ARM) {
            if (activeTimerId !== null)
                globalThis.clearTimeout(activeTimerId)
            const delayMs = Math.max(0, sabState.timer.readDelayMs())
            activeTimerId = globalThis.setTimeout(() => {
                activeTimerId = null
                sabState.irq.signal(IRQ_TIMER)
            }, delayMs)
        }
    }

    function ensureRuntimePump() {
        if (runtimePumpStarted)
            return

        runtimePumpStarted = true
        void (async () => {
            const runtimeMask = IRQ_OUTPUT_READY | IRQ_LOG_READY | IRQ_TIMER_CONTROL
            while (true) {
                const flags = await sabState.irq.wait(runtimeMask)
                if (!flags)
                    continue
                if (flags & IRQ_OUTPUT_READY)
                    drainOutputQueue()
                if (flags & IRQ_LOG_READY)
                    drainLogQueue()
                if (flags & IRQ_TIMER_CONTROL)
                    handleTimerControl()
            }
        })()
    }

    function writeInput(bytes) {
        const written = sabState.input.tryWritePacketLossy(EVENT_INPUT_BYTES, bytes)
        if (written > 0)
            sabState.irq.signal(IRQ_INPUT_READY)
        return Promise.resolve(written)
    }

    function writeResize(rows, cols) {
        const payload = new Uint8Array(4)
        const view = new DataView(payload.buffer)
        view.setUint16(0, rows, true)
        view.setUint16(2, cols, true)
        const written = sabState.input.tryWritePacketLossy(EVENT_RESIZE, payload)
        if (written > 0)
            sabState.irq.signal(IRQ_INPUT_READY)
        return Promise.resolve(written)
    }

    function writeControl(payload) {
        const written = sabState.input.tryWritePacketLossy(EVENT_CONTROL, payload)
        if (written > 0)
            sabState.irq.signal(IRQ_INPUT_READY)
        return Promise.resolve(written)
    }

    function stopSession() {
        return writeControl(Uint8Array.of(CONTROL_STOP_SESSION))
    }

    function startRunLoop() {
        worker.postMessage({type: 'run-session'})
    }

    worker.addEventListener('message', event => {
        const message = event.data
        if (message?.type === 'http-rpc-kick') {
            void processHttpRpcKick()
            return
        }
        if (message?.type !== 'response') return
        const entry = pending.get(message.id)
        if (!entry) return
        pending.delete(message.id)
        if (message.ok) entry.resolve(message.result)
        else entry.reject(new Error(message.error ?? 'Worker invocation failed.'))
    })

    function invoke(method, args = [], transfer = []) {
        return ready.then(() => new Promise((resolve, reject) => {
            const id = nextRequestId++
            pending.set(id, {resolve, reject})
            worker.postMessage({type: 'invoke', id, method, args}, transfer)
        }))
    }

    return {
        ready,
        invoke,
        writeInput,
        writeResize,
        stopSession,
        startRunLoop,
        terminate() {
            worker.terminate()
        },
        onOutput(cb) {
            onOutputCallback = cb
            ensureRuntimePump()
        },
        onControl(cb) {
            onControlCallback = cb
            ensureRuntimePump()
        },
        onNetworkActivityChange(cb) {
            onNetworkActivityChange = cb
            cb?.(networkActivityCount > 0)
        }
    }
}

const podishWorker = createPodishWorkerClient()

export async function callWorker(method, args = [], transfer = []) {
    return podishWorker.invoke(method, args, transfer)
}

export function writeSessionInput(bytes) {
    return podishWorker.writeInput(bytes)
}

export function writeSessionResize(rows, cols) {
    return podishWorker.writeResize(rows, cols)
}

export function onWorkerNetworkActivityChange(cb) {
    podishWorker.onNetworkActivityChange(cb)
}

export function startSessionRunLoop() {
    podishWorker.startRunLoop()
}

export function stopSessionViaSab() {
    return podishWorker.stopSession()
}

export {podishWorker, encoder, decoder}
