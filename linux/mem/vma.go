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
		addr = mm.findFreeRegion(len)
		if addr == 0 {
			return 0, fmt.Errorf("execution out of memory")
		}
	}

	end := addr + len
	// Check overlap
	if mm.checkOverlap(addr, end) {
		if flags&MAP_FIXED != 0 {
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

func (mm *VMAManager) Clone() *VMAManager {
	newMM := NewVMAManager()
	for e := mm.vmas.Front(); e != nil; e = e.Next() {
		vma := e.Value.(*VMA)
		newVMA := &VMA{
			Start:  vma.Start,
			End:    vma.End,
			Perms:  vma.Perms,
			Flags:  vma.Flags, // TODO: handle MAP_SHARED
			File:   vma.File,
			Offset: vma.Offset,
			FileSz: vma.FileSz,
			Name:   vma.Name,
		}
		newMM.vmas.PushBack(newVMA)
	}
	return newMM
}

func (mm *VMAManager) Munmap(addr uint32, length uint32) {
	end := addr + length
	var next *list.Element
	for e := mm.vmas.Front(); e != nil; e = next {
		next = e.Next()
		vma := e.Value.(*VMA)

		// No intersection
		if end <= vma.Start || addr >= vma.End {
			continue
		}

		// intersection: [max(addr, vma.Start), min(end, vma.End))

		// Case 1: Full removal
		if addr <= vma.Start && end >= vma.End {
			mm.vmas.Remove(e)
			continue
		}

		// Case 2: Split (Middle removal)
		if addr > vma.Start && end < vma.End {
			// Create new VMA for the tail
			tailStart := end
			tailEnd := vma.End
			var tailOffset int64
			var tailFileSz int64
			if vma.File != nil {
				diff := int64(tailStart - vma.Start)
				tailOffset = vma.Offset + diff
				if vma.FileSz > diff {
					tailFileSz = vma.FileSz - diff
				}
			}

			tailVMA := &VMA{
				Start:  tailStart,
				End:    tailEnd,
				Perms:  vma.Perms,
				Flags:  vma.Flags,
				File:   vma.File,
				Offset: tailOffset,
				FileSz: tailFileSz,
				Name:   vma.Name,
			}
			mm.vmas.InsertAfter(tailVMA, e)

			// Truncate current VMA (Head)
			vma.End = addr
			// FileSz adjustment for head
			if vma.File != nil {
				newLen := int64(vma.End - vma.Start)
				if vma.FileSz > newLen {
					vma.FileSz = newLen
				}
			}
			continue
		}

		// Case 3: Head removal (Address overlaps start)
		if addr <= vma.Start && end < vma.End {
			// New start is end
			diff := end - vma.Start
			vma.Start = end
			if vma.File != nil {
				vma.Offset += int64(diff)
				if vma.FileSz > int64(diff) {
					vma.FileSz -= int64(diff)
				} else {
					vma.FileSz = 0
				}
			}
			continue
		}

		// Case 4: Tail removal (Address overlaps end)
		if addr > vma.Start && end >= vma.End {
			vma.End = addr
			if vma.File != nil {
				newLen := int64(vma.End - vma.Start)
				if vma.FileSz > newLen {
					vma.FileSz = newLen
				}
			}
			continue
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
		base += 0x1000          // Align 4k
		if base >= 0xC0000000 { // Kernel limit
			return 0
		}
	}
}

// Mapper interface to avoid cyclic dependency
type Mapper interface {
	MemMap(addr uint32, size uint32, perms int)
	MemWrite(addr uint32, data []byte)
	MemRead(addr uint32, size uint32) []byte
}

// HandleFault returns true if handled
func (mm *VMAManager) HandleFault(addr uint32, isWrite bool, mapper Mapper) bool {
	// 1. Find VMA
	vma := mm.FindVMA(addr)
	if vma == nil {
		// fmt.Printf("[VMA] Fault at 0x%x: No VMA found\n", addr)
		return false
	}

	// 2. Check Permissions
	if isWrite && (vma.Perms&PROT_WRITE == 0) {
		fmt.Printf("[VMA] Fault at 0x%x: Permission Denied (Perms=%x)\n", addr, vma.Perms)
		return false
	}

	// fmt.Printf("[VMA] Fault at 0x%x: Mapping page (VMA: %s)\n", addr, vma.Name)

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
				return false
			}
			if n > 0 {
				mapper.MemWrite(pageStart, buf[:n])
			}
		}
	}

	// Restore permissions if we added Write
	if tempPerms != vma.Perms {
		mapper.MemMap(pageStart, 4096, vma.Perms)
	}

	return true
}
