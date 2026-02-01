package fs

import (
	"os"
)

func (sm *SyscallManager) sys_exit(a1, a2, a3, a4, a5, a6 uint32) int32 {
	os.Exit(int(a1))
	return 0
}

func (sm *SyscallManager) sys_exit_group(a1, a2, a3, a4, a5, a6 uint32) int32 {
	os.Exit(int(a1))
	return 0
}

func (sm *SyscallManager) sys_read(a1, a2, a3, a4, a5, a6 uint32) int32 {
	fd := int(a1)
	bufAddr := a2
	count := a3
	if f, ok := sm.GetFD(fd); ok {
		localBuf := make([]byte, count)
		n, err := f.Read(localBuf)
		if n > 0 {
			sm.Emu.MemWrite(bufAddr, localBuf[:n])
		}
		if err != nil {
			if n == 0 {
				return 0
			} // EOF
			return -1
		}
		return int32(n)
	}
	return -9
}

func (sm *SyscallManager) sys_write(a1, a2, a3, a4, a5, a6 uint32) int32 {
	fd := int(a1)
	bufAddr := a2
	count := a3
	if f, ok := sm.GetFD(fd); ok {
		data := sm.Emu.MemRead(bufAddr, count)
		n, err := f.Write(data)
		if err != nil {
			return -1
		}
		return int32(n)
	}
	return -9
}

func (sm *SyscallManager) sys_open(a1, a2, a3, a4, a5, a6 uint32) int32 {
	path := sm.ReadString(a1)
	flags := int(a2)
	mode := uint32(a3)
	hostPath := sm.ResolvePath(path)

	goFlags := 0
	if flags&0x3 == 0 {
		goFlags |= os.O_RDONLY
	}
	if flags&0x3 == 1 {
		goFlags |= os.O_WRONLY
	}
	if flags&0x3 == 2 {
		goFlags |= os.O_RDWR
	}
	if flags&0x40 != 0 {
		goFlags |= os.O_CREATE
	}
	if flags&0x200 != 0 {
		goFlags |= os.O_TRUNC
	}
	if flags&0x400 != 0 {
		goFlags |= os.O_APPEND
	}

	perm := os.FileMode(0644)
	if flags&0x40 != 0 {
		perm = os.FileMode(mode & 0777)
	}

	f, err := os.OpenFile(hostPath, goFlags, perm)
	if err != nil {
		return -2
	}

	return int32(sm.AllocFD(f))
}

func (sm *SyscallManager) sys_close(a1, a2, a3, a4, a5, a6 uint32) int32 {
	fd := int(a1)
	sm.FreeFD(fd)
	return 0
}

func (sm *SyscallManager) sys_stat64(a1, a2, a3, a4, a5, a6 uint32) int32 {
	path := sm.ReadString(a1)
	hostPath := sm.ResolvePath(path)
	info, err := os.Stat(hostPath)
	if err != nil {
		return -2
	}
	sm.writeStat64(a2, info)
	return 0
}

func (sm *SyscallManager) sys_lstat64(a1, a2, a3, a4, a5, a6 uint32) int32 {
	path := sm.ReadString(a1)
	hostPath := sm.ResolvePath(path)
	info, err := os.Lstat(hostPath)
	if err != nil {
		return -2
	}
	sm.writeStat64(a2, info)
	return 0
}

func (sm *SyscallManager) sys_fstat64(a1, a2, a3, a4, a5, a6 uint32) int32 {
	fd := int(a1)
	if f, ok := sm.GetFD(fd); ok {
		info, err := f.Stat()
		if err != nil {
			return -9
		}
		sm.writeStat64(a2, info)
		return 0
	}
	return -9
}

func (sm *SyscallManager) sys_ioctl(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return 0
}

func (sm *SyscallManager) sys_access(a1, a2, a3, a4, a5, a6 uint32) int32 {
	path := sm.ReadString(a1)
	hostPath := sm.ResolvePath(path)
	_, err := os.Stat(hostPath)
	if err != nil {
		return -2
	}
	return 0
}

func (sm *SyscallManager) sys_mkdir(a1, a2, a3, a4, a5, a6 uint32) int32 {
	// Implement or Stub? Missing in previous but useful
	return -38
}

func (sm *SyscallManager) sys_unlink(a1, a2, a3, a4, a5, a6 uint32) int32 {
	path := sm.ReadString(a1)
	hostPath := sm.ResolvePath(path)
	// unlink handles files, rmdir directories.
	// os.Remove handles both but Linux unlink fails on dirs (usually).
	// For now simple os.Remove
	err := os.Remove(hostPath)
	if err != nil {
		return -2
	}
	return 0
}

func (sm *SyscallManager) sys_writev(a1, a2, a3, a4, a5, a6 uint32) int32 {
	fd := int(a1)
	iovAddr := a2
	iovCnt := int(a3)
	if f, ok := sm.GetFD(fd); ok {
		totalWritten := 0
		for i := 0; i < iovCnt; i++ {
			baseBytes := sm.Emu.MemRead(iovAddr+uint32(i*8), 4)
			lenBytes := sm.Emu.MemRead(iovAddr+uint32(i*8)+4, 4)
			base := binary_LittleEndian_Uint32(baseBytes)
			length := binary_LittleEndian_Uint32(lenBytes)
			if length > 0 {
				data := sm.Emu.MemRead(base, length)
				n, _ := f.Write(data)
				if n > 0 {
					totalWritten += n
				}
			}
		}
		return int32(totalWritten)
	}
	return -9
}

func (sm *SyscallManager) sys_getcwd(a1, a2, a3, a4, a5, a6 uint32) int32 {
	bufAddr := a1
	size := a2
	cwd := "/"
	if len(cwd)+1 > int(size) {
		return -34
	}
	sm.Emu.MemWrite(bufAddr, []byte(cwd))
	sm.Emu.MemWrite(bufAddr+uint32(len(cwd)), []byte{0})
	return int32(len(cwd) + 1)
}

func (sm *SyscallManager) sys_getdents64(a1, a2, a3, a4, a5, a6 uint32) int32 {
	fd := int(a1)
	bufAddr := a2
	count := int(a3)

	if f, ok := sm.GetFD(fd); ok {
		entries, err := f.ReadDir(-1)
		if err != nil && len(entries) == 0 {
			return 0
		}

		writeOffset := 0
		for i, e := range entries {
			nameBytes := []byte(e.Name())
			nameLen := len(nameBytes) + 1
			recLen := (8 + 8 + 2 + 1 + nameLen + 7) & ^7

			if writeOffset+recLen > count {
				break
			}

			base := bufAddr + uint32(writeOffset)

			sm.Emu.MemWrite(base, []byte{1, 0, 0, 0, 0, 0, 0, 0})
			off := int64(i + 1)
			offBuf := make([]byte, 8)
			binary_LittleEndian_PutUint64(offBuf, uint64(off))
			sm.Emu.MemWrite(base+8, offBuf)

			reclenBuf := make([]byte, 2)
			reclenBuf[0] = byte(recLen)
			reclenBuf[1] = byte(recLen >> 8)
			sm.Emu.MemWrite(base+16, reclenBuf)

			dType := byte(DT_UNKNOWN)
			if e.IsDir() {
				dType = byte(DT_DIR)
			} else {
				dType = byte(DT_REG)
			}
			sm.Emu.MemWrite(base+18, []byte{dType})

			sm.Emu.MemWrite(base+19, append(nameBytes, 0))

			writeOffset += recLen
		}
		return int32(writeOffset)
	}
	return -9
}

func (sm *SyscallManager) sys_statx(a1, a2, a3, a4, a5, a6 uint32) int32 {
	return -38 // ENOSYS
}

func (sm *SyscallManager) writeStat64(addr uint32, info os.FileInfo) {
	buf := make([]byte, 96)

	mode := uint32(info.Mode() & 0777)
	if info.IsDir() {
		mode |= 0040000
	}
	if info.Mode().IsRegular() {
		mode |= 0100000
	}

	size := info.Size()

	binary_LittleEndian_PutUint64(buf[0:], 0x800)
	binary_LittleEndian_PutUint32(buf[12:], 1)

	binary_LittleEndian_PutUint32(buf[16:], mode)
	binary_LittleEndian_PutUint32(buf[20:], 1)

	binary_LittleEndian_PutUint32(buf[24:], 0)
	binary_LittleEndian_PutUint32(buf[28:], 0)

	binary_LittleEndian_PutUint64(buf[32:], 0)

	binary_LittleEndian_PutUint64(buf[44:], uint64(size))

	binary_LittleEndian_PutUint32(buf[52:], 4096)
	binary_LittleEndian_PutUint64(buf[56:], uint64((size+511)/512))

	binary_LittleEndian_PutUint64(buf[88:], 1)

	sm.Emu.MemWrite(addr, buf)
}
