package fs

import (
	"x86emu-loader/emu"
)

func (sm *SyscallManager) sys_uname(a1, a2, a3, a4, a5, a6 uint32) int32 {
	sm.Emu.MemWrite(a1, []byte("Linux\x00"))
	return 0
}

func (sm *SyscallManager) sys_set_thread_area(a1, a2, a3, a4, a5, a6 uint32) int32 {
	uInfoAddr := a1
	entryNumBuf := sm.Emu.MemRead(uInfoAddr, 4)
	entryNum := int32(binary_LittleEndian_Uint32(entryNumBuf))
	baseAddrBuf := sm.Emu.MemRead(uInfoAddr+4, 4)
	baseAddr := binary_LittleEndian_Uint32(baseAddrBuf)

	sm.Emu.SetSegBase(emu.GS, baseAddr)

	if entryNum == -1 {
		buf := make([]byte, 4)
		binary_LittleEndian_PutUint32(buf, 12)
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
	return 1
}

func (sm *SyscallManager) sys_getpid(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return 1
}
