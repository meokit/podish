using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Process = Fiberish.Core.Process;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public class SemaphoreSet
{
    public int Key { get; set; }
    public int Semid { get; set; }
    public int Index { get; set; }
    public int Mode { get; set; }
    public int Uid { get; set; }
    public int Gid { get; set; }
    public int CUid { get; set; }
    public int CGid { get; set; }
    public DateTime CTime { get; set; }
    public DateTime OTime { get; set; }

    public short[] Values { get; set; } = [];
    public int[] LastPid { get; set; } = [];
    public List<SemWaitState> Waiters { get; } = [];
}

public sealed class SemWaitState
{
    public SemWaitState(FiberTask task, int semNum, short op)
    {
        Task = task;
        Token = task.BeginWaitToken();
        SemNum = semNum;
        Op = op;
    }

    public FiberTask Task { get; }
    public FiberTask.WaitToken Token { get; }
    public int SemNum { get; }
    public short Op { get; }
    internal TaskAsyncOperationHandle? Operation { get; set; }

    public bool TryWake(WakeReason reason)
    {
        return Operation?.TryComplete(reason) ?? false;
    }
}

public readonly struct SemWaitAwaitable
{
    private readonly SemWaitState _state;
    private readonly long? _timeoutMs;

    public SemWaitAwaitable(SemWaitState state, long? timeoutMs = null)
    {
        _state = state;
        _timeoutMs = timeoutMs;
    }

    public SemWaitAwaiter GetAwaiter()
    {
        return new SemWaitAwaiter(_state, _timeoutMs);
    }
}

public readonly struct SemWaitAwaiter : INotifyCompletion
{
    private readonly SemWaitState _state;
    private readonly long? _timeoutMs;

    public SemWaitAwaiter(SemWaitState state, long? timeoutMs)
    {
        _state = state;
        _timeoutMs = timeoutMs;
    }

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation)
    {
        var state = _state;
        if (!state.Task.TryEnterAsyncOperation(state.Token, out var operation) || operation == null)
            return;

        state.Operation = operation;
        var asyncState = new SemWaitOperation(state, continuation, operation, _timeoutMs);
        state.Task.ArmInterruptingSignalSafetyNet(state.Token, asyncState.OnSignal);
    }

    public SemWaitOutcome GetResult()
    {
        var reason = _state.Task.CompleteWaitToken(_state.Token);
        if (reason == WakeReason.Timer) return SemWaitOutcome.TimedOut;
        if (reason != WakeReason.None && reason != WakeReason.Event) return SemWaitOutcome.Interrupted;
        return SemWaitOutcome.Completed;
    }

    private sealed class SemWaitOperation
    {
        private readonly TaskAsyncOperationHandle _operation;
        private readonly SemWaitState _state;
        private readonly Timer? _timer;

        public SemWaitOperation(SemWaitState state, Action continuation, TaskAsyncOperationHandle operation,
            long? timeoutMs)
        {
            _state = state;
            _operation = operation;
            _operation.TryInitialize(continuation);
            if (timeoutMs.HasValue)
            {
                _timer = state.Task.CommonKernel.ScheduleTimer(timeoutMs.Value, OnTimeout);
                _operation.TryAddRegistration(TaskAsyncRegistration.From(_timer));
            }
        }

        public void OnSignal()
        {
            _operation.TryComplete(WakeReason.Signal);
        }

        private void OnTimeout()
        {
            _operation.TryComplete(WakeReason.Timer);
        }
    }
}

public enum SemWaitOutcome
{
    Completed,
    Interrupted,
    TimedOut
}

public class SysVSemManager
{
    public const int SEMMNI = 128;
    public const int SEMMSL = 32000;
    public const int SEMMNS = 1024000000;
    public const int SEMOPM = 500;
    public const int SEMVMX = 32767;

    private readonly Dictionary<int, SemaphoreSet> _setsByKey = new();
    private readonly Dictionary<int, SemaphoreSet> _setsBySemid = new();
    private readonly Dictionary<int, SemaphoreSet> _setsByIndex = new();
    private readonly HashSet<Process> _activeProcesses = new();

    public int SemGet(int key, int nsems, int semflg, int uid, int gid, Process? process = null)
    {
        if (key != LinuxConstants.IPC_PRIVATE && _setsByKey.TryGetValue(key, out var existing))
        {
            // Permission check on existing set
            var requested = 0;
            if ((semflg & 0444) != 0) requested |= 4; // Read
            if ((semflg & 0222) != 0) requested |= 2; // Alter
            if (requested != 0 && !CheckPermission(existing, uid, gid, requested, process))
                return -(int)Errno.EACCES;

            if ((semflg & LinuxConstants.IPC_CREAT) != 0 && (semflg & LinuxConstants.IPC_EXCL) != 0)
                return -(int)Errno.EEXIST;

            if (nsems > existing.Values.Length && nsems != 0)
                return -(int)Errno.EINVAL;

            return existing.Semid;
        }

        if (key != LinuxConstants.IPC_PRIVATE && (semflg & LinuxConstants.IPC_CREAT) == 0)
            return -(int)Errno.ENOENT;

        if (nsems <= 0 || nsems > SEMMSL)
            return -(int)Errno.EINVAL;

        if (_setsBySemid.Count >= SEMMNI)
            return -(int)Errno.ENOSPC;

        var index = -1;
        for (var i = 0; i < SEMMNI; i++)
        {
            if (!_setsByIndex.ContainsKey(i))
            {
                index = i;
                break;
            }
        }

        if (index < 0) return -(int)Errno.ENOSPC;

        var semid = index;
        var set = new SemaphoreSet
        {
            Key = key,
            Semid = semid,
            Index = index,
            Mode = semflg & 0x1FF,
            Uid = uid,
            Gid = gid,
            CUid = uid,
            CGid = gid,
            CTime = DateTime.UtcNow,
            OTime = DateTime.UnixEpoch,
            Values = new short[nsems],
            LastPid = new int[nsems]
        };

        for (var i = 0; i < nsems; i++)
            set.LastPid[i] = process?.TGID ?? 0;

        _setsBySemid[semid] = set;
        _setsByIndex[index] = set;
        if (key != LinuxConstants.IPC_PRIVATE)
            _setsByKey[key] = set;

        return semid;
    }

    public async ValueTask<int> SemOp(int semid, uint sopsPtr, uint nsops, Engine engine, long? timeoutMs = null)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (nsops == 0 || nsops > SEMOPM) return -(int)Errno.E2BIG;

        var bytes = new byte[nsops * 6];
        if (!engine.CopyFromUser(sopsPtr, bytes)) return -(int)Errno.EFAULT;

        if (!_setsBySemid.TryGetValue(semid, out var set))
            return -(int)Errno.EINVAL;

        var uid = task.Process.EUID;
        var gid = task.Process.EGID;

        // Pre-validate all operations and permissions
        for (var i = 0; i < nsops; i++)
        {
            var offset = i * 6;
            var semNum = BitConverter.ToInt16(bytes, offset);
            var semOp = BitConverter.ToInt16(bytes, offset + 2);

            if (semNum >= set.Values.Length || semNum < 0) return -(int)Errno.EFBIG;

            var reqPerm = semOp == 0 ? 4 : 2;
            if (!CheckPermission(set, uid, gid, reqPerm, task.Process))
                return -(int)Errno.EACCES;
        }

        var deadlineMs = timeoutMs.HasValue
            ? Stopwatch.GetTimestamp() + timeoutMs.Value * Stopwatch.Frequency / 1000
            : 0;

        while (true)
        {
            if (!_setsBySemid.ContainsKey(semid)) return -(int)Errno.EIDRM;

            var canProceed = true;
            SemWaitState? blockedWaiter = null;

            for (var i = 0; i < nsops; i++)
            {
                var offset = i * 6;
                var semNum = BitConverter.ToInt16(bytes, offset);
                var semOp = BitConverter.ToInt16(bytes, offset + 2);
                var semFlg = BitConverter.ToInt16(bytes, offset + 4);

                if (semOp > 0)
                {
                    if (set.Values[semNum] + semOp > SEMVMX)
                        return -(int)Errno.ERANGE;
                }
                else if (semOp < 0)
                {
                    if (set.Values[semNum] < -semOp)
                    {
                        canProceed = false;
                        blockedWaiter = new SemWaitState(task, semNum, semOp);
                        if ((semFlg & LinuxConstants.IPC_NOWAIT) != 0) return -(int)Errno.EAGAIN;
                        break;
                    }
                }
                else // semOp == 0
                {
                    if (set.Values[semNum] != 0)
                    {
                        canProceed = false;
                        blockedWaiter = new SemWaitState(task, semNum, semOp);
                        if ((semFlg & LinuxConstants.IPC_NOWAIT) != 0) return -(int)Errno.EAGAIN;
                        break;
                    }
                }
            }

            if (canProceed)
            {
                // Commit state
                for (var i = 0; i < nsops; i++)
                {
                    var offset = i * 6;
                    var semNum = BitConverter.ToInt16(bytes, offset);
                    var semOp = BitConverter.ToInt16(bytes, offset + 2);
                    var semFlg = BitConverter.ToInt16(bytes, offset + 4);

                    set.Values[semNum] += semOp;
                    set.LastPid[semNum] = task.Process.TGID;

                    if ((semFlg & LinuxConstants.SEM_UNDO) != 0)
                    {
                        var key = (semid, (int)semNum);
                        task.Process.SemUndo.TryGetValue(key, out var curUndo);
                        task.Process.SemUndo[key] = curUndo - semOp;
                        _activeProcesses.Add(task.Process);
                    }
                }

                set.OTime = DateTime.UtcNow;
                WakeWaiters(set);
                return 0;
            }

            set.Waiters.Add(blockedWaiter!);

            try
            {
                long? remainingMs = null;
                if (timeoutMs.HasValue)
                {
                    var remainingTicks = Math.Max(0L, deadlineMs - Stopwatch.GetTimestamp());
                    remainingMs = remainingTicks == 0 ? 0 : Math.Max(1L, remainingTicks * 1000 / Stopwatch.Frequency);
                    if (remainingMs == 0)
                        return -(int)Errno.EAGAIN;
                }

                var res = await new SemWaitAwaitable(blockedWaiter!, remainingMs);
                if (res == SemWaitOutcome.TimedOut)
                    return -(int)Errno.EAGAIN;

                if (res == SemWaitOutcome.Interrupted)
                    return -(int)Errno.EINTR;
            }
            finally
            {
                set.Waiters.Remove(blockedWaiter!);
            }
        }
    }

    public int SemCtl(int semid, int semnum, int cmd, uint arg, Engine engine, int uid, int gid,
        Process? process = null)
    {
        var actualCmd = cmd & ~LinuxConstants.IPC_64;

        if (actualCmd == LinuxConstants.IPC_INFO || actualCmd == LinuxConstants.SEM_INFO)
        {
            var writeErr = WriteSemInfo(arg, engine, actualCmd == LinuxConstants.SEM_INFO);
            if (writeErr != 0) return writeErr;
            return _setsByIndex.Count > 0 ? _setsByIndex.Keys.Max() : 0;
        }

        if (actualCmd is LinuxConstants.SEM_STAT or LinuxConstants.SEM_STAT_ANY)
        {
            if (!_setsByIndex.TryGetValue(semid, out var statSet))
                return -(int)Errno.EINVAL;

            if (actualCmd == LinuxConstants.SEM_STAT && !CheckPermission(statSet, uid, gid, 4, process))
                return -(int)Errno.EACCES;

            var writeErr = WriteSemidDs(statSet, arg, engine);
            if (writeErr != 0) return writeErr;
            return statSet.Semid;
        }

        if (!_setsBySemid.TryGetValue(semid, out var set))
            return -(int)Errno.EINVAL;

        switch (actualCmd)
        {
            case LinuxConstants.IPC_RMID:
                if (uid != 0 && uid != set.Uid && uid != set.CUid)
                    return -(int)Errno.EPERM;

                _setsBySemid.Remove(semid);
                _setsByIndex.Remove(set.Index);
                if (set.Key != LinuxConstants.IPC_PRIVATE)
                    _setsByKey.Remove(set.Key);

                foreach (var waiter in set.Waiters.ToList())
                    waiter.TryWake(WakeReason.Event);

                ClearUndoForSet(semid);
                return 0;

            case LinuxConstants.IPC_STAT:
                if (!CheckPermission(set, uid, gid, 4, process))
                    return -(int)Errno.EACCES;

                return WriteSemidDs(set, arg, engine);

            case LinuxConstants.IPC_SET:
                if (uid != 0 && uid != set.Uid && uid != set.CUid)
                    return -(int)Errno.EPERM;

                return ReadSemidDsForSet(set, arg, engine);

            case LinuxConstants.GETVAL:
                if (!CheckPermission(set, uid, gid, 4, process)) return -(int)Errno.EACCES;
                if (semnum < 0 || semnum >= set.Values.Length) return -(int)Errno.EINVAL;
                return set.Values[semnum];

            case LinuxConstants.SETVAL:
                if (!CheckPermission(set, uid, gid, 2, process)) return -(int)Errno.EACCES;
                if (semnum < 0 || semnum >= set.Values.Length) return -(int)Errno.EINVAL;

                var setval = (int)arg;
                if (setval < 0 || setval > SEMVMX) return -(int)Errno.ERANGE;

                set.Values[semnum] = (short)setval;
                set.LastPid[semnum] = process?.TGID ?? 0;
                set.CTime = DateTime.UtcNow;

                ClearUndoForSem(semid, semnum);
                WakeWaiters(set);
                return 0;

            case LinuxConstants.GETALL:
                if (!CheckPermission(set, uid, gid, 4, process)) return -(int)Errno.EACCES;
                if (arg == 0) return -(int)Errno.EFAULT;

                var getBuf = new byte[set.Values.Length * 2];
                for (var i = 0; i < set.Values.Length; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(getBuf.AsSpan(i * 2, 2), (ushort)set.Values[i]);

                if (!engine.CopyToUser(arg, getBuf)) return -(int)Errno.EFAULT;
                return 0;

            case LinuxConstants.SETALL:
                if (!CheckPermission(set, uid, gid, 2, process)) return -(int)Errno.EACCES;
                if (arg == 0) return -(int)Errno.EFAULT;

                var setBuf = new byte[set.Values.Length * 2];
                if (!engine.CopyFromUser(arg, setBuf)) return -(int)Errno.EFAULT;

                var newVals = new short[set.Values.Length];
                for (var i = 0; i < set.Values.Length; i++)
                {
                    var v = BinaryPrimitives.ReadUInt16LittleEndian(setBuf.AsSpan(i * 2, 2));
                    if (v > SEMVMX) return -(int)Errno.ERANGE;
                    newVals[i] = (short)v;
                }

                for (var i = 0; i < set.Values.Length; i++)
                {
                    set.Values[i] = newVals[i];
                    set.LastPid[i] = process?.TGID ?? 0;
                }

                set.CTime = DateTime.UtcNow;
                ClearUndoForSet(semid);
                WakeWaiters(set);
                return 0;

            case LinuxConstants.GETPID:
                if (!CheckPermission(set, uid, gid, 4, process)) return -(int)Errno.EACCES;
                if (semnum < 0 || semnum >= set.Values.Length) return -(int)Errno.EINVAL;
                return set.LastPid[semnum];

            case LinuxConstants.GETNCNT:
                if (!CheckPermission(set, uid, gid, 4, process)) return -(int)Errno.EACCES;
                if (semnum < 0 || semnum >= set.Values.Length) return -(int)Errno.EINVAL;
                return set.Waiters.Count(w => w.SemNum == semnum && w.Op < 0);

            case LinuxConstants.GETZCNT:
                if (!CheckPermission(set, uid, gid, 4, process)) return -(int)Errno.EACCES;
                if (semnum < 0 || semnum >= set.Values.Length) return -(int)Errno.EINVAL;
                return set.Waiters.Count(w => w.SemNum == semnum && w.Op == 0);

            default:
                return -(int)Errno.EINVAL;
        }
    }

    public void OnProcessExit(Process process)
    {
        _activeProcesses.Remove(process);
        if (process.SemUndo.Count == 0) return;

        foreach (var ((semid, semNum), undoVal) in process.SemUndo)
        {
            if (undoVal == 0) continue;
            if (!_setsBySemid.TryGetValue(semid, out var set)) continue;
            if (semNum < 0 || semNum >= set.Values.Length) continue;

            var newVal = set.Values[semNum] + undoVal;
            if (newVal < 0) newVal = 0;
            if (newVal > SEMVMX) newVal = SEMVMX;
            set.Values[semNum] = (short)newVal;
            set.OTime = DateTime.UtcNow;
            WakeWaiters(set);
        }

        process.SemUndo.Clear();
    }

    public string GenerateProcSysvipcSem()
    {
        var sb = new StringBuilder();
        sb.Append("       key      semid perms      nsems   uid   gid  cuid  cgid      otime      ctime\n");
        foreach (var set in _setsBySemid.Values.OrderBy(s => s.Semid))
        {
            var otimeSec = set.OTime == DateTime.UnixEpoch ? 0 : (long)(set.OTime - DateTime.UnixEpoch).TotalSeconds;
            var ctimeSec = set.CTime == DateTime.UnixEpoch ? 0 : (long)(set.CTime - DateTime.UnixEpoch).TotalSeconds;
            sb.AppendFormat("{0,10} {1,10}  {2,4} {3,10} {4,5} {5,5} {6,5} {7,5} {8,10} {9,10}\n",
                set.Key,
                set.Semid,
                Convert.ToString(set.Mode & 0x1FF, 8).PadLeft(4, '0'),
                set.Values.Length,
                set.Uid,
                set.Gid,
                set.CUid,
                set.CGid,
                otimeSec,
                ctimeSec);
        }

        return sb.ToString();
    }

    private static bool CheckPermission(SemaphoreSet set, int uid, int gid, int requestedAccess,
        Process? process = null)
    {
        if (uid == 0) return true;

        var mode = set.Mode;
        if (uid == set.Uid || uid == set.CUid)
        {
            var ownerBits = (mode >> 6) & 7;
            if ((requestedAccess & ~ownerBits) == 0) return true;
        }
        else if (gid == set.Gid || gid == set.CGid ||
                 (process != null && (process.SupplementaryGroups.Contains(set.Gid) ||
                                     process.SupplementaryGroups.Contains(set.CGid))))
        {
            var groupBits = (mode >> 3) & 7;
            if ((requestedAccess & ~groupBits) == 0) return true;
        }
        else
        {
            var otherBits = mode & 7;
            if ((requestedAccess & ~otherBits) == 0) return true;
        }

        return false;
    }

    private static void WakeWaiters(SemaphoreSet set)
    {
        foreach (var waiter in set.Waiters.ToList())
            waiter.TryWake(WakeReason.Event);
    }

    private void ClearUndoForSet(int semid)
    {
        foreach (var proc in _activeProcesses.ToList())
        {
            var keysToRemove = proc.SemUndo.Keys.Where(k => k.Semid == semid).ToList();
            foreach (var k in keysToRemove)
                proc.SemUndo.Remove(k);
        }
    }

    private void ClearUndoForSem(int semid, int semnum)
    {
        foreach (var proc in _activeProcesses.ToList())
            proc.SemUndo.Remove((semid, semnum));
    }

    private static int WriteSemidDs(SemaphoreSet set, uint buf, Engine engine)
    {
        if (buf == 0) return -(int)Errno.EFAULT;

        Span<byte> data = stackalloc byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(data[..4], set.Key);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(4, 4), (uint)set.Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(8, 4), (uint)set.Gid);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(12, 4), (uint)set.CUid);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(16, 4), (uint)set.CGid);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(20, 4), (uint)(set.Mode & 0x1FF));
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(24, 2), (ushort)set.Semid);

        var otimeSec = set.OTime == DateTime.UnixEpoch ? 0 : (int)(set.OTime - DateTime.UnixEpoch).TotalSeconds;
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(36, 4), otimeSec);

        var ctimeSec = set.CTime == DateTime.UnixEpoch ? 0 : (int)(set.CTime - DateTime.UnixEpoch).TotalSeconds;
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(44, 4), ctimeSec);

        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(52, 4), (uint)set.Values.Length);

        if (!engine.CopyToUser(buf, data))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private static int ReadSemidDsForSet(SemaphoreSet set, uint buf, Engine engine)
    {
        if (buf == 0) return -(int)Errno.EFAULT;

        Span<byte> data = stackalloc byte[36];
        if (!engine.CopyFromUser(buf, data))
            return -(int)Errno.EFAULT;

        var newUid = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        var newGid = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
        var newMode = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));

        set.Uid = newUid;
        set.Gid = newGid;
        set.Mode = newMode & 0x1FF;
        set.CTime = DateTime.UtcNow;
        return 0;
    }

    private int WriteSemInfo(uint buf, Engine engine, bool semInfo)
    {
        if (buf == 0) return -(int)Errno.EFAULT;

        Span<byte> data = stackalloc byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(data[..4], SEMMNI);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(4, 4), SEMMNI);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(8, 4), SEMMNS);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(12, 4), 32);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(16, 4), SEMMSL);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(20, 4), SEMOPM);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(24, 4), 32);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(28, 4), _setsBySemid.Count);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(32, 4), SEMVMX);

        var totalSems = 0;
        foreach (var s in _setsBySemid.Values) totalSems += s.Values.Length;
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(36, 4), totalSems);

        if (!engine.CopyToUser(buf, data))
            return -(int)Errno.EFAULT;

        return 0;
    }
}
