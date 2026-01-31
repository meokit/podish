package loader

import (
    "debug/elf"
    "encoding/binary"
    "fmt"
    "os"
    "x86emu-loader/mem"
)

const (
	StackTop  = 0xC0000000 // 3GB
	StackSize = 0x20000    // 128KB
)

// Auxv types
const (
	AT_NULL   = 0
	AT_IGNORE = 1
	AT_EXECFD = 2
	AT_PHDR   = 3
	AT_PHENT  = 4
	AT_PHNUM  = 5
	AT_PAGESZ = 6
	AT_BASE   = 7
	AT_FLAGS  = 8
	AT_ENTRY  = 9
	AT_NOTELF = 10
	AT_UID    = 11
	AT_EUID   = 12
	AT_GID    = 13
	AT_EGID   = 14
	AT_PLATFORM = 15
	AT_HWCAP  = 16
	AT_CLKTCK = 17
	AT_SECURE = 23
	AT_RANDOM = 25
	AT_EXECFN = 31
)

type LoaderResult struct {
	Entry        uint32
	SP           uint32
	InitialStack []byte
}

func Load(filename string, mm *mem.VMAManager, args []string, envs []string) (*LoaderResult, error) {
	f, err := os.Open(filename)
	if err != nil {
		return nil, err
	}
	// Keep file open as VMAs reference it

	ef, err := elf.NewFile(f)
	if err != nil {
		return nil, err
	}

	loadBase := uint32(0)
	if ef.Type == elf.ET_DYN {
		loadBase = 0x40000000 // PIE base
	}

	// 1. Map LOAD segments
	phdrAddr := uint32(0)
	// Track where headers are in memory
    // If not found in PT_PHDR, we guess.
    
    // We need to read segments.
	
	for i, prog := range ef.Progs {
		if prog.Type == elf.PT_LOAD {
			flags := 0
			if prog.Flags&elf.PF_X != 0 {
				flags |= mem.PROT_EXEC
			}
			if prog.Flags&elf.PF_W != 0 {
				flags |= mem.PROT_WRITE
			}
			if prog.Flags&elf.PF_R != 0 {
				flags |= mem.PROT_READ
			}

			// Align to page boundary
			vaddrRaw := uint32(prog.Vaddr) + loadBase
			offsetRaw := int64(prog.Off)
			sizeRaw := uint32(prog.Memsz)
			
			// Floor to page
			pageStart := vaddrRaw &^ 0xFFF
			pageOffset := offsetRaw &^ 0xFFF // vaddr and offset must be congruent
			diff := vaddrRaw - pageStart
			
			// Calculate new size to cover the range
			totalSize := sizeRaw + diff
            // Align up length to page?
            // Mmap usually takes any length but maps pages. 
            // Our VMA manager likely expects page-aligned length if it just stores it?
            // checking Mmap in vma.go: it checks `addr&0xFFF != 0` but doesn't strictly check len alignment for the VMA itself, 
            // but usually we map whole pages.
            // Let's align length up.
			alignedLen := (totalSize + 0xFFF) &^ 0xFFF

			if sizeRaw > 0 {
                // FileSz limit: prog.Filesz + offset_in_first_page
                fileSzLimit := int64(diff) + int64(prog.Filesz)
				_, err := mm.Mmap(pageStart, alignedLen, flags, mem.MAP_PRIVATE|mem.MAP_FIXED, f, pageOffset, fileSzLimit, "ELF_LOAD")
				if err != nil {
					return nil, fmt.Errorf("mmap failed for seg %d (va=%x off=%x): %v", i, vaddrRaw, offsetRaw, err)
				}
			}
		}
        
        if prog.Type == elf.PT_PHDR {
            phdrAddr = uint32(prog.Vaddr) + loadBase
        }
	}
    
    if phdrAddr == 0 {
        // Simple heuristic: If ET_DYN, usually at base+offset of PHDRs in file
        // If loaded segment covers it.
        // Assuming typical layout where headers are at file start.
        phdrAddr = loadBase + uint32(ef.ByteOrder.Uint32(make([]byte, 4))) // Dummy? No.
        // Just use loadBase + 52 (sizeof ehdr)
         phdrAddr = loadBase + 52
    }

	// 2. Setup Stack
	stackStart := uint32(StackTop - StackSize)
	_, err = mm.Mmap(stackStart, StackSize, mem.PROT_READ|mem.PROT_WRITE, mem.MAP_PRIVATE|mem.MAP_FIXED|mem.MAP_ANONYMOUS, nil, 0, 0, "STACK")
	if err != nil {
		return nil, err
	}
    
    sp := uint32(StackTop)
    stackData := make([]byte, StackSize)
    
    pushBytes := func(b []byte) uint32 {
        sp -= uint32(len(b))
        copy(stackData[sp-stackStart:], b)
        return sp
    }
    
    pushString := func(s string) uint32 {
        b := append([]byte(s), 0)
        return pushBytes(b)
    }
    
    pushUint32 := func(v uint32) {
        sp -= 4
        binary.LittleEndian.PutUint32(stackData[sp-stackStart:], v)
    }

    argPtrs := make([]uint32, len(args))
    for i := len(args) - 1; i >= 0; i-- {
        argPtrs[i] = pushString(args[i])
    }
    
    envPtrs := make([]uint32, len(envs))
    for i := len(envs) - 1; i >= 0; i-- {
        envPtrs[i] = pushString(envs[i])
    }
    
    platPtr := pushString("i686")
    randPtr := pushBytes(make([]byte, 16))

    sp = sp &^ 0xF
    
    pushAux := func(k, v uint32) {
        fmt.Printf(" [Aux] %d -> %x\n", k, v)
        pushUint32(v)
        pushUint32(k)
    }
    
    pushAux(AT_NULL, 0)
    pushAux(AT_PLATFORM, platPtr)
    pushAux(AT_RANDOM, randPtr)
    pushAux(AT_SECURE, 0)
    pushAux(AT_UID, 1000)
    pushAux(AT_EUID, 1000)
    pushAux(AT_GID, 1000)
    pushAux(AT_EGID, 1000)
    // Approximate PHNUM. debug/elf doesn't always give it directly in FileHeader conveniently if extensions used, but Progs len is close enough usually.
    // Actually ef.Progs includes Sections? No, Segments.
    // ELF header has Phnum.
    // We can just use len(ef.Progs) but filter only Load/Phdr?
    // Auxv usually wants what kernel sees.
    // Let's use 0 for now or hardcode reasonable. Linux passes it.
    // We can read it from header manually or just guess.
    // Let's use generic count.
    
    pushAux(AT_PHNUM, uint32(len(ef.Progs))) 
    pushAux(AT_PHENT, 32) 
    pushAux(AT_PHDR, phdrAddr)
    pushAux(AT_PAGESZ, 4096)
    pushAux(AT_ENTRY, uint32(ef.Entry) + loadBase)
    pushAux(AT_BASE, loadBase)
    pushAux(AT_FLAGS, 0)

    pushUint32(0)
    for i := len(envPtrs) - 1; i >= 0; i-- {
        pushUint32(envPtrs[i])
    }
    
    pushUint32(0)
    for i := len(argPtrs) - 1; i >= 0; i-- {
        pushUint32(argPtrs[i])
    }
    
    pushUint32(uint32(len(args)))
    
    fmt.Printf("[Loader] loadBase=%x phdr=%x phnum=%d entry=%x\n", 
        loadBase, phdrAddr, len(ef.Progs), uint32(ef.Entry)+loadBase)
    
    // Dump Headers
    for i, p := range ef.Progs {
        fmt.Printf(" Phdr[%d]: Type=%x Vaddr=%x Memsz=%x\n", i, p.Type, p.Vaddr, p.Memsz)
    }
    // Dump Auxv (need to reconstruct or log during push)
    // We can't easy read back form argptr?
    // Let's just trust variable log.

    return &LoaderResult{
        Entry: uint32(ef.Entry) + loadBase,
        SP: sp,
        InitialStack: stackData[sp-stackStart:],
    }, nil
}
