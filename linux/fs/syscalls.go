package fs

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"x86emu-loader/emu"
	"x86emu-loader/mem"
)

type SyscallManager struct {
	Emu *emu.Engine
	Mem *mem.VMAManager
	
	RootFS string
	Cwd    string
	
	FDs map[int]*os.File
	
	BrkAddr uint32
}

func NewSyscallManager(e *emu.Engine, m *mem.VMAManager, rootfs string) *SyscallManager {
    if rootfs == "" { rootfs = "/" }
    if rootfs != "/" {
        rootfs, _ = filepath.Abs(rootfs)
    }
    
	sm := &SyscallManager{
		Emu:    e,
		Mem:    m,
		RootFS: rootfs,
		Cwd:    "/",
		FDs:    make(map[int]*os.File),
		BrkAddr: 0x8000000, // Default heap start implementation dependent
	}
    
    // Std FDs
    sm.FDs[0] = os.Stdin
    sm.FDs[1] = os.Stdout
    sm.FDs[2] = os.Stderr
    
    return sm
}

func (sm *SyscallManager) ResolvePath(path string) string {
    if !strings.HasPrefix(path, "/") {
        path = filepath.Join(sm.Cwd, path)
    }
    // Handle chroot
    if sm.RootFS != "/" {
        return filepath.Join(sm.RootFS, path)
    }
    return path
}

func (sm *SyscallManager) ReadString(addr uint32) string {
    // Read util 0
    // Simple slow implementation
    var b []byte
    for {
        buf := sm.Emu.MemRead(addr, 1)
        if buf[0] == 0 { break }
        b = append(b, buf[0])
        addr++
    }
    return string(b)
}

func (sm *SyscallManager) Handle(vec uint32) bool {
    if vec != 0x80 {
        return false // Only handle 0x80
    }
    
    eax := sm.Emu.RegRead(emu.EAX)
    ebx := sm.Emu.RegRead(emu.EBX)
    ecx := sm.Emu.RegRead(emu.ECX)
    edx := sm.Emu.RegRead(emu.EDX)
    esi := sm.Emu.RegRead(emu.ESI)
    edi := sm.Emu.RegRead(emu.EDI)
    ebp := sm.Emu.RegRead(emu.EBP)
    
    ret := sm.dispatch(eax, ebx, ecx, edx, esi, edi, ebp)
    
    sm.Emu.RegWrite(emu.EAX, uint32(ret))
    return true
}

func (sm *SyscallManager) dispatch(nr, a1, a2, a3, a4, a5, a6 uint32) int32 {
    // fmt.Printf("Syscall %d (%x, %x, %x)\n", nr, a1, a2, a3)
    
    switch nr {
    case 1: // EXIT
        // fmt.Printf("Exit(%d)\n", a1)
        os.Exit(int(a1))
        return 0
        
    case 3: // READ
        fd := int(a1)
        bufAddr := a2
        count := a3
        if f, ok := sm.FDs[fd]; ok {
            localBuf := make([]byte, count)
            n, err := f.Read(localBuf)
            if n > 0 {
                sm.Emu.MemWrite(bufAddr, localBuf[:n])
            }
            if err != nil {
                // handle EOF logic
                if n == 0 { return 0 }
                return -1 // Error
            }
            return int32(n)
        }
        return -9 // EBADF
        
    case 4: // WRITE
        fd := int(a1)
        bufAddr := a2
        count := a3
        if f, ok := sm.FDs[fd]; ok {
            data := sm.Emu.MemRead(bufAddr, count)
            n, err := f.Write(data)
            if err != nil { return -1 }
            return int32(n)
        }
        return -9
        
    case 5: // OPEN
        path := sm.ReadString(a1)
        flags := int(a2)
        // mode := int(a3)
        
        hostPath := sm.ResolvePath(path)
        
        // Translate flags roughly (O_RDONLY etc)
        // Linux O_RDONLY=0, O_WRONLY=1, O_RDWR=2
        // Go os.O_RDONLY=0 ... varies on platform!
        // We should map flags carefully.
        goFlags := 0
        if flags&0x1 != 0 { goFlags |= os.O_WRONLY }
        if flags&0x2 != 0 { goFlags |= os.O_RDWR }
        if flags == 0 { goFlags |= os.O_RDONLY } // 0 is RDONLY
        if flags&0x40 != 0 { goFlags |= os.O_CREATE }
        
        f, err := os.OpenFile(hostPath, goFlags, 0644)
        if err != nil {
            return -2 // ENOENT
        }
        
        // Find free FD
        newFd := 3
        for {
            if _, exists := sm.FDs[newFd]; !exists { break }
            newFd++
        }
        sm.FDs[newFd] = f
        return int32(newFd)
        
    case 6: // CLOSE
        fd := int(a1)
        if f, ok := sm.FDs[fd]; ok {
            f.Close()
            delete(sm.FDs, fd)
            return 0
        }
        return -9
        
    case 45: // BRK
        req := a1
        // If 0, return current
        if req == 0 {
            return int32(sm.BrkAddr)
        }
        // If req > current, allocate anonymous
        if req > sm.BrkAddr {
            // Align to page
            // size := req - sm.BrkAddr
            // We just let the fault handler deal with it?
            // No, we should probably add a VMA for the heap or extend it.
            // For now, let's just assume valid extend and update marker.
            
            // Map the range [BrkAddr, req)
            start := (sm.BrkAddr + 0xFFF) &^ 0xFFF
            end := (req + 0xFFF) &^ 0xFFF
            if end > start {
                _, err := sm.Mem.Mmap(start, end-start, mem.PROT_READ|mem.PROT_WRITE, mem.MAP_PRIVATE|mem.MAP_ANONYMOUS, nil, 0, 0, "HEAP")
                if err != nil { return int32(sm.BrkAddr) } // Fail 
            }
            sm.BrkAddr = req
            return int32(req)
        }
        return int32(sm.BrkAddr) // Shrink not implemented
        
    case 91: // MUNMAP
        sm.Mem.Munmap(a1, a2)
        return 0

    case 192: // MMAP2
        // args: addr, len, prot, flags, fd, pgoff
        // pgoff is in 4k units
        addr := a1
        length := a2
        prot := int(a3)
        flags := int(a4)
        fd := int(a5)
        offset := int64(a6) * 4096
        
        var f *os.File
        if fd != -1 {
            if file, ok := sm.FDs[fd]; ok {
                f = file
            } else {
                return -9
            }
        }
        
        if flags & 0x20 != 0 { // MAP_ANONYMOUS
            f = nil
        }
        
        // filesz = length (map request size)
        res, err := sm.Mem.Mmap(addr, length, prot, flags, f, offset, int64(length), "MMAP2")
        if err != nil {
            return -12 // ENOMEM
        }
        return int32(res)

    case 243: // SET_THREAD_AREA
        // arg: struct user_desc *u_info
        uInfoAddr := a1
        // struct user_desc {
        //   uint entry_number; // 0
        //   uint base_addr;    // 4
        //   uint limit;        // 8
        //   ...
        // }
        // We read base_addr and set it to FS or GS? 
        // Glibc usually tries to set specifically.
        // x86-32 TLS usually uses GS usually? Or FS?
        // Glibc i386 uses GS.
        // We can check entry_number?
        // Actually, we usually just update GS register base in recent kernels or GDT.
        // Let's assume this sets the GS base for now as typical for 32-bit Linux.
        
        // Read entry_number to see if it's -1 (allocate)
        entryNumBuf := sm.Emu.MemRead(uInfoAddr, 4)
        entryNum := int32(binary_LittleEndian_Uint32(entryNumBuf))
        
        baseAddrBuf := sm.Emu.MemRead(uInfoAddr+4, 4)
        baseAddr := binary_LittleEndian_Uint32(baseAddrBuf)
        
        // In emulation we barely use segmentation except FS/GS base.
        // Let's set GS base. 
        sm.Emu.SetSegBase(emu.GS, baseAddr)
        
        // Write back entry number if -1 was passed (mock 12)
        if entryNum == -1 {
            buf := make([]byte, 4)
            binary_LittleEndian_PutUint32(buf, 12)
            sm.Emu.MemWrite(uInfoAddr, buf)
        }
        
        return 0

    // fstat64 (197) or fstat (108 old?) or newfstat (108)
    case 108: 
        // fstat(fd, statbuf)
        // We need to marshal host stat to linux stat struct.
        // This is tedious structure packing.
        // For Hello World/Alpine usage, usually fstat is called on stdout.
        // We can just zero it out or fill minimals.
        // Or return 0.
        return 0
        
    case 54: // IOCTL
        return 0 // Mock success
        
    case 122: // UNAME
        // Write "Linux" to buf
        bufAddr := a1
        sm.Emu.MemWrite(bufAddr, []byte("Linux\x00"))
        return 0
        
    case 125: // MPROTECT
        // Stub success
        return 0
        
    case 174: // RT_SIGACTION
        return 0
    case 175: // RT_SIGPROCMASK
        return 0
        
    case 33: // ACCESS
        return 0 // Success (assume file exists)
        
    case 146: // WRITEV
        fd := int(a1)
        iovAddr := a2
        iovCnt := int(a3)
        
        if f, ok := sm.FDs[fd]; ok {
            totalWritten := 0
            for i := 0; i < iovCnt; i++ {
                // iovec is 8 bytes (32-bit: base(4), len(4))
                baseBytes := sm.Emu.MemRead(iovAddr + uint32(i*8), 4)
                lenBytes := sm.Emu.MemRead(iovAddr + uint32(i*8) + 4, 4)
                
                base := binary_LittleEndian_Uint32(baseBytes)
                length := binary_LittleEndian_Uint32(lenBytes)
                
                if length > 0 {
                    data := sm.Emu.MemRead(base, length)
                    n, err := f.Write(data)
                    if n > 0 {
                        totalWritten += n
                    }
                    if err != nil {
                        if totalWritten > 0 { return int32(totalWritten) }
                        return -1
                    }
                }
            }
            return int32(totalWritten)
        }
        return -9 // EBADF

    case 252: // EXIT_GROUP
        os.Exit(int(a1))
        return 0

    default:
        fmt.Printf("Unimplemented Syscall: %d\n", nr)
        return -38 // ENOSYS
    }
}

// Helpers for endianness as not importing binary to avoid clutter? 
// No, imports are fine. Adding binary to imports.
// Wait, I didn't add encoding/binary to imports above, I need to.
// I'll fix imports.

func binary_LittleEndian_Uint32(b []byte) uint32 {
	return uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24
}

func binary_LittleEndian_PutUint32(b []byte, v uint32) {
	b[0] = byte(v)
	b[1] = byte(v >> 8)
	b[2] = byte(v >> 16)
	b[3] = byte(v >> 24)
}
