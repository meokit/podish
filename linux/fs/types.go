package fs

// Linux i386 Types

type Timespec struct {
	Sec  int32
	Nsec int32
}

// stat64 for i386
// Size: 96 bytes usually?
//
//	struct stat64 {
//	    unsigned long long  st_dev;     // 0-8
//	    unsigned char       __pad0[4];  // 8-12
//	    unsigned long       __st_ino;   // 12-16 (32)
//	    unsigned int        st_mode;    // 16-20
//	    unsigned int        st_nlink;   // 20-24
//	    unsigned long       st_uid;     // 24-28
//	    unsigned long       st_gid;     // 28-32
//	    unsigned long long  st_rdev;    // 32-40
//	    unsigned char       __pad3[4];  // 40-44
//	    long long           st_size;    // 44-52
//	    unsigned long       st_blksize; // 52-56
//	    unsigned long long  st_blocks;  // 56-64
//	    struct timespec     st_atim;    // 64-72
//	    struct timespec     st_mtim;    // 72-80
//	    struct timespec     st_ctim;    // 80-88
//	    unsigned long long  st_ino;     // 88-96
//	};
type Stat64 struct {
	Dev     uint64
	Pad0    [4]byte
	Ino32   uint32
	Mode    uint32
	Nlink   uint32
	Uid     uint32
	Gid     uint32
	Rdev    uint64
	Pad3    [4]byte
	Size    int64
	Blksize uint32
	Blocks  uint64
	Atim    Timespec
	Mtim    Timespec
	Ctim    Timespec
	Ino64   uint64
}

// dirent64
//
//	struct linux_dirent64 {
//	   u64        d_ino;
//	   s64        d_off;
//	   unsigned short d_reclen;
//	   unsigned char  d_type;
//	   char           d_name[];
//	};
type Dirent64 struct {
	Ino    uint64
	Off    int64
	Reclen uint16
	Type   uint8
	Name   []byte // null terminated
}

// Constants
const (
	DT_UNKNOWN = 0
	DT_FIFO    = 1
	DT_CHR     = 2
	DT_DIR     = 4
	DT_BLK     = 6
	DT_REG     = 8
	DT_LNK     = 10
	DT_SOCK    = 12
	DT_WHT     = 14
)
