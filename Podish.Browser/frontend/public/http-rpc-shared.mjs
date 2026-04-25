export const HTTP_RPC_SLOT_COUNT = 32
export const HTTP_RPC_REQUEST_TEXT_CAPACITY = 4096
export const HTTP_RPC_RESPONSE_CAPACITY = 128 * 1024
export const HTTP_RPC_CHUNK_CAPACITY = HTTP_RPC_RESPONSE_CAPACITY

export const HTTP_RPC_RANGE_MODE_NONE = 0
export const HTTP_RPC_RANGE_MODE_OPEN_ENDED = 1
export const HTTP_RPC_RANGE_MODE_BOUNDED = 2

export const HTTP_RPC_STATE_FREE = 0
export const HTTP_RPC_STATE_BEGIN_PENDING = 1
export const HTTP_RPC_STATE_OPEN = 2
export const HTTP_RPC_STATE_READ_PENDING = 3
export const HTTP_RPC_STATE_CHUNK_READY = 4
export const HTTP_RPC_STATE_EOF = 5
export const HTTP_RPC_STATE_ERROR = 6
export const HTTP_RPC_STATE_CANCELLED = 7

export const HTTP_RPC_OPCODE_STREAM_GET = 1

export const HTTP_RPC_RESULT_OK = 0
export const HTTP_RPC_RESULT_TIMEOUT = -1
export const HTTP_RPC_RESULT_NETWORK_ERROR = -2
export const HTTP_RPC_RESULT_TOO_LARGE = -3
export const HTTP_RPC_RESULT_PENDING = -4
export const HTTP_RPC_RESULT_NO_FREE_SLOT = -5
export const HTTP_RPC_RESULT_INVALID_REQUEST = -6
export const HTTP_RPC_RESULT_CANCELLED = -7
export const HTTP_RPC_RESULT_URL_TOO_LONG = -8

export const HTTP_RPC_FLAG_STARTED = 1 << 0
export const HTTP_RPC_FLAG_CHUNK_READY = 1 << 1
export const HTTP_RPC_FLAG_EOF = 1 << 2
export const HTTP_RPC_FLAG_ERROR = 1 << 3
export const HTTP_RPC_FLAG_CANCELLED = 1 << 4

const HEADER_NEXT_REQUEST_ID_INDEX = 0
const HEADER_I32_COUNT = 1
const SLOT_META_I32_COUNT = 14

const SLOT_META_STATE = 0
const SLOT_META_REQUEST_ID = 1
const SLOT_META_OPCODE = 2
const SLOT_META_FLAGS = 3
const SLOT_META_REQUESTED_LENGTH = 4
const SLOT_META_STATUS_CODE = 5
const SLOT_META_RESULT_CODE = 6
const SLOT_META_RESPONSE_LENGTH = 7
const SLOT_META_TIMEOUT_MS = 8
const SLOT_META_URL_LENGTH = 9
const SLOT_META_RANGE_MODE = 10
const SLOT_META_RANGE_START_LOW = 11
const SLOT_META_RANGE_START_HIGH = 12
const SLOT_META_RANGE_LENGTH = 13

export function createHttpRpcStorage() {
    const metadataBytes = HTTP_RPC_SLOT_COUNT * SLOT_META_I32_COUNT * 4
    const requestBytes = HTTP_RPC_SLOT_COUNT * HTTP_RPC_REQUEST_TEXT_CAPACITY
    const responseBytes = HTTP_RPC_SLOT_COUNT * HTTP_RPC_RESPONSE_CAPACITY
    const totalBytes = HEADER_I32_COUNT * 4 + metadataBytes + requestBytes + responseBytes
    const buffer = new SharedArrayBuffer(totalBytes)
    const header = new Int32Array(buffer, 0, HEADER_I32_COUNT)
    Atomics.store(header, HEADER_NEXT_REQUEST_ID_INDEX, 0)
    return buffer
}

export function attachHttpRpcController(buffer) {
    const header = new Int32Array(buffer, 0, HEADER_I32_COUNT)
    const metadataOffset = HEADER_I32_COUNT * 4
    const metadata = new Int32Array(buffer, metadataOffset, HTTP_RPC_SLOT_COUNT * SLOT_META_I32_COUNT)
    const requestOffset = metadataOffset + HTTP_RPC_SLOT_COUNT * SLOT_META_I32_COUNT * 4
    const responseOffset = requestOffset + HTTP_RPC_SLOT_COUNT * HTTP_RPC_REQUEST_TEXT_CAPACITY
    const requestArena = new Uint8Array(buffer, requestOffset, HTTP_RPC_SLOT_COUNT * HTTP_RPC_REQUEST_TEXT_CAPACITY)
    const responseArena = new Uint8Array(buffer, responseOffset, HTTP_RPC_SLOT_COUNT * HTTP_RPC_RESPONSE_CAPACITY)

    function slotBase(slotId) {
        return slotId * SLOT_META_I32_COUNT
    }

    function slotFieldIndex(slotId, field) {
        return slotBase(slotId) + field
    }

    function requestOffsetForSlot(slotId) {
        return slotId * HTTP_RPC_REQUEST_TEXT_CAPACITY
    }

    function responseOffsetForSlot(slotId) {
        return slotId * HTTP_RPC_RESPONSE_CAPACITY
    }

    function resetSlot(slotId) {
        const base = slotBase(slotId)
        for (let index = 1; index < SLOT_META_I32_COUNT; index += 1)
            Atomics.store(metadata, base + index, 0)
        Atomics.store(metadata, base + SLOT_META_STATE, HTTP_RPC_STATE_FREE)
    }

    function getSlotState(slotId) {
        return Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_STATE)) | 0
    }

    function setFlags(slotId, flags) {
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_FLAGS), flags | 0)
    }

    function getFlags(slotId) {
        return Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_FLAGS)) | 0
    }

    function tryBeginStreamRequest(urlBytes, rangeMode = HTTP_RPC_RANGE_MODE_NONE, rangeStartLow = 0, rangeStartHigh = 0, rangeLength = -1, timeoutMs = -1) {
        if (!(urlBytes instanceof Uint8Array))
            throw new TypeError('urlBytes must be a Uint8Array')
        if (urlBytes.length > HTTP_RPC_REQUEST_TEXT_CAPACITY)
            return HTTP_RPC_RESULT_URL_TOO_LONG
        if (rangeMode !== HTTP_RPC_RANGE_MODE_NONE
            && rangeMode !== HTTP_RPC_RANGE_MODE_OPEN_ENDED
            && rangeMode !== HTTP_RPC_RANGE_MODE_BOUNDED)
            return HTTP_RPC_RESULT_INVALID_REQUEST
        if (rangeMode === HTTP_RPC_RANGE_MODE_NONE && rangeLength >= 0)
            return HTTP_RPC_RESULT_INVALID_REQUEST
        if (rangeMode === HTTP_RPC_RANGE_MODE_BOUNDED && rangeLength < 0)
            return HTTP_RPC_RESULT_INVALID_REQUEST

        for (let slotId = 0; slotId < HTTP_RPC_SLOT_COUNT; slotId += 1) {
            const stateIndex = slotFieldIndex(slotId, SLOT_META_STATE)
            if (Atomics.compareExchange(metadata, stateIndex, HTTP_RPC_STATE_FREE, HTTP_RPC_STATE_BEGIN_PENDING) !== HTTP_RPC_STATE_FREE)
                continue

            const requestId = (Atomics.add(header, HEADER_NEXT_REQUEST_ID_INDEX, 1) + 1) | 0
            const requestBase = requestOffsetForSlot(slotId)
            requestArena.fill(0, requestBase, requestBase + HTTP_RPC_REQUEST_TEXT_CAPACITY)
            requestArena.set(urlBytes, requestBase)

            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID), requestId)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_OPCODE), HTTP_RPC_OPCODE_STREAM_GET)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_REQUESTED_LENGTH), 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATUS_CODE), 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESULT_CODE), 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESPONSE_LENGTH), 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_TIMEOUT_MS), timeoutMs | 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_URL_LENGTH), urlBytes.length | 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_MODE), rangeMode | 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_START_LOW), rangeStartLow | 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_START_HIGH), rangeStartHigh | 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_LENGTH), rangeLength | 0)
            setFlags(slotId, 0)
            return requestId
        }

        console.warn(`[http-rpc/shared] allocate-failed urlLength=${urlBytes.length} rangeMode=${rangeMode} rangeLength=${rangeLength}`)
        return HTTP_RPC_RESULT_NO_FREE_SLOT
    }

    function findSlotByRequestId(requestId) {
        if (!requestId)
            return -1
        for (let slotId = 0; slotId < HTTP_RPC_SLOT_COUNT; slotId += 1) {
            if ((Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID)) | 0) !== (requestId | 0))
                continue
            if (getSlotState(slotId) === HTTP_RPC_STATE_FREE)
                continue
            return slotId
        }
        return -1
    }

    function getRequestFlags(requestId) {
        const slotId = findSlotByRequestId(requestId)
        if (slotId < 0)
            return 0
        return getFlags(slotId)
    }

    function tryScheduleRead(requestId, requestedLength) {
        if (requestedLength < 0) {
            console.warn(`[http-rpc/shared] schedule-invalid requestId=${requestId} requestedLength=${requestedLength}`)
            return HTTP_RPC_RESULT_INVALID_REQUEST
        }
        const slotId = findSlotByRequestId(requestId)
        if (slotId < 0) {
            console.warn(`[http-rpc/shared] schedule-missing requestId=${requestId} requestedLength=${requestedLength}`)
            return HTTP_RPC_RESULT_INVALID_REQUEST
        }
        const state = getSlotState(slotId)
        if (state === HTTP_RPC_STATE_BEGIN_PENDING || state === HTTP_RPC_STATE_READ_PENDING)
            return HTTP_RPC_RESULT_PENDING
        if (state === HTTP_RPC_STATE_CHUNK_READY || state === HTTP_RPC_STATE_EOF)
            return HTTP_RPC_RESULT_OK
        if (state === HTTP_RPC_STATE_ERROR || state === HTTP_RPC_STATE_CANCELLED)
            return Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_RESULT_CODE)) | 0
        if (state !== HTTP_RPC_STATE_OPEN) {
            console.warn(`[http-rpc/shared] schedule-bad-state requestId=${requestId} slot=${slotId} state=${state}`)
            return HTTP_RPC_RESULT_INVALID_REQUEST
        }

        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_REQUESTED_LENGTH), requestedLength | 0)
        return Atomics.compareExchange(
            metadata,
            slotFieldIndex(slotId, SLOT_META_STATE),
            HTTP_RPC_STATE_OPEN,
            HTTP_RPC_STATE_READ_PENDING
        ) === HTTP_RPC_STATE_OPEN
            ? HTTP_RPC_RESULT_PENDING
            : HTTP_RPC_RESULT_PENDING
    }

    function readBeginRequest(slotId) {
        if (slotId < 0 || slotId >= HTTP_RPC_SLOT_COUNT)
            return null
        const state = getSlotState(slotId)
        if (state !== HTTP_RPC_STATE_BEGIN_PENDING)
            return null

        const requestId = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID)) | 0
        const opcode = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_OPCODE)) | 0
        const urlLength = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_URL_LENGTH)) | 0
        const timeoutMs = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_TIMEOUT_MS)) | 0
        const rangeMode = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_MODE)) | 0
        const rangeStartLow = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_START_LOW)) >>> 0
        const rangeStartHigh = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_START_HIGH)) >>> 0
        const rangeLength = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_RANGE_LENGTH)) | 0
        const requestBase = requestOffsetForSlot(slotId)
        const urlBytes = requestArena.slice(requestBase, requestBase + urlLength)

        return {
            slotId,
            state,
            requestId,
            opcode,
            urlBytes,
            timeoutMs,
            rangeMode,
            rangeStartLow,
            rangeStartHigh,
            rangeLength,
        }
    }

    function readPendingChunkRequest(slotId) {
        if (slotId < 0 || slotId >= HTTP_RPC_SLOT_COUNT)
            return null
        if (getSlotState(slotId) !== HTTP_RPC_STATE_READ_PENDING)
            return null

        return {
            slotId,
            requestId: Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID)) | 0,
            requestedLength: Math.max(0, Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUESTED_LENGTH)) | 0),
            timeoutMs: Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_TIMEOUT_MS)) | 0,
        }
    }

    function markStarted(slotId, requestId, statusCode = 200) {
        if ((Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID)) | 0) !== (requestId | 0))
            return false
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATUS_CODE), statusCode | 0)
        setFlags(slotId, HTTP_RPC_FLAG_STARTED)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATE), HTTP_RPC_STATE_OPEN)
        return true
    }

    function writeChunk(slotId, requestId, payloadBytes, statusCode = 200) {
        if ((Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID)) | 0) !== (requestId | 0))
            return false
        if (payloadBytes.length > HTTP_RPC_RESPONSE_CAPACITY)
            return completeFailure(slotId, requestId, HTTP_RPC_RESULT_TOO_LARGE)

        const responseBase = responseOffsetForSlot(slotId)
        responseArena.fill(0, responseBase, responseBase + HTTP_RPC_RESPONSE_CAPACITY)
        responseArena.set(payloadBytes, responseBase)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATUS_CODE), statusCode | 0)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESULT_CODE), HTTP_RPC_RESULT_OK)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESPONSE_LENGTH), payloadBytes.length | 0)
        setFlags(slotId, HTTP_RPC_FLAG_STARTED | HTTP_RPC_FLAG_CHUNK_READY)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATE), HTTP_RPC_STATE_CHUNK_READY)
        return true
    }

    function markEof(slotId, requestId) {
        if ((Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID)) | 0) !== (requestId | 0))
            return false
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESULT_CODE), HTTP_RPC_RESULT_OK)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESPONSE_LENGTH), 0)
        setFlags(slotId, HTTP_RPC_FLAG_STARTED | HTTP_RPC_FLAG_EOF)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATE), HTTP_RPC_STATE_EOF)
        return true
    }

    function completeFailure(slotId, requestId, resultCode) {
        if ((Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_REQUEST_ID)) | 0) !== (requestId | 0))
            return false
        console.warn(`[http-rpc/shared] failure slot=${slotId} requestId=${requestId} resultCode=${resultCode}`)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESULT_CODE), resultCode | 0)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESPONSE_LENGTH), 0)
        Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_REQUESTED_LENGTH), 0)
        const flags = resultCode === HTTP_RPC_RESULT_CANCELLED
            ? HTTP_RPC_FLAG_STARTED | HTTP_RPC_FLAG_CANCELLED
            : HTTP_RPC_FLAG_STARTED | HTTP_RPC_FLAG_ERROR
        setFlags(slotId, flags)
        Atomics.store(
            metadata,
            slotFieldIndex(slotId, SLOT_META_STATE),
            resultCode === HTTP_RPC_RESULT_CANCELLED ? HTTP_RPC_STATE_CANCELLED : HTTP_RPC_STATE_ERROR
        )
        return true
    }

    function tryReadStreamChunkInto(requestId, destinationView, destinationOffset = 0, destinationLength = destinationView?.length ?? 0) {
        const slotId = findSlotByRequestId(requestId)
        if (slotId < 0) {
            console.warn(`[http-rpc/shared] read-missing requestId=${requestId} destinationLength=${destinationLength}`)
            return HTTP_RPC_RESULT_INVALID_REQUEST
        }

        const state = getSlotState(slotId)
        if (state === HTTP_RPC_STATE_BEGIN_PENDING || state === HTTP_RPC_STATE_READ_PENDING)
            return HTTP_RPC_RESULT_PENDING

        if (state === HTTP_RPC_STATE_OPEN) {
            const scheduled = tryScheduleRead(requestId, destinationLength)
            return scheduled === HTTP_RPC_RESULT_OK ? HTTP_RPC_RESULT_PENDING : scheduled
        }

        if (state === HTTP_RPC_STATE_CHUNK_READY) {
            const responseLength = Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_RESPONSE_LENGTH)) | 0
            if (responseLength > destinationLength) {
                return HTTP_RPC_RESULT_TOO_LARGE
            }
            if (responseLength > 0 && destinationView) {
                const responseBase = responseOffsetForSlot(slotId)
                destinationView.set(responseArena.subarray(responseBase, responseBase + responseLength), destinationOffset)
            }
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESPONSE_LENGTH), 0)
            setFlags(slotId, HTTP_RPC_FLAG_STARTED)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATE), HTTP_RPC_STATE_OPEN)
            return responseLength
        }

        if (state === HTTP_RPC_STATE_EOF)
            return 0

        return Atomics.load(metadata, slotFieldIndex(slotId, SLOT_META_RESULT_CODE)) | 0 || HTTP_RPC_RESULT_NETWORK_ERROR
    }

    function closeRequest(requestId, resultCode = HTTP_RPC_RESULT_CANCELLED) {
        const slotId = findSlotByRequestId(requestId)
        if (slotId < 0) {
            console.warn(`[http-rpc/shared] close-missing requestId=${requestId} resultCode=${resultCode}`)
            return false
        }
        if (resultCode === HTTP_RPC_RESULT_CANCELLED) {
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESULT_CODE), resultCode | 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_RESPONSE_LENGTH), 0)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_REQUESTED_LENGTH), 0)
            setFlags(slotId, HTTP_RPC_FLAG_STARTED | HTTP_RPC_FLAG_CANCELLED)
            Atomics.store(metadata, slotFieldIndex(slotId, SLOT_META_STATE), HTTP_RPC_STATE_CANCELLED)
        } else {
            completeFailure(slotId, requestId, resultCode)
        }
        resetSlot(slotId)
        return true
    }

    return {
        buffer,
        slotCount: HTTP_RPC_SLOT_COUNT,
        getSlotState,
        getRequestFlags,
        tryBeginStreamRequest,
        findSlotByRequestId,
        tryScheduleRead,
        readBeginRequest,
        readPendingChunkRequest,
        markStarted,
        writeChunk,
        markEof,
        completeFailure,
        tryReadStreamChunkInto,
        closeRequest,
    }
}
