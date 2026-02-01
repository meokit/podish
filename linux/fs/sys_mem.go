package fs

import (
	"fmt"
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
	fd := int32(a5)
	offset := int64(a6) * 4096

	// WORKAROUND: Force RW permissions because sys_mprotect is a stub.
	// Applications (like Musl) often mmap with PROT_NONE and then mprotect RW.
	// Since we don't implement mprotect yet, valid memory remains PROT_NONE and faults.
	prot = prot | mem.PROT_READ | mem.PROT_WRITE

	var f *os.File
	isAnon := (flags & 0x20) != 0

	if !isAnon && fd != -1 {
		if file, ok := sm.GetFD(int(fd)); ok {
			f = file
		} else {
			return -9
		}
	} else if isAnon {
		f = nil
	}

	res, err := sm.Mem.Mmap(addr, length, prot, flags, f, offset, int64(length), "MMAP2")
	if err != nil {
		fmt.Printf("sys_mmap2(0x%x, %d) failed: %v\n", addr, length, err)
		return -12
	}
	// fmt.Printf("sys_mmap2(0x%x, %d) -> 0x%x\n", addr, length, res)
	return int32(res)
}

func (sm *SyscallManager) sys_mprotect(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return 0
}
