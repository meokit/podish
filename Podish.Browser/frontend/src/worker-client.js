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
    IRQ_INPUT_READY,
    IRQ_LOG_READY,
    IRQ_OUTPUT_DRAINED,
    IRQ_OUTPUT_READY,
    IRQ_TIMER,
    IRQ_TIMER_CONTROL,
} from '../public/interrupt-controller.mjs'

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
    const worker = new Worker('/podish-worker.mjs', {type: 'module'})
    let nextRequestId = 1
    const pending = new Map()
    const sabState = {
        input: attachQueue(createQueueStorage(INPUT_QUEUE_CAPACITY), INPUT_QUEUE_CAPACITY),
        output: attachQueue(createQueueStorage(OUTPUT_QUEUE_CAPACITY), OUTPUT_QUEUE_CAPACITY),
        log: attachQueue(createQueueStorage(LOG_QUEUE_CAPACITY), LOG_QUEUE_CAPACITY),
        timer: attachTimerControl(createTimerControlStorage()),
        irq: attachInterruptController(createInterruptControllerStorage()),
    }
    let onOutputCallback = null
    let onControlCallback = null
    let runtimePumpStarted = false
    let activeTimerId = null
    let pendingLogChunks = []
    let pendingLogBytes = 0

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
        inputCapacity: INPUT_QUEUE_CAPACITY,
        outputCapacity: OUTPUT_QUEUE_CAPACITY,
        logCapacity: LOG_QUEUE_CAPACITY,
    })

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

export function startSessionRunLoop() {
    podishWorker.startRunLoop()
}

export function stopSessionViaSab() {
    return podishWorker.stopSession()
}

export {podishWorker, encoder, decoder}
