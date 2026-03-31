const HEADER_I32_COUNT = 4
const READ_INDEX = 0
const WRITE_INDEX = 1
const DROPPED_INDEX = 2
const RESERVED_INDEX = 3
const PACKET_HEADER_BYTES = 8

export const EVENT_INPUT_BYTES = 1
export const EVENT_OUTPUT_BYTES = 2
export const EVENT_RESIZE = 3
export const EVENT_CONTROL = 4

function align4(value) {
  return (value + 3) & ~3
}

export function getRequiredBytes(capacity) {
  return HEADER_I32_COUNT * 4 + capacity
}

export function createQueueStorage(capacity) {
  const buffer = new SharedArrayBuffer(getRequiredBytes(capacity))
  const i32 = new Int32Array(buffer, 0, HEADER_I32_COUNT)
  const u8 = new Uint8Array(buffer, HEADER_I32_COUNT * 4)
  Atomics.store(i32, READ_INDEX, 0)
  Atomics.store(i32, WRITE_INDEX, 0)
  Atomics.store(i32, DROPPED_INDEX, 0)
  Atomics.store(i32, RESERVED_INDEX, 0)
  u8.fill(0)
  return buffer
}

export function attachQueue(buffer, capacity) {
  const i32 = new Int32Array(buffer, 0, HEADER_I32_COUNT)
  const u8 = new Uint8Array(buffer, HEADER_I32_COUNT * 4)
  const dataView = new DataView(buffer, HEADER_I32_COUNT * 4)

  function writeBytesAt(cursor, data, dataOffset = 0, dataLength = (data?.length ?? 0) - dataOffset) {
    if (!dataLength)
      return

    const offset = cursor % capacity
    const first = Math.min(dataLength, capacity - offset)
    u8.set(data.subarray(dataOffset, dataOffset + first), offset)
    if (dataLength > first)
      u8.set(data.subarray(dataOffset + first, dataOffset + dataLength), 0)
  }

  function tryReserveWrite(length) {
    if (length > capacity) {
      Atomics.add(i32, DROPPED_INDEX, 1)
      return null
    }

    const writeCursor = Atomics.load(i32, WRITE_INDEX) >>> 0
    const readCursor = Atomics.load(i32, READ_INDEX) >>> 0
    const readable = (writeCursor - readCursor) >>> 0
    const writable = capacity - readable
    if (length > writable) {
      Atomics.add(i32, DROPPED_INDEX, 1)
      return null
    }

    Atomics.store(i32, RESERVED_INDEX, length >>> 0)
    return writeCursor
  }

  function commitReservedWrite(writeCursor, length) {
    Atomics.store(i32, RESERVED_INDEX, 0)
    Atomics.store(i32, WRITE_INDEX, (writeCursor + length) >>> 0)
    Atomics.notify(i32, WRITE_INDEX)
  }

  function readableBytes() {
    const writeCursor = Atomics.load(i32, WRITE_INDEX) >>> 0
    const readCursor = Atomics.load(i32, READ_INDEX) >>> 0
    return (writeCursor - readCursor) >>> 0
  }

  function writableBytes() {
    return capacity - readableBytes()
  }

  function tryWriteLossy(data) {
    if (!data?.length)
      return 0
    if (data.length > capacity) {
      Atomics.add(i32, DROPPED_INDEX, 1)
      return 0
    }

    const writeCursor = Atomics.load(i32, WRITE_INDEX) >>> 0
    const readCursor = Atomics.load(i32, READ_INDEX) >>> 0
    const readable = (writeCursor - readCursor) >>> 0
    const writable = capacity - readable
    if (data.length > writable) {
      Atomics.add(i32, DROPPED_INDEX, 1)
      return 0
    }

    const offset = writeCursor % capacity
    const first = Math.min(data.length, capacity - offset)
    u8.set(data.subarray(0, first), offset)
    if (data.length > first)
      u8.set(data.subarray(first), 0)

    Atomics.store(i32, WRITE_INDEX, (writeCursor + data.length) >>> 0)
    Atomics.notify(i32, WRITE_INDEX)
    return data.length
  }

  function tryWritePacketLossy(eventType, payload = new Uint8Array(0)) {
    const payloadLength = payload?.length ?? 0
    const totalLength = PACKET_HEADER_BYTES + payloadLength
    const recordLength = align4(totalLength)
    const writeCursor = tryReserveWrite(recordLength)
    if (writeCursor === null)
      return 0

    const header = new Uint8Array(PACKET_HEADER_BYTES)
    const headerView = new DataView(header.buffer)
    headerView.setUint32(0, totalLength, true)
    headerView.setUint32(4, eventType >>> 0, true)
    writeBytesAt(writeCursor, header)
    if (payloadLength > 0)
      writeBytesAt(writeCursor + PACKET_HEADER_BYTES, payload)

    const padding = recordLength - totalLength
    if (padding > 0)
      writeBytesAt(writeCursor + totalLength, new Uint8Array(padding))

    commitReservedWrite(writeCursor, recordLength)
    return totalLength
  }

  function tryWritePacketPartsLossy(eventType, parts = [], payloadLength = -1) {
    let computedPayloadLength = payloadLength >>> 0
    if (payloadLength < 0) {
      computedPayloadLength = 0
      for (const part of parts)
        computedPayloadLength += part?.length ?? 0
    }

    const totalLength = PACKET_HEADER_BYTES + computedPayloadLength
    const recordLength = align4(totalLength)
    const writeCursor = tryReserveWrite(recordLength)
    if (writeCursor === null)
      return 0

    const header = new Uint8Array(PACKET_HEADER_BYTES)
    const headerView = new DataView(header.buffer)
    headerView.setUint32(0, totalLength, true)
    headerView.setUint32(4, eventType >>> 0, true)
    writeBytesAt(writeCursor, header)

    let payloadCursor = writeCursor + PACKET_HEADER_BYTES
    for (const part of parts) {
      const length = part?.length ?? 0
      if (!length)
        continue
      writeBytesAt(payloadCursor, part)
      payloadCursor += length
    }

    const padding = recordLength - totalLength
    if (padding > 0)
      writeBytesAt(writeCursor + totalLength, new Uint8Array(padding))

    commitReservedWrite(writeCursor, recordLength)
    return totalLength
  }

  function tryWritePacketLeadingByteLossy(eventType, leadingByte, payload = new Uint8Array(0), payloadLength = -1) {
    const tailLength = payloadLength >= 0 ? payloadLength >>> 0 : (payload?.length ?? 0)
    const computedPayloadLength = 1 + tailLength
    const totalLength = PACKET_HEADER_BYTES + computedPayloadLength
    const recordLength = align4(totalLength)
    const writeCursor = tryReserveWrite(recordLength)
    if (writeCursor === null)
      return 0

    const header = new Uint8Array(PACKET_HEADER_BYTES)
    const headerView = new DataView(header.buffer)
    headerView.setUint32(0, totalLength, true)
    headerView.setUint32(4, eventType >>> 0, true)
    writeBytesAt(writeCursor, header)

    u8[(writeCursor + PACKET_HEADER_BYTES) % capacity] = leadingByte & 0xFF
    if (tailLength > 0)
      writeBytesAt(writeCursor + PACKET_HEADER_BYTES + 1, payload, 0, tailLength)

    const padding = recordLength - totalLength
    if (padding > 0)
      writeBytesAt(writeCursor + totalLength, new Uint8Array(padding))

    commitReservedWrite(writeCursor, recordLength)
    return totalLength
  }

  function tryRead(maxBytes = capacity) {
    const writeCursor = Atomics.load(i32, WRITE_INDEX) >>> 0
    const readCursor = Atomics.load(i32, READ_INDEX) >>> 0
    const readable = (writeCursor - readCursor) >>> 0
    if (!readable)
      return null

    const length = Math.min(maxBytes, readable)
    const result = new Uint8Array(length)
    const offset = readCursor % capacity
    const first = Math.min(length, capacity - offset)
    result.set(u8.subarray(offset, offset + first), 0)
    if (length > first)
      result.set(u8.subarray(0, length - first), first)

    Atomics.store(i32, READ_INDEX, (readCursor + length) >>> 0)
    Atomics.notify(i32, READ_INDEX)
    return result
  }

  function peekPacketHeader() {
    const writeCursor = Atomics.load(i32, WRITE_INDEX) >>> 0
    const readCursor = Atomics.load(i32, READ_INDEX) >>> 0
    const readable = (writeCursor - readCursor) >>> 0
    if (readable < PACKET_HEADER_BYTES)
      return null

    const offset = readCursor % capacity
    if (offset + PACKET_HEADER_BYTES <= capacity) {
      return {
        totalLength: dataView.getUint32(offset, true),
        eventType: dataView.getUint32(offset + 4, true),
      }
    }

    const header = new Uint8Array(PACKET_HEADER_BYTES)
    const first = capacity - offset
    header.set(u8.subarray(offset, offset + first), 0)
    header.set(u8.subarray(0, PACKET_HEADER_BYTES - first), first)
    const view = new DataView(header.buffer)
    return {
      totalLength: view.getUint32(0, true),
      eventType: view.getUint32(4, true),
    }
  }

  function tryReadPacket() {
    const header = peekPacketHeader()
    if (!header)
      return null

    const recordLength = align4(header.totalLength)
    const writeCursor = Atomics.load(i32, WRITE_INDEX) >>> 0
    const readCursor = Atomics.load(i32, READ_INDEX) >>> 0
    const readable = (writeCursor - readCursor) >>> 0
    if (header.totalLength < PACKET_HEADER_BYTES || recordLength > readable)
      return null

    const packet = tryRead(recordLength)
    if (!packet)
      return null

    return {
      eventType: header.eventType,
      payload: packet.slice(PACKET_HEADER_BYTES, header.totalLength),
      totalLength: header.totalLength,
    }
  }

  function droppedWrites() {
    return Atomics.load(i32, DROPPED_INDEX) >>> 0
  }

  return {
    capacity,
    buffer,
    readableBytes,
    writableBytes,
    tryWriteLossy,
    tryWritePacketLossy,
    tryWritePacketPartsLossy,
    tryWritePacketLeadingByteLossy,
    tryRead,
    tryReadPacket,
    droppedWrites,
  }
}
