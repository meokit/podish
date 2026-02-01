package fs

import (
	"os"
)

// FD Management
func (sm *SyscallManager) GetFD(fd int) (*os.File, bool) {
	f, ok := sm.FDs[fd]
	return f, ok
}

func (sm *SyscallManager) AllocFD(f *os.File) int {
	// Find free FD starting from 3
	newFd := 3
	for {
		if _, exists := sm.FDs[newFd]; !exists {
			break
		}
		newFd++
	}
	sm.FDs[newFd] = f
	return newFd
}

func (sm *SyscallManager) FreeFD(fd int) {
	if f, ok := sm.FDs[fd]; ok {
		f.Close()
		delete(sm.FDs, fd)
	}
}
