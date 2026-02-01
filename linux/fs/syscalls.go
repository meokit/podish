package fs

import (
	"fmt"
	"os"
	"x86emu-loader/emu"
	"x86emu-loader/futex"
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

	WorkDir string

	// Clone Handler injected to avoid cyclic dependency with task package
	CloneHandler func(flags int, stack uint32, ptid uint32, tls uint32, ctid uint32) (int32, error)

	// Exit Handler injected.
	// code: exit code. group: true if exit_group (all threads).
	ExitHandler func(eng *emu.Engine, code int, group bool)

	Futex *futex.FutexManager

	// Dependency Injection for GIL
	LockGIL   func()
	UnlockGIL func()

	// Dependency Injection for Process Info
	GetTID  func(*emu.Engine) int
	GetTGID func(*emu.Engine) int

	Strace bool
}

func NewSyscallManager(eng *emu.Engine, mem *mem.VMAManager, brk uint32) *SyscallManager {
	sm := &SyscallManager{
		Emu:     eng,
		Mem:     mem,
		FDs:     make(map[int]*os.File),
		WorkDir: "/tests/linux/rootfs", // hardcoded for now, or use Cwd
		BrkAddr: brk,
		Table:   make(map[uint32]SyscallHandler),
		Futex:   futex.NewFutexManager(),
	}

	sm.FDs[0] = os.Stdin
	sm.FDs[1] = os.Stdout
	sm.FDs[2] = os.Stderr

	sm.initTable()

	return sm
}

func (sm *SyscallManager) Clone(newMem *mem.VMAManager) *SyscallManager {
	// For Fork: We need new FD table but sharing underlying files.
	// Since os.File is a struct pointer, copying the map values is correct (shallow reference).
	newSM := &SyscallManager{
		Emu:       sm.Emu, // Temporarily share Emu? No, Emu is context specific. Task will overwrite.
		Mem:       newMem,
		FDs:       make(map[int]*os.File),
		WorkDir:   sm.WorkDir,
		BrkAddr:   sm.BrkAddr,
		RootFS:    sm.RootFS, // Preserve RootFS
		Cwd:       sm.Cwd,    // Preserve Cwd
		Table:     sm.Table,  // Share the syscall table
		Futex:     sm.Futex,  // Share Futex Manager (threads share waiting queues)
		LockGIL:   sm.LockGIL,
		UnlockGIL: sm.UnlockGIL,
		GetTID:    sm.GetTID,
		GetTGID:   sm.GetTGID,
		Strace:    sm.Strace,
	}
	for k, v := range sm.FDs {
		newSM.FDs[k] = v
	}
	return newSM
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
	sm.Table[119] = sm.sys_sigreturn // sigreturn
	sm.Table[120] = sm.sys_clone
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
	sm.Table[240] = sm.sys_futex
	sm.Table[243] = sm.sys_set_thread_area
	sm.Table[252] = sm.sys_exit_group
	sm.Table[258] = sm.sys_set_tid_address
	sm.Table[383] = sm.sys_statx

	// Stub old fstat?
	sm.Table[108] = func(a1, a2, a3, a4, a5, a6 uint32) int32 { return 0 }
}

func (sm *SyscallManager) Handle(eng *emu.Engine, vec uint32) bool {
	if vec != 0x80 {
		return false
	}

	// Update local pointers for current execution context (GIL held)
	sm.Emu = eng
	// Mem might be shared or process-specific. We should ideally get it from the task.
	// But since SyscallManager is per-process, and threads share process, Mem is mostly same.
	// For Forks, SyscallManager is copied but pointers remain.
	// Let's assume the caller ensures sm is the correct one for the process.

	eax := eng.RegRead(emu.EAX)
	ebx := eng.RegRead(emu.EBX)
	ecx := eng.RegRead(emu.ECX)
	edx := eng.RegRead(emu.EDX)
	esi := eng.RegRead(emu.ESI)
	edi := eng.RegRead(emu.EDI)
	ebp := eng.RegRead(emu.EBP)

	if sm.Strace {
		tid := uint32(0)
		if sm.GetTID != nil {
			tid = uint32(sm.GetTID(sm.Emu))
		}
		fmt.Printf("[%d] syscall(%d, %x, %x, %x, %x, %x, %x)", tid, eax, ebx, ecx, edx, esi, edi, ebp)
	}

	ret := int32(-38) // ENOSYS
	if handler, ok := sm.Table[eax]; ok {
		// fmt.Printf("Syscall %d (EBX=%x ECX=%x EDX=%x)\n", eax, ebx, ecx, edx) // Debug
		ret = handler(ebx, ecx, edx, esi, edi, ebp)
	} else {
		// Only print unimplemented if NOT stracing (since strace prints entry)
		// Or print detailed error
		if !sm.Strace {
			fmt.Printf("Unimplemented Syscall: %d\n", eax)
		}
	}

	if sm.Strace {
		fmt.Printf(" = %d\n", ret)
	}

	sm.Emu.RegWrite(emu.EAX, uint32(ret))
	return true
}
