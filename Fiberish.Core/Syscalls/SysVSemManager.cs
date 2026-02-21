using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Syscalls;

public class SemaphoreSet
{
    public int Key { get; set; }
    public int Semid { get; set; }
    public int Mode { get; set; }
    public int Uid { get; set; }
    public int Gid { get; set; }
    public int CUid { get; set; }
    public int CGid { get; set; }
    public DateTime CTime { get; set; }
    public DateTime OTime { get; set; }
    
    public short[] Values { get; set; } = [];
    public List<SemWaiter> Waiters { get; set; } = [];
}

public class SemWaiter : INotifyCompletion
{
    public FiberTask Task { get; }
    public int SemNum { get; }
    public short Op { get; }
    public Action? Continuation { get; set; }

    public SemWaiter(FiberTask task, int semNum, short op)
    {
        Task = task;
        SemNum = semNum;
        Op = op;
    }

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation)
    {
        Continuation = continuation;
    }

    public AwaitResult GetResult()
    {
        if (Task.WakeReason != WakeReason.None)
        {
            Task.WakeReason = WakeReason.None;
            return AwaitResult.Interrupted;
        }
        return AwaitResult.Completed;
    }

    public SemWaiter GetAwaiter() => this;
}

public class SysVSemManager
{
    private readonly Dictionary<int, SemaphoreSet> _setsBySemid = new();
    private readonly Dictionary<int, SemaphoreSet> _setsByKey = new();
    private readonly object _lock = new();
    private int _nextSemid = 1;

    public int SemGet(int key, int nsems, int semflg, int uid, int gid)
    {
        lock (_lock)
        {
            if (key != LinuxConstants.IPC_PRIVATE && _setsByKey.TryGetValue(key, out var existing))
            {
                if ((semflg & LinuxConstants.IPC_CREAT) != 0 && (semflg & LinuxConstants.IPC_EXCL) != 0)
                    return -(int)Errno.EEXIST;

                if (nsems > existing.Values.Length && nsems != 0)
                    return -(int)Errno.EINVAL;

                return existing.Semid;
            }

            if (key != LinuxConstants.IPC_PRIVATE && (semflg & LinuxConstants.IPC_CREAT) == 0)
                return -(int)Errno.ENOENT;
                
            if (nsems <= 0 || nsems > 32000)
                return -(int)Errno.EINVAL;

            var semid = _nextSemid++;
            var set = new SemaphoreSet
            {
                Key = key,
                Semid = semid,
                Mode = semflg & 0x1FF,
                Uid = uid,
                Gid = gid,
                CUid = uid,
                CGid = gid,
                CTime = DateTime.UtcNow,
                OTime = DateTime.UnixEpoch, // 0 initially
                Values = new short[nsems]
            };

            _setsBySemid[semid] = set;
            if (key != LinuxConstants.IPC_PRIVATE)
                _setsByKey[key] = set;

            return semid;
        }
    }

    public async ValueTask<int> SemOp(int semid, uint sopsPtr, uint nsops, Engine engine)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (nsops == 0 || nsops > 500 /* SEMOPM limit */) return -(int)Errno.E2BIG;

        var bytes = new byte[nsops * 6]; // struct sembuf: short sem_num, short sem_op, short sem_flg
        if (!engine.CopyFromUser(sopsPtr, bytes)) return -(int)Errno.EFAULT;

        SemaphoreSet set;
        lock (_lock)
        {
            if (!_setsBySemid.TryGetValue(semid, out set!))
                return -(int)Errno.EINVAL;
        }

        while (true)
        {
            var canProceed = true;
            SemWaiter? blockedWaiter = null;
            
            lock (_lock)
            {
                if (!_setsBySemid.ContainsKey(semid)) return -(int)Errno.EIDRM; // Removed while waiting

                // Try atomic operations
                for (int i = 0; i < nsops; i++)
                {
                    var offset = i * 6;
                    var semNum = BitConverter.ToInt16(bytes, offset);
                    var semOp = BitConverter.ToInt16(bytes, offset + 2);
                    var semFlg = BitConverter.ToInt16(bytes, offset + 4);

                    if (semNum >= set.Values.Length || semNum < 0)
                    {
                        return -(int)Errno.EFBIG;
                    }

                    if (semOp > 0)
                    {
                        // Always fine to add, assuming no overflow (skipping overflow check for now)
                    }
                    else if (semOp < 0)
                    {
                        if (set.Values[semNum] < -semOp)
                        {
                            canProceed = false;
                            blockedWaiter = new SemWaiter(task, semNum, semOp);
                            if ((semFlg & LinuxConstants.IPC_NOWAIT) != 0) return -(int)Errno.EAGAIN;
                            break;
                        }
                    }
                    else // semOp == 0
                    {
                        if (set.Values[semNum] != 0)
                        {
                            canProceed = false;
                            blockedWaiter = new SemWaiter(task, semNum, semOp);
                            if ((semFlg & LinuxConstants.IPC_NOWAIT) != 0) return -(int)Errno.EAGAIN;
                            break;
                        }
                    }
                }

                if (canProceed)
                {
                    // Success! Commit state.
                    for (int i = 0; i < nsops; i++)
                    {
                        var offset = i * 6;
                        var semNum = BitConverter.ToInt16(bytes, offset);
                        var semOp = BitConverter.ToInt16(bytes, offset + 2);
                        set.Values[semNum] += semOp; // works for 0 naturally
                    }

                    set.OTime = DateTime.UtcNow;

                    // Wake up valid waiters
                    var waken = false;
                    foreach (var waiter in set.Waiters.ToList())
                    {
                        // Quick evaluation if they might be satisfied now
                        var currentVal = set.Values[waiter.SemNum];
                        var op = waiter.Op;
                        if ((op < 0 && currentVal >= -op) || (op == 0 && currentVal == 0))
                        {
                            set.Waiters.Remove(waiter);
                            waiter.Continuation?.Invoke();
                            waken = true;
                        }
                    }

                    if (waken) KernelScheduler.Current?.WakeUp();

                    return 0; // Success
                }
                
                // Track we are waiting
                set.Waiters.Add(blockedWaiter!);
            }

            // Await execution
            var res = await blockedWaiter!;
            if (res == AwaitResult.Interrupted)
            {
                lock (_lock)
                {
                    set.Waiters.Remove(blockedWaiter!);
                }
                return -(int)Errno.EINTR;
            }
        }
    }

    public int SemCtl(int semid, int semnum, int cmd, uint arg, Engine engine, int uid, int gid)
    {
        lock (_lock)
        {
            if (!_setsBySemid.TryGetValue(semid, out var set))
                return -(int)Errno.EINVAL;

            var actualCmd = cmd & ~LinuxConstants.IPC_64;

            switch (actualCmd)
            {
                case LinuxConstants.IPC_RMID:
                    // Destroy
                    _setsBySemid.Remove(semid);
                    if (set.Key != LinuxConstants.IPC_PRIVATE)
                        _setsByKey.Remove(set.Key);
                    
                    // Wake up all waiters with EIDRM
                    foreach (var waiter in set.Waiters)
                    {
                        waiter.Task.WakeReason = WakeReason.Event; // Special wake, let them fail with EIDRM loop
                        waiter.Continuation?.Invoke();
                    }
                    KernelScheduler.Current?.WakeUp();
                    return 0;
                    
                case LinuxConstants.IPC_STAT:
                    // Similar to SHM_STAT
                    return 0; // Stub

                case LinuxConstants.GETVAL:
                    if (semnum < 0 || semnum >= set.Values.Length) return -(int)Errno.EINVAL;
                    return set.Values[semnum];

                case LinuxConstants.SETVAL:
                    if (semnum < 0 || semnum >= set.Values.Length) return -(int)Errno.EINVAL;
                    // For SETVAL in sys_ipc(SEMCTL), the 4th argument (ptr) IS the union value.
                    // So `arg` contains the 32-bit integer `val` directly.
                    int setval = (int)arg;

                    // Clamp to short
                    if (setval < 0 || setval > 32767) return -(int)Errno.ERANGE;

                    set.Values[semnum] = (short)setval;
                    
                    // Wake up all waiters since state changed
                    foreach (var waiter in set.Waiters)
                    {
                        waiter.Task.WakeReason = WakeReason.Event;
                        waiter.Continuation?.Invoke();
                    }
                    KernelScheduler.Current?.WakeUp();
                    return 0;

                case LinuxConstants.GETALL:
                    return 0; // Stub
                case LinuxConstants.SETALL:
                    return 0; // Stub

                default:
                    return -(int)Errno.EINVAL;
            }
        }
    }
}
