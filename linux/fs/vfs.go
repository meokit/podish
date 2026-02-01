package fs

import (
	"path/filepath"
	"strings"
)

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
