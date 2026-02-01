package emu

/*
#cgo CFLAGS: -I../../src
#cgo LDFLAGS: -L../../build -lx86emu -Wl,-rpath,@loader_path

#include "bindings.h"
#include <stdlib.h>

// Forward declarations of exported Go functions
void goFaultCallback(EmuState* state, uint32_t addr, int is_write, void* userdata);
int goInterruptHook(EmuState* state, uint32_t vector, void* userdata);

// Trampolines to register with C
static void RegisterCallbacks(EmuState* state) {
    X86_SetFaultCallback(state, goFaultCallback, NULL);
    X86_SetInterruptHook(state, 0x80, goInterruptHook, NULL);
}

*/
import "C"
import (
	"unsafe"
)

// Reg constants matching common.h
const (
	EAX = 0
	ECX = 1
	EDX = 2
	EBX = 3
	ESP = 4
	EBP = 5
	ESI = 6
	EDI = 7

	ES = 0
	CS = 1
	SS = 2
	DS = 3
	FS = 4
	GS = 5
)

// Global state to map back to Go objects
// Since we only run one emulator at a time in this loader, globals are acceptable.
// The new API supports userdata, so we could pass the Engine pointer, but CGO can't easily pass Go pointers to C as userdata without pinning or handles.
// For simplicity, we stick to globals.
var currentFaultHandler func(uint32, bool)
var currentIntrHandler func(uint32) bool

//export goFaultCallback
func goFaultCallback(state *C.EmuState, addr C.uint32_t, isWrite C.int, userdata unsafe.Pointer) {
	if currentFaultHandler != nil {
		currentFaultHandler(uint32(addr), isWrite != 0)
	}
}

//export goInterruptHook
func goInterruptHook(state *C.EmuState, vec C.uint32_t, userdata unsafe.Pointer) C.int {
	if currentIntrHandler != nil {
		if currentIntrHandler(uint32(vec)) {
			return 1
		}
	}
	return 0
}

type Engine struct {
	State *C.EmuState
}

func New() *Engine {
	s := C.X86_Create()
	e := &Engine{State: s}

	// Register callbacks
	C.RegisterCallbacks(s)
	return e
}

func (e *Engine) Close() {
	if e.State != nil {
		C.X86_Destroy(e.State)
		e.State = nil
	}
}

func (e *Engine) SetFaultHandler(handler func(uint32, bool)) {
	currentFaultHandler = handler
}

func (e *Engine) SetInterruptHandler(handler func(uint32) bool) {
	currentIntrHandler = handler
}

func (e *Engine) MemMap(addr uint32, size uint32, perms int) {
	C.X86_MemMap(e.State, C.uint32_t(addr), C.uint32_t(size), C.uint8_t(perms))
}

func (e *Engine) MemWrite(addr uint32, data []byte) {
	if len(data) == 0 {
		return
	}
	cData := (*C.uint8_t)(unsafe.Pointer(&data[0]))
	C.X86_MemWrite(e.State, C.uint32_t(addr), cData, C.uint32_t(len(data)))
}

func (e *Engine) MemRead(addr uint32, size uint32) []byte {
	buf := make([]byte, size)
	cData := (*C.uint8_t)(unsafe.Pointer(&buf[0]))
	C.X86_MemRead(e.State, C.uint32_t(addr), cData, C.uint32_t(size))
	return buf
}

func (e *Engine) RegRead(reg int) uint32 {
	return uint32(C.X86_RegRead(e.State, C.int(reg)))
}

func (e *Engine) RegWrite(reg int, val uint32) {
	C.X86_RegWrite(e.State, C.int(reg), C.uint32_t(val))
}

func (e *Engine) Eip() uint32 {
	return uint32(C.X86_GetEIP(e.State))
}

func (e *Engine) SetEip(val uint32) {
	C.X86_SetEIP(e.State, C.uint32_t(val))
}

func (e *Engine) Eflags() uint32 {
	return uint32(C.X86_GetEFLAGS(e.State))
}

func (e *Engine) SetEflags(val uint32) {
	C.X86_SetEFLAGS(e.State, C.uint32_t(val))
}

func (e *Engine) SetSegBase(seg int, base uint32) {
	C.X86_SegBaseWrite(e.State, C.int(seg), C.uint32_t(base))
}

func (e *Engine) Run(endEip uint32, maxInsts uint64) {
	C.X86_Run(e.State, C.uint32_t(endEip), C.uint64_t(maxInsts))
}

func (e *Engine) Stop() {
	C.X86_EmuStop(e.State)
}

func (e *Engine) Step() int {
	return int(C.X86_Step(e.State))
}

func (e *Engine) GetStatus() int {
	return int(C.X86_GetStatus(e.State))
}
