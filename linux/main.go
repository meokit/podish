package main

import (
	"flag"
	"fmt"
	"os"
	"x86emu-loader/emu"
	"x86emu-loader/fs"
	"x86emu-loader/loader"
	"x86emu-loader/mem"
	"x86emu-loader/task"
)

func main() {
	_ = flag.String("rootfs", "/", "Path to RootFS (Integrated into SyscallManager default now)")
	traceFlag := flag.Bool("trace", false, "Enable syscall tracing")
	flag.Parse()

	args := flag.Args()
	if len(args) < 1 {
		fmt.Println("Usage: loader [--rootfs /path] [--trace] <native_binary> [args...]")
		os.Exit(1)
	}

	exe := args[0]
	exeArgs := args // includes exe name as argv[0]

	// Environment
	envs := os.Environ()

	// 1. Init Emulator
	e := emu.New()
	defer e.Close()

	// 2. Init VMA Manager
	mm := mem.NewVMAManager()

	// 3. Setup Fault Handler
	// This is the bridge between C emulator and Go VMA Manager
	e.SetFaultHandler(func(eng *emu.Engine, addr uint32, isWrite bool) {
		t := task.GetTask(eng)
		if t == nil {
			fmt.Printf("Fault without Task context at %x\n", addr)
			// Panic or try fallback?
			if !mm.HandleFault(addr, isWrite, eng) {
				fmt.Printf("Fallback Fault Failed.\n")
				eng.Stop()
			}
			return
		}

		// Use Task's MM and CPU
		if !t.Process.Mem.HandleFault(addr, isWrite, t.CPU) {
			fmt.Printf("Task %d SegFault at 0x%x EIP=0x%x\n", t.TID, addr, t.CPU.Eip())
			t.CPU.SetStatusFault()
			t.Exited = true
		}
	})

	// 4. Load ELF
	res, err := loader.Load(exe, mm, exeArgs, envs)
	if err != nil {
		fmt.Printf("Failed to load ELF: %v\n", err)
		os.Exit(1)
	}

	// 5. Setup Stack
	e.MemWrite(res.SP, res.InitialStack)

	// 6. Setup CPU State
	e.SetEip(res.Entry)
	e.RegWrite(emu.ESP, res.SP)
	e.SetEflags(0x202) // IF=1, Reserved=1

	// 7. Setup Syscalls
	sys := fs.NewSyscallManager(e, mm, res.BrkAddr)
	sys.Strace = *traceFlag

	// 8. Create Main Task
	mainTask := task.NewTask(e, mm, sys)

	// 9. Inject Clone Handler
	sys.CloneHandler = func(flags int, stack, ptid, tls, ctid uint32) (int32, error) {
		parent := task.CurrentTask() // Try using CurrentTask or registry
		if parent == nil {
			// Fallback to registry-based lookup for the CPU that called cloning
			// But we don't have the CPU here... wait.
			// Actually CurrentTask should be set in RunLoop!
			parent = task.GetTask(e) // This closure captures main 'e', which is wrong for nested clones!
			// BUT wait, sys_clone is a method on SyscallManager.
			// SyscallManager should probably know its Task?
		}

		// Let's use task.GetTask on the Current CPU.
		// We need to pass the engine to the handler.
		// For now, assume single process with multiple threads.
		// All threads share the same 'sys' if it's main threads?
		// No, nested clones will fail if we capture 'e'.

		// FIX: Use task.GetTask(CurrentEngine) or similar?
		// Better: SyscallManager should know its Task.
		// For now, use the parent task we already have.

		t := task.GetTask(task.CurrentCPU()) // task.CurrentCPU must be set in RunLoop
		if t == nil {
			return -1, fmt.Errorf("no current task")
		}

		child, err := t.Clone(flags, stack, ptid, tls, ctid)
		if err != nil {
			return -1, err
		}

		go child.RunLoop()
		return int32(child.TID), nil
	}

	// Inject GIL handlers for futex
	sys.LockGIL = task.LockGIL
	sys.UnlockGIL = task.UnlockGIL

	// Inject PID/TID handlers
	sys.GetTID = func(eng *emu.Engine) int {
		t := task.GetTask(eng)
		if t != nil {
			return t.TID
		}
		return 0
	}
	sys.GetTGID = func(eng *emu.Engine) int {
		t := task.GetTask(eng)
		if t != nil && t.Process != nil {
			return t.Process.TGID
		}
		return 0
	}

	sys.ExitHandler = func(eng *emu.Engine, code int, group bool) {
		t := task.GetTask(eng)
		if t == nil {
			fmt.Printf("Exit called without Task context\n")
			os.Exit(code)
		}

		fmt.Printf("Task %d Exiting (Code %d, Group %v)\n", t.TID, code, group)
		if group {
			os.Exit(code)
		}
		t.ExitCode = code
		t.Exited = true
		t.CPU.Stop()
	}

	e.SetInterruptHandler(func(eng *emu.Engine, vec uint32) bool {
		curr := task.GetTask(eng)
		if curr != nil && curr.Process != nil && curr.Process.FDs != nil {
			return curr.Process.FDs.Handle(eng, vec)
		}
		return sys.Handle(eng, vec)
	})

	fmt.Printf("Starting execution at 0x%x, SP=0x%x\n", res.Entry, res.SP)
	// 10. Run
	mainTask.RunLoop()
}
