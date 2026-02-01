package fs

import (
	"os"
	"x86emu-loader/mem"
)

func (sm *SyscallManager) sys_brk(a1, a2, a3, a4, a5, a6 uint32) int32 {
	req := a1
	if req == 0 {
		return int32(sm.BrkAddr)
	}
	if req > sm.BrkAddr {
		start := (sm.BrkAddr + 0xFFF) &^ 0xFFF
		end := (req + 0xFFF) &^ 0xFFF
		if end > start {
			_, err := sm.Mem.Mmap(start, end-start, mem.PROT_READ|mem.PROT_WRITE, mem.MAP_PRIVATE|mem.MAP_ANONYMOUS, nil, 0, 0, "HEAP")
			if err != nil {
				return int32(sm.BrkAddr)
			}
		}
		sm.BrkAddr = req
		return int32(req)
	}
	return int32(sm.BrkAddr)
}

func (sm *SyscallManager) sys_munmap(a1, a2, a3, a4, a5, a6 uint32) int32 {
	sm.Mem.Munmap(a1, a2)
	return 0
}

func (sm *SyscallManager) sys_mmap2(a1, a2, a3, a4, a5, a6 uint32) int32 {
	addr := a1
	length := a2
	prot := int(a3)
	flags := int(a4)
	fd := int(a5)
	offset := int64(a6) * 4096

	var f *os.File
	if fd != -1 {
		if file, ok := sm.GetFD(fd); ok {
			f = file
		} else {
			return -9
		}
	}
	if flags&0x20 != 0 {
		f = nil
	}

	res, err := sm.Mem.Mmap(addr, length, prot, flags, f, offset, int64(length), "MMAP2")
	if err != nil {
		return -12
	}
	return int32(res)
}

func (sm *SyscallManager) sys_mprotect(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return 0
}
