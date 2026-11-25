import assert from 'node:assert/strict'
import test from 'node:test'
import {
    attachHttpRpcController,
    createHttpRpcStorage,
    HTTP_RPC_FLAG_CANCELLED,
    HTTP_RPC_FLAG_ERROR,
    HTTP_RPC_RANGE_MODE_BOUNDED,
    HTTP_RPC_RESULT_CANCELLED,
    HTTP_RPC_RESULT_NO_FREE_SLOT,
    HTTP_RPC_RESULT_PENDING,
    HTTP_RPC_RESULT_TIMEOUT,
} from '../../Podish.Browser/frontend/public/http-rpc-shared.mjs'

const url = new TextEncoder().encode('https://example.invalid/layer')

function begin(controller, asynchronous = true) {
    return controller.tryBeginStreamRequest(url, HTTP_RPC_RANGE_MODE_BOUNDED, 4096, 0, 4096, 5000, asynchronous)
}

test('async saturation leaves the pager slot free and a closed request can be reclaimed', () => {
    const controller = attachHttpRpcController(createHttpRpcStorage())
    const requests = Array.from({length: controller.slotCount - 1}, () => begin(controller))
    assert.equal(new Set(requests).size, requests.length)
    assert.ok(requests.every(id => id > 0))
    assert.equal(begin(controller), HTTP_RPC_RESULT_NO_FREE_SLOT)
    const pagerRequest = begin(controller, false)
    assert.ok(pagerRequest > 0)
    assert.equal(controller.findSlotByRequestId(pagerRequest), 0)

    const closed = requests[0]
    const closedSlot = controller.findSlotByRequestId(closed)
    assert.ok(controller.closeRequest(closed))
    assert.equal(begin(controller), HTTP_RPC_RESULT_NO_FREE_SLOT)
    assert.ok(controller.reclaimClosedRequests())
    const replacement = begin(controller)
    assert.ok(replacement > 0)
    assert.notEqual(replacement, closed)
    assert.equal(controller.findSlotByRequestId(replacement), closedSlot)
    assert.equal(controller.findSlotByRequestId(closed), -1)

    assert.equal(controller.markStarted(closedSlot, closed), false)
    assert.equal(controller.completeFailure(closedSlot, closed, HTTP_RPC_RESULT_TIMEOUT), false)
    assert.equal(controller.writeChunk(closedSlot, closed, new Uint8Array([42])), false)
    assert.equal(controller.tryReadStreamChunkInto(replacement, new Uint8Array(1)), HTTP_RPC_RESULT_PENDING)
})

test('simultaneous streams preserve request identity, data and terminal status', () => {
    const shared = createHttpRpcStorage()
    const requester = attachHttpRpcController(shared)
    const responder = attachHttpRpcController(shared)
    const copy = begin(requester)
    const timeout = begin(requester)
    const cancelled = begin(requester)
    const copySlot = responder.findSlotByRequestId(copy)
    const timeoutSlot = responder.findSlotByRequestId(timeout)
    const cancelledSlot = responder.findSlotByRequestId(cancelled)
    const destination = new Uint8Array(4)

    for (const [slot, id] of [[copySlot, copy], [timeoutSlot, timeout], [cancelledSlot, cancelled]]) {
        assert.ok(responder.markStarted(slot, id))
        assert.equal(requester.tryReadStreamChunkInto(id, destination), HTTP_RPC_RESULT_PENDING)
        assert.equal(responder.readPendingChunkRequest(slot).requestId, id)
    }

    assert.ok(responder.completeFailure(timeoutSlot, timeout, HTTP_RPC_RESULT_TIMEOUT))
    assert.ok(responder.completeFailure(cancelledSlot, cancelled, HTTP_RPC_RESULT_CANCELLED))
    assert.equal(requester.tryReadStreamChunkInto(timeout, destination), HTTP_RPC_RESULT_TIMEOUT)
    assert.equal(requester.tryReadStreamChunkInto(cancelled, destination), HTTP_RPC_RESULT_CANCELLED)
    assert.ok(requester.getRequestFlags(timeout) & HTTP_RPC_FLAG_ERROR)
    assert.ok(requester.getRequestFlags(cancelled) & HTTP_RPC_FLAG_CANCELLED)
    assert.deepEqual(destination, new Uint8Array(4))
    assert.ok(responder.writeChunk(copySlot, copy, new Uint8Array([1, 3, 5, 7])))
    assert.equal(requester.tryReadStreamChunkInto(copy, destination), 4)
    assert.deepEqual(destination, new Uint8Array([1, 3, 5, 7]))

    assert.equal(requester.tryReadStreamChunkInto(copy, destination), HTTP_RPC_RESULT_PENDING)
    assert.ok(responder.markEof(copySlot, copy))
    assert.equal(requester.tryReadStreamChunkInto(copy, destination), 0)
})
