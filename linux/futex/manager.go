package futex

import (
	"sync"
)

// Waiter represents a thread waiting on a futex
type Waiter struct {
	C chan struct{}
}

type FutexManager struct {
	mu     sync.Mutex
	queues map[uint32][]*Waiter
}

func NewFutexManager() *FutexManager {
	return &FutexManager{
		queues: make(map[uint32][]*Waiter),
	}
}

// PrepareWait registers a waiter for the given address.
// It returns a channel to wait on.
func (fm *FutexManager) PrepareWait(addr uint32) *Waiter {
	fm.mu.Lock()
	defer fm.mu.Unlock()

	w := &Waiter{C: make(chan struct{})}
	fm.queues[addr] = append(fm.queues[addr], w)
	return w
}

// CancelWait removes the waiter if it hasn't been signaled.
func (fm *FutexManager) CancelWait(addr uint32, w *Waiter) {
	fm.mu.Lock()
	defer fm.mu.Unlock()

	q := fm.queues[addr]
	newQ := q[:0]
	for _, waiter := range q {
		if waiter != w {
			newQ = append(newQ, waiter)
		}
	}
	if len(newQ) == 0 {
		delete(fm.queues, addr)
	} else {
		fm.queues[addr] = newQ
	}
}

// Wake wakes up to 'count' waiters on the address.
// Returns the number of waiters woken.
func (fm *FutexManager) Wake(addr uint32, count int) int {
	fm.mu.Lock()
	defer fm.mu.Unlock()

	q, ok := fm.queues[addr]
	if !ok || len(q) == 0 {
		return 0
	}

	woken := 0
	for i := 0; i < count && len(q) > 0; i++ {
		w := q[0]
		q = q[1:]
		close(w.C) // Unblock the waiter
		woken++
	}

	if len(q) == 0 {
		delete(fm.queues, addr)
	} else {
		fm.queues[addr] = q
	}
	return woken
}
