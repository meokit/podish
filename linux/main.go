package main

import (
	"flag"
	"fmt"
	"os"
	"x86emu-loader/emu"
	"x86emu-loader/fs"
	"x86emu-loader/loader"
	"x86emu-loader/mem"
)

func main() {
	rootfs := flag.String("rootfs", "/", "Path to RootFS")
	flag.Parse()

	args := flag.Args()
	if len(args) < 1 {
		fmt.Println("Usage: loader [--rootfs /path] <native_binary> [args...]")
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
	e.SetFaultHandler(func(addr uint32, isWrite bool) {
		if !mm.HandleFault(addr, isWrite, e) {
			fmt.Printf("Segmentation Fault at 0x%x (Write=%v) EIP=0x%x\n", addr, isWrite, e.Eip())
			fmt.Printf("EAX=%x ECX=%x EDX=%x EBX=%x ESP=%x EBP=%x ESI=%x EDI=%x\n",
				e.RegRead(emu.EAX), e.RegRead(emu.ECX), e.RegRead(emu.EDX), e.RegRead(emu.EBX),
				e.RegRead(emu.ESP), e.RegRead(emu.EBP), e.RegRead(emu.ESI), e.RegRead(emu.EDI))
			e.Stop()

			// Debug: Read stack
			esp := e.RegRead(emu.ESP)
			stack := e.MemRead(esp, 16)
			fmt.Printf("Stack at ESP (%x): %x\n", esp, stack)
			// os.Exit(139)
			// We rely on Stop logic or allow Run to return?
			// X86_Run catches faults?
		}
	})

	// 4. Load ELF
	res, err := loader.Load(exe, mm, exeArgs, envs)
	if err != nil {
		fmt.Printf("Failed to load ELF: %v\n", err)
		os.Exit(1)
	}

	// 5. Setup Stack
	// We rely on the write causing a fault which allocates the pages.
	// If the emulator doesn't support fault-on-write from API, we might crash here.
	// Assuming standard behavior.
	e.MemWrite(res.SP, res.InitialStack)

	// 6. Setup CPU State
	e.SetEip(res.Entry)
	e.RegWrite(emu.ESP, res.SP)
	e.SetEflags(0x202) // IF=1, Reserved=1

	// 7. Setup Syscalls
	sys := fs.NewSyscallManager(e, mm, *rootfs, res.BrkAddr)
	e.SetInterruptHandler(func(vec uint32) bool {
		return sys.Handle(vec)
	})

	fmt.Printf("Starting execution at 0x%x, SP=0x%x\n", res.Entry, res.SP)

	// Debug: Read 16 bytes at entry
	buf := e.MemRead(res.Entry, 16)
	fmt.Printf("Code at entry: %x\n", buf)

	// 8. Run
	// Infinite loop (max insts large)
	e.Run(0, 0)
}
