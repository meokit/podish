package task

import (
	"encoding/binary"
	"sync"
	"sync/atomic"
	"x86emu-loader/emu"
	"x86emu-loader/fs"
	"x86emu-loader/mem"
)

// Global GIL to serialize all x86 execution
// Since C++ emulator is not thread-safe and has global state (bindings),
// we must ensure only one Task executes instructions or accesses memory at a time.
var GIL sync.Mutex

func LockGIL() {
	GIL.Lock()
}

func UnlockGIL() {
	GIL.Unlock()
}

// ID Generator
var pidCounter int32 = 1000

func NextPID() int {
	return int(atomic.AddInt32(&pidCounter, 1))
}

// Process represents a thread group (TGID in Linux)
// Resources shared by threads in a process:
// - Memory (VMAManager)
// - File Descriptors (FDTable)
// - Signal Handlers (SignalTable - TODO)
type Process struct {
	TGID int // Thread Group ID (PID)

	// Resource Managers
	Mem *mem.VMAManager
	FDs *fs.SyscallManager // Reused SyscallManager as FD holder for now, should refactor later
}

// Task represents a thread of execution
// Unique execution context:
// - CPU State (Emu Engine)
// - Stack
// - TID (Thread ID)
type Task struct {
	TID     int // Thread ID
	Process *Process
	CPU     *emu.Engine

	// Thread Local Storage (Linux pthreads use SetThreadArea / GDT)
	// Handled by CPU State

	// Exit State
	ExitCode int
	Exited   bool
	WaitCh   chan struct{} // Closed on exit
}

// Scheduler tracks all tasks
type Scheduler struct {
	Tasks map[int]*Task
	Mu    sync.Mutex
}

var GlobalSchedString string = "Global Scheduler"
var GlobalSched = &Scheduler{
	Tasks: make(map[int]*Task),
}

func (s *Scheduler) Add(t *Task) {
	s.Mu.Lock()
	defer s.Mu.Unlock()
	s.Tasks[t.TID] = t
}

func (s *Scheduler) Remove(tid int) {
	s.Mu.Lock()
	defer s.Mu.Unlock()
	delete(s.Tasks, tid)
}

func (s *Scheduler) Get(tid int) *Task {
	s.Mu.Lock()
	defer s.Mu.Unlock()
	return s.Tasks[tid]
}

// Registry to map Engine to Task, replacing global CurrentTask reliance
var Registry sync.Map // map[*emu.Engine]*Task

func RegisterTask(e *emu.Engine, t *Task) {
	Registry.Store(e, t)
}

func UnregisterTask(e *emu.Engine) {
	Registry.Delete(e)
}

func GetTask(e *emu.Engine) *Task {
	if v, ok := Registry.Load(e); ok {
		return v.(*Task)
	}
	return nil
}

// CurrentTask is deprecated/unsafe in multi-threaded environment.
// Use GetTask(e) inside callbacks.
var (
	currTask *Task
	currCPU  *emu.Engine
)

func CurrentTask() *Task      { return currTask }
func CurrentCPU() *emu.Engine { return currCPU }

// NewTask creates a new main process task
func NewTask(eng *emu.Engine, mem *mem.VMAManager, fds *fs.SyscallManager) *Task {
	pid := NextPID()
	proc := &Process{
		TGID: pid,
		Mem:  mem,
		FDs:  fds,
	}

	t := &Task{
		TID:     pid, // Main thread has TID == TGID
		Process: proc,
		CPU:     eng,
		WaitCh:  make(chan struct{}),
	}
	RegisterTask(eng, t)
	GlobalSched.Add(t)
	return t
}

// Clone creates a new task (Thread or Process)
// flags matching standard linux clone flags
// CLONE_VM      = 0x00000100
// CLONE_FS      = 0x00000200
// CLONE_FILES   = 0x00000400
// CLONE_SIGHAND = 0x00000800
// CLONE_THREAD  = 0x00010000
func (t *Task) Clone(flags int, stackPtr uint32, ptidPtr uint32, tlsPtr uint32, ctidPtr uint32) (*Task, error) {
	// Flags
	cloneVM := (flags & 0x00000100) != 0
	cloneFiles := (flags & 0x00000400) != 0
	cloneThread := (flags & 0x00010000) != 0

	// 1. Clone CPU State
	// If CLONE_VM is set, C++ shares memory. If not, deep copy.
	newCPU := t.CPU.Clone(cloneVM)
	// Set new internal pointer?
	// Note: C++ Clone returns new state, wrapper creates new Engine. Good.

	// 2. Resource Management
	var newProc *Process

	if cloneThread {
		// Thread: Share Process
		newProc = t.Process
	} else {
		// Fork/New Process
		newProc = &Process{
			TGID: NextPID(),
			// Mem: Shared or Copied?
			// If CLONE_VM is false (fork), we need to copy VMA info Go-side too.
		}

		if cloneVM {
			newProc.Mem = t.Process.Mem
		} else {
			// Deep Copy VMA Manager (Not implemented fully yet!)
			// For busybox fork (rarely used without exec), usually COW.
			// Currently we share everything or nothing.
			// Let's implement basic copy for VMA structs
			newProc.Mem = t.Process.Mem.Clone()
		}

		if cloneFiles {
			newProc.FDs = t.Process.FDs
		} else {
			// Duplicate FDs
			// TODO: newProc.FDs = t.Process.FDs.Clone()
			// For now, share to avoid complexity, or create empty?
			// "fork" implies copy.
			// Let's assume fork+exec, so shallow copy is okay for now?
			// Actually SyscallManager holds the Mem pointer too.
			// We need a way to instantiate a new SyscallManager with new Mem.
			// This requires refactoring SyscallManager to separate FDs from Logic.
			// For this iteration, we might assume CLONE_VM (Threads) is the priority.
			// If fork() reaches here, we might have issues.
			newProc.FDs = t.Process.FDs // Dangerous sharing for fork!
		}
	}

	newTID := 0
	if cloneThread {
		newTID = NextPID()
	} else {
		newTID = newProc.TGID
	}

	child := &Task{
		TID:     newTID,
		Process: newProc,
		CPU:     newCPU,
		WaitCh:  make(chan struct{}),
	}

	// 3. Setup Child CPU State
	RegisterTask(child.CPU, child)

	if stackPtr != 0 {
		child.CPU.RegWrite(emu.ESP, stackPtr)
	}
	// Return value in EAX should be 0 for child
	child.CPU.RegWrite(emu.EAX, 0)

	// TLS
	const CLONE_SETTLS = 0x00080000
	if flags&CLONE_SETTLS != 0 && tlsPtr != 0 {
		// set_thread_area for child
		// Usually stored in GDT entry 6, 7, or 8.
		// Emulate `sys_set_thread_area` logic here or just assume library handles it?
		// clone(..., tls_val, ...) implies we must set it.
		// The arg 'tlsPtr' points to a 'struct user_desc'.
		// struct user_desc {
		//   uint32 entry_number;
		//   uint32 base_addr;
		//   uint32 limit;
		//   ...
		// }
		// We want base_addr at offset 4.
		buf := child.CPU.MemRead(tlsPtr+4, 4)
		if len(buf) == 4 {
			base := binary.LittleEndian.Uint32(buf)
			child.CPU.SetSegBase(emu.GS, base)
		}
	}

	// Write TID to user memory
	if ptidPtr != 0 {
		// Parent's memory (which might be shared)
		// Write child TID
		// But we need to use t.CPU (Parent) to write to Parent's AS?
		// Or child.CPU? If CLONE_VM, they are same physical memory.
		// using child.CPU is safe.
		// TODO: Access method.
		// child.MemWriteInt32(ptidPtr, newTID)
	}

	GlobalSched.Add(child)
	return child, nil
}

func (t *Task) RunLoop() {
	defer func() {
		// Ensure WaitCh is closed only once
		select {
		case <-t.WaitCh:
		default:
			close(t.WaitCh)
		}
	}()

	// Initial switch
	// 2. Run
	// 3. Handle Exit/Syscall/Fault
	// 4. Unlock

	// fmt.Printf("Task %d RunLoop Start. EIP=%x\n", t.TID, t.CPU.Eip())

	// We need to loop because Run() returns on every block/syscall
	steps := 0
	for !t.Exited {
		GIL.Lock()
		currTask = t
		currCPU = t.CPU

		// Execute a batch of instructions using Step()
		for i := 0; i < 1000; i++ {
			st := t.CPU.Step()
			if st != emu.StatusRunning {
				break
			}
		}

		/*
			if steps < 50 {
				fmt.Printf("Tick %d EIP=%x\n", t.TID, t.CPU.Eip())
			}
		*/
		steps++

		status := t.CPU.GetStatus()

		currTask = nil
		currCPU = nil
		GIL.Unlock()

		if status == emu.StatusStopped {
			// Usually syscall finished or standard stop.
			// Yield to allow other goroutines
			// runtime.Gosched()
		}

		if status == emu.StatusFault {
			// fmt.Printf("Task %d Faulted!\n", t.TID)
			t.Exited = true
		}
	}

	close(t.WaitCh)
	UnregisterTask(t.CPU)
	GlobalSched.Remove(t.TID)
}
