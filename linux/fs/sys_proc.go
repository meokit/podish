package fs

import (
	"encoding/binary"
	"x86emu-loader/emu"
)

func (sm *SyscallManager) sys_uname(a1, a2, a3, a4, a5, a6 uint32) int32 {
	sm.Emu.MemWrite(a1, []byte("Linux\x00"))
	return 0
}

func (sm *SyscallManager) sys_set_thread_area(a1, a2, a3, a4, a5, a6 uint32) int32 {
	uInfoAddr := a1
	entryNumBuf := sm.Emu.MemRead(uInfoAddr, 4)
	if len(entryNumBuf) != 4 {
		return -14
	}
	entryNum := int32(binary.LittleEndian.Uint32(entryNumBuf))
	baseAddrBuf := sm.Emu.MemRead(uInfoAddr+4, 4)
	if len(baseAddrBuf) != 4 {
		return -14
	}
	baseAddr := binary.LittleEndian.Uint32(baseAddrBuf)

	sm.Emu.SetSegBase(emu.GS, baseAddr)

	if entryNum == -1 {
		buf := make([]byte, 4)
		binary.LittleEndian.PutUint32(buf, 12)
		sm.Emu.MemWrite(uInfoAddr, buf)
	}
	return 0
}

func (sm *SyscallManager) sys_rt_sigaction(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return 0
}

func (sm *SyscallManager) sys_rt_sigprocmask(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return 0
}

func (sm *SyscallManager) sys_set_tid_address(a1, a2, a3, a4, a5, a6 uint32) int32 {
	// a1 is pointer to clear on exit. logic handled by clone flag CLONE_CHILD_CLEARTID usually?
	// But set_tid_address updates the clear-tid address.
	// We should probably store it in Task?
	// For now, just return TID as expected.
	if sm.GetTID != nil {
		return int32(sm.GetTID(sm.Emu))
	}
	return 1
}

func (sm *SyscallManager) sys_sigreturn(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return 0
}

func (sm *SyscallManager) sys_clone(flags, stack, ptid, tls, ctid, unused uint32) int32 {
	if sm.CloneHandler == nil {
		return -38 // ENOSYS
	}
	// flags is arg1 (EBX), stack is arg2 (ECX)
	// ptid is arg3 (EDX), tls is arg4 (ESI), ctid is arg5 (EDI)
	tid, err := sm.CloneHandler(int(flags), stack, ptid, tls, ctid)
	if err != nil {
		return -11 // EAGAIN
	}
	return tid
}

func (sm *SyscallManager) sys_futex(uaddr, op, val, timeout, uaddr2, val3 uint32) int32 {
	opCode := op & 0x7F // basic op code, ignore private flag for now
	// FUTEX_WAIT = 0
	// FUTEX_WAKE = 1

	switch opCode {
	case 0: // FUTEX_WAIT
		// Atomically check if *uaddr == val
		// We are holding GIL, so emulator memory access is safe from other threads.

		valBuf := sm.Emu.MemRead(uaddr, 4)
		if len(valBuf) != 4 {
			return -14 // EFAULT
		}
		currentVal := binary.LittleEndian.Uint32(valBuf)

		if currentVal != val {
			return -11 // EWOULDBLOCK
		}

		// Prepare wait
		waiter := sm.Futex.PrepareWait(uaddr)

		// Release GIL to let others run
		if sm.UnlockGIL != nil {
			sm.UnlockGIL()
		}

		// Wait
		// TODO: Handle timeout
		select {
		case <-waiter.C:
			// Woken up
		}

		// Re-acquire GIL
		if sm.LockGIL != nil {
			sm.LockGIL()
		}
		return 0

	case 1: // FUTEX_WAKE
		count := int(val)
		woken := sm.Futex.Wake(uaddr, count)
		return int32(woken)

	default:
		// fmt.Printf("sys_futex: Unimplemented op %d\n", op)
		return -38 // ENOSYS
	}
}

func (sm *SyscallManager) sys_getpid(a1, a2, a3, a4, a5, a6 uint32) int32 {
	if sm.GetTGID != nil {
		return int32(sm.GetTGID(sm.Emu))
	}
	return 1000
}
