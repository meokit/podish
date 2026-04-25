const IRQ_SEQ_INDEX = 0
const IRQ_FLAGS_INDEX = 1
const IRQ_I32_COUNT = 2

export const IRQ_INPUT_READY = 1 << 0
export const IRQ_OUTPUT_READY = 1 << 1
export const IRQ_OUTPUT_DRAINED = 1 << 2
export const IRQ_TIMER = 1 << 3
export const IRQ_SCHEDULER_WAKE = 1 << 4
export const IRQ_LOG_READY = 1 << 5
export const IRQ_HTTP_RPC = 1 << 6
export const IRQ_TIMER_CONTROL = 1 << 7

const WAIT_SLICE_MS = 100

export function createInterruptControllerStorage() {
    const buffer = new SharedArrayBuffer(IRQ_I32_COUNT * 4)
    const i32 = new Int32Array(buffer)
    Atomics.store(i32, IRQ_SEQ_INDEX, 0)
    Atomics.store(i32, IRQ_FLAGS_INDEX, 0)
    return buffer
}

export function attachInterruptController(buffer) {
    const i32 = new Int32Array(buffer, 0, IRQ_I32_COUNT)

    function signal(bits) {
        if (!bits)
            return 0

        Atomics.or(i32, IRQ_FLAGS_INDEX, bits)
        const seq = Atomics.add(i32, IRQ_SEQ_INDEX, 1) + 1
        Atomics.notify(i32, IRQ_SEQ_INDEX)
        return seq
    }

    function take(mask = -1) {
        const normalizedMask = mask >>> 0
        while (true) {
            const flags = Atomics.load(i32, IRQ_FLAGS_INDEX) >>> 0
            const matched = flags & normalizedMask
            if (!matched)
                return 0
            const next = flags & ~matched
            if (Atomics.compareExchange(i32, IRQ_FLAGS_INDEX, flags, next) === flags)
                return matched
        }
    }

    function consume() {
        return take(0xFFFFFFFF)
    }

    function getRemainingTimeout(startedAt, timeoutMs) {
        if (timeoutMs < 0)
            return -1

        const elapsed = Date.now() - startedAt
        if (elapsed >= timeoutMs)
            return 0
        return timeoutMs - elapsed
    }

    function getWaitSliceMs(startedAt, timeoutMs) {
        const remaining = getRemainingTimeout(startedAt, timeoutMs)
        if (remaining < 0)
            return WAIT_SLICE_MS
        return Math.min(remaining, WAIT_SLICE_MS)
    }

    function waitSync(mask = 0xFFFFFFFF, timeoutMs = -1) {
        const startedAt = timeoutMs >= 0 ? Date.now() : 0
        while (true) {
            const seq = Atomics.load(i32, IRQ_SEQ_INDEX)
            const pending = take(mask)
            if (pending)
                return pending

            const waitTimeoutMs = getWaitSliceMs(startedAt, timeoutMs)
            if (waitTimeoutMs === 0)
                return take(mask)

            Atomics.wait(i32, IRQ_SEQ_INDEX, seq, waitTimeoutMs)

            const matched = take(mask)
            if (matched)
                return matched
            if (timeoutMs >= 0 && getRemainingTimeout(startedAt, timeoutMs) === 0)
                return 0
        }
    }

    async function wait(mask = 0xFFFFFFFF, timeoutMs = -1) {
        const startedAt = timeoutMs >= 0 ? Date.now() : 0
        while (true) {
            const seq = Atomics.load(i32, IRQ_SEQ_INDEX)
            const pending = take(mask)
            if (pending)
                return pending

            const waitTimeoutMs = getWaitSliceMs(startedAt, timeoutMs)
            if (waitTimeoutMs === 0)
                return take(mask)

            if (typeof Atomics.waitAsync === 'function') {
                const result = Atomics.waitAsync(i32, IRQ_SEQ_INDEX, seq, waitTimeoutMs)
                if (result.async)
                    await result.value
            } else {
                await new Promise(resolve => globalThis.setTimeout(resolve, waitTimeoutMs))
            }

            const matched = take(mask)
            if (matched)
                return matched
            if (timeoutMs >= 0 && getRemainingTimeout(startedAt, timeoutMs) === 0)
                return 0
        }
    }

    return {
        buffer,
        signal,
        take,
        consume,
        waitSync,
        wait,
    }
}
