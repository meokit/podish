package fs

func (sm *SyscallManager) ReadString(addr uint32) string {
	var b []byte
	for {
		buf := sm.Emu.MemRead(addr, 1)
		if buf[0] == 0 {
			break
		}
		b = append(b, buf[0])
		addr++
	}
	return string(b)
}

func binary_LittleEndian_PutUint32(b []byte, v uint32) {
	b[0] = byte(v)
	b[1] = byte(v >> 8)
	b[2] = byte(v >> 16)
	b[3] = byte(v >> 24)
}

func binary_LittleEndian_PutUint64(b []byte, v uint64) {
	b[0] = byte(v)
	b[1] = byte(v >> 8)
	b[2] = byte(v >> 16)
	b[3] = byte(v >> 24)
	b[4] = byte(v >> 32)
	b[5] = byte(v >> 40)
	b[6] = byte(v >> 48)
	b[7] = byte(v >> 56)
}

func binary_LittleEndian_Uint32(b []byte) uint32 {
	return uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24
}
