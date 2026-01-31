package mem

import (
	"container/list"
	"fmt"
	"io"
	"os"
)

const (
	PROT_READ  = 0x1
	PROT_WRITE = 0x2
	PROT_EXEC  = 0x4
	PROT_NONE  = 0x0

	MAP_SHARED    = 0x01
	MAP_PRIVATE   = 0x02
	MAP_FIXED     = 0x10
	MAP_ANONYMOUS = 0x20
)

type VMA struct {
	Start  uint32
	End    uint32 // Exclusive
	Perms  int
	Flags  int
	File   *os.File
	Offset int64
    FileSz int64 // Max bytes to read from file relative to Start
    // Name for debugging
    Name string
}

type VMAManager struct {
	vmas *list.List
}

func NewVMAManager() *VMAManager {
	return &VMAManager{
		vmas: list.New(),
	}
}

func (mm *VMAManager) FindVMA(addr uint32) *VMA {
	for e := mm.vmas.Front(); e != nil; e = e.Next() {
		vma := e.Value.(*VMA)
		if addr >= vma.Start && addr < vma.End {
			return vma
		}
	}
	return nil
}

// Mmap adds a new VMA. Simplified logic: straightforward insertion, no merging/splitting for now.
func (mm *VMAManager) Mmap(addr uint32, len uint32, perms int, flags int, file *os.File, offset int64, filesz int64, name string) (uint32, error) {
	// Align to 4k
	if addr&0xFFF != 0 {
		return 0, fmt.Errorf("addr not aligned")
	}
    // TODO: if addr is 0 and not fixed, find a free spot. 
    // For now we assume the loader/ELF provides valid fixed addresses or we pick a simple one.
    if addr == 0 {
        // Simple allocator: Start at 0x40000000 (1GB) and go up?
        // Or specific base.
        // Let's implement a rudimentary linear search for free and valid space if needed.
        // But ELF loader usually specifies address or we pick one for ET_DYN.
        // Helper allocator:
        addr = mm.findFreeRegion(len)
        if addr == 0 {
             return 0, fmt.Errorf("execution out of memory")
        }
    }

	end := addr + len
    // Check overlap
    if mm.checkOverlap(addr, end) {
        if flags & MAP_FIXED != 0 {
             // Overwrite? Implementation detail. Linux unmaps strictly. 
             // We'll just Error for simplicity or Unmap.
             mm.Munmap(addr, len)
        } else {
             return 0, fmt.Errorf("overlap detected")
        }
    }

	vma := &VMA{
		Start:  addr,
		End:    end,
		Perms:  perms,
		Flags:  flags,
		File:   file,
		Offset: offset,
        FileSz: filesz,
        Name:   name,
	}
    
    // Insert sorted
    inserted := false
    for e := mm.vmas.Front(); e != nil; e = e.Next() {
        curr := e.Value.(*VMA)
        if vma.End <= curr.Start {
            mm.vmas.InsertBefore(vma, e)
            inserted = true
            break
        }
    }
    if !inserted {
        mm.vmas.PushBack(vma)
    }

	return addr, nil
}

func (mm *VMAManager) Munmap(addr uint32, len uint32) {
    // Basic implementation: Remove any VMA that is fully contained. 
    // Partial unmap not supported in this simple version.
    var next *list.Element
    for e := mm.vmas.Front(); e != nil; e = next {
        next = e.Next()
        vma := e.Value.(*VMA)
        // Check intersection
        if addr < vma.End && (addr+len) > vma.Start {
            // Remove whole VMA for now
            mm.vmas.Remove(e)
        }
    }
}

func (mm *VMAManager) checkOverlap(start, end uint32) bool {
    for e := mm.vmas.Front(); e != nil; e = e.Next() {
        vma := e.Value.(*VMA)
        if start < vma.End && end > vma.Start {
            return true
        }
    }
    return false
}

func (mm *VMAManager) findFreeRegion(size uint32) uint32 {
    base := uint32(0x10000) // Start low-ish
    // Simple scan
    for {
        end := base + size
        if !mm.checkOverlap(base, end) {
            return base
        }
        base += 0x1000 // Align 4k
        if base >= 0xC0000000 { // Kernel limit
            return 0
        }
    }
}

// Mapper interface to avoid cyclic dependency
type Mapper interface {
    MemMap(addr uint32, size uint32, perms int)
    MemWrite(addr uint32, data []byte)
}

// HandleFault returns true if handled
func (mm *VMAManager) HandleFault(addr uint32, isWrite bool, mapper Mapper) bool {
    // 1. Find VMA
    vma := mm.FindVMA(addr)
    if vma == nil {
        fmt.Printf("[Fault] No VMA for %x\n", addr)
        return false
    }

    // 2. Check Permissions
    if isWrite && (vma.Perms&PROT_WRITE == 0) {
        fmt.Printf("[Fault] Permission Denied (Write) for %x\n", addr)
        return false
    }
    // Read is usually implied
    
    // 3. Map page (Temporary RW for loading)
    pageStart := addr &^ 0xFFF
    tempPerms := vma.Perms | PROT_WRITE
    mapper.MemMap(pageStart, 4096, tempPerms)
    
    // 4. Fill Data
    if vma.File != nil {
        vmaOffset := int64(pageStart - vma.Start)
        off := vma.Offset + vmaOffset
        
        // Calculate max bytes for file
        readLen := 4096
        if vma.FileSz > 0 {
             remainingFile := vma.FileSz - vmaOffset
             if remainingFile <= 0 {
                 readLen = 0
             } else if remainingFile < 4096 {
                 readLen = int(remainingFile)
             }
        }
        
        if readLen > 0 {
            buf := make([]byte, 4096)
            n, err := vma.File.ReadAt(buf[:readLen], off)
            if err != nil && err != io.EOF {
                fmt.Printf("[Fault] Read Error at off %x: %v\n", off, err)
                return false 
            }
            if n > 0 {
                 mapper.MemWrite(pageStart, buf[:n])
            }
        }
        // Implicitly remaining bytes are 0 (handled by MMU or MemWrite of partial buffer?)
        // If MemWrite is partial?
        // No, MMU allocates zeroed page. MemWrite overwrites.
        // So we only MemWrite valid file data. The rest stays 0.
        // Correct.
    }
    
    // Restore permissions if we added Write
    if tempPerms != vma.Perms {
        mapper.MemMap(pageStart, 4096, vma.Perms)
    }

    // fmt.Printf("[PageFault] Handled at %x (VMA: %s)\n", addr, vma.Name)
    return true
}
