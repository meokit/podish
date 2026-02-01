package fs

import (
	"fmt"
	"os"
	"path/filepath"
	"x86emu-loader/emu"
	"x86emu-loader/mem"
)

type SyscallHandler func(a1, a2, a3, a4, a5, a6 uint32) int32

type SyscallManager struct {
	Emu *emu.Engine
	Mem *mem.VMAManager

	RootFS string
	Cwd    string

	FDs map[int]*os.File

	BrkAddr uint32

	Table map[uint32]SyscallHandler
}

func NewSyscallManager(e *emu.Engine, m *mem.VMAManager, rootfs string) *SyscallManager {
	if rootfs == "" {
		rootfs = "/"
	}
	if rootfs != "/" {
		rootfs, _ = filepath.Abs(rootfs)
	}

	sm := &SyscallManager{
		Emu:     e,
		Mem:     m,
		RootFS:  rootfs,
		Cwd:     "/",
		FDs:     make(map[int]*os.File),
		BrkAddr: 0x8000000,
		Table:   make(map[uint32]SyscallHandler),
	}

	sm.FDs[0] = os.Stdin
	sm.FDs[1] = os.Stdout
	sm.FDs[2] = os.Stderr

	sm.initTable()

	return sm
}

func (sm *SyscallManager) initTable() {
	sm.Table[1] = sm.sys_exit
	sm.Table[3] = sm.sys_read
	sm.Table[4] = sm.sys_write
	sm.Table[5] = sm.sys_open
	sm.Table[6] = sm.sys_close
	sm.Table[10] = sm.sys_unlink
	sm.Table[20] = sm.sys_getpid
	sm.Table[33] = sm.sys_access
	sm.Table[45] = sm.sys_brk
	sm.Table[54] = sm.sys_ioctl
	sm.Table[91] = sm.sys_munmap
	sm.Table[122] = sm.sys_uname
	sm.Table[125] = sm.sys_mprotect
	sm.Table[146] = sm.sys_writev
	sm.Table[174] = sm.sys_rt_sigaction
	sm.Table[175] = sm.sys_rt_sigprocmask
	sm.Table[183] = sm.sys_getcwd
	sm.Table[192] = sm.sys_mmap2
	sm.Table[195] = sm.sys_stat64
	sm.Table[196] = sm.sys_lstat64
	sm.Table[197] = sm.sys_fstat64
	sm.Table[220] = sm.sys_getdents64
	sm.Table[243] = sm.sys_set_thread_area
	sm.Table[252] = sm.sys_exit_group
	sm.Table[258] = sm.sys_set_tid_address
	sm.Table[383] = sm.sys_statx

	// Stub old fstat?
	sm.Table[108] = func(a1, a2, a3, a4, a5, a6 uint32) int32 { return 0 }
}

func (sm *SyscallManager) Handle(vec uint32) bool {
	if vec != 0x80 {
		return false
	}

	eax := sm.Emu.RegRead(emu.EAX)
	ebx := sm.Emu.RegRead(emu.EBX)
	ecx := sm.Emu.RegRead(emu.ECX)
	edx := sm.Emu.RegRead(emu.EDX)
	esi := sm.Emu.RegRead(emu.ESI)
	edi := sm.Emu.RegRead(emu.EDI)
	ebp := sm.Emu.RegRead(emu.EBP)

	ret := int32(-38) // ENOSYS
	if handler, ok := sm.Table[eax]; ok {
		ret = handler(ebx, ecx, edx, esi, edi, ebp)
	} else {
		fmt.Printf("Unimplemented Syscall: %d\n", eax)
	}

	sm.Emu.RegWrite(emu.EAX, uint32(ret))
	return true
}
