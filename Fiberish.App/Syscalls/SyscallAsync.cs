using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Bifrost.Core;
using Bifrost.Native;
using Bifrost.VFS;

namespace Bifrost.Syscalls
{
    public static class SyscallAsync
    {
        private const int FD_SETSIZE = 1024;
        private const int NFDBITS = 32;

        public static int Poll(SyscallManager sm, uint fdsAddr, uint nfds, int timeout)
        {
            // 1. Scan
            int readyCount = ScanPoll(sm, fdsAddr, nfds);

            // 2. If ready or no timeout, return
            if (readyCount > 0 || timeout == 0)
            {
                return readyCount;
            }

            // 3. Async Wait
            var task = Scheduler.CurrentTask;
            if (task == null) return 0;

            int waitMs = timeout < 0 ? -1 : timeout;

            task.BlockingTask = System.Threading.Tasks.Task.Run(async () =>
            {
                if (waitMs > 0) await System.Threading.Tasks.Task.Delay(waitMs);
                else if (waitMs < 0) await System.Threading.Tasks.Task.Delay(100); 

                await Bifrost.Core.Task.GIL.WaitAsync();
                try
                {
                    return ScanPoll(sm, fdsAddr, nfds);
                }
                finally
                {
                    Bifrost.Core.Task.GIL.Release();
                }
            });

            sm.Engine.Stop();
            return 0; 
        }

        private static int ScanPoll(SyscallManager sm, uint fdsAddr, uint nfds)
        {
            int readyCount = 0;
            int sizeOfPollfd = Marshal.SizeOf<Pollfd>();

            for (uint i = 0; i < nfds; i++)
            {
                uint itemAddr = fdsAddr + i * (uint)sizeOfPollfd;
                Pollfd pfd = ReadStruct<Pollfd>(sm.Engine, itemAddr);
                pfd.Revents = 0;

                if (pfd.Fd >= 0)
                {
                    // Access FDs carefully (thread safe because GIL held)
                    Bifrost.VFS.File? file = null;
                    if (sm.FDs.TryGetValue(pfd.Fd, out file))
                    {
                        short revents = file.Poll(pfd.Events);
                        if (revents != 0)
                        {
                            pfd.Revents = revents;
                            readyCount++;
                        }
                    }
                    else
                    {
                        pfd.Revents = PollEvents.POLLNVAL;
                        readyCount++;
                    }
                }
                
                WriteStruct(sm.Engine, itemAddr, pfd);
            }
            return readyCount;
        }

        public static int Select(SyscallManager sm, int n, uint inp, uint outp, uint exp, uint tvp)
        {
            if (n < 0 || n > FD_SETSIZE) return -(int)Errno.EINVAL;

            Timeval timeoutVal = new Timeval { TvSec = 0, TvUsec = 0 };
            bool hasTimeout = false;
            if (tvp != 0)
            {
                timeoutVal = ReadStruct<Timeval>(sm.Engine, tvp);
                hasTimeout = true;
            }

            // 1. Scan
            int ready = ScanSelect(sm, n, inp, outp, exp, out var resIn, out var resOut, out var resEx);

            if (ready > 0)
            {
                WriteSelectResults(sm, inp, outp, exp, resIn, resOut, resEx);
                return ready;
            }

            // 2. Check timeout
            long timeoutMs = 0;
            if (hasTimeout)
            {
                timeoutMs = timeoutVal.TvSec * 1000 + timeoutVal.TvUsec / 1000;
                if (timeoutMs == 0) return 0; // Pure polling
            }
            else
            {
                timeoutMs = -1; // Infinite
            }

            // 3. Async Wait
            var task = Scheduler.CurrentTask;
            if (task == null) return 0;

            task.BlockingTask = System.Threading.Tasks.Task.Run(async () =>
            {
                 if (timeoutMs > 0) await System.Threading.Tasks.Task.Delay((int)timeoutMs);
                 else if (timeoutMs < 0) await System.Threading.Tasks.Task.Delay(100);

                 await Bifrost.Core.Task.GIL.WaitAsync();
                 try
                 {
                     int r = ScanSelect(sm, n, inp, outp, exp, out var rIn, out var rOut, out var rEx);
                     if (r > 0 || timeoutMs != -1) 
                     {
                          WriteSelectResults(sm, inp, outp, exp, rIn, rOut, rEx);
                     }
                     return r;
                 }
                 finally
                 {
                     Bifrost.Core.Task.GIL.Release();
                 }
            });

            sm.Engine.Stop();
            return 0;
        }

        private static int ScanSelect(SyscallManager sm, int n, uint inp, uint outp, uint exp, 
                                      out uint[] resIn, out uint[] resOut, out uint[] resEx)
        {
            int ready = 0;
            int intCount = (n + NFDBITS - 1) / NFDBITS;
            if (intCount > FD_SETSIZE / 32) intCount = FD_SETSIZE / 32;

            resIn = new uint[intCount];
            resOut = new uint[intCount];
            resEx = new uint[intCount];

            uint[] inSets = new uint[intCount];
            uint[] outSets = new uint[intCount];
            uint[] exSets = new uint[intCount];

            if (inp != 0) ReadSpan(sm.Engine, inp, inSets);
            if (outp != 0) ReadSpan(sm.Engine, outp, outSets);
            if (exp != 0) ReadSpan(sm.Engine, exp, exSets);

            for (int i = 0; i < n; i++)
            {
                int wordIndex = i / NFDBITS;
                int bitIndex = i % NFDBITS;
                uint mask = 1u << bitIndex;

                bool checkRead = (inSets[wordIndex] & mask) != 0;
                bool checkWrite = (outSets[wordIndex] & mask) != 0;
                bool checkEx = (exSets[wordIndex] & mask) != 0;

                if (!checkRead && !checkWrite && !checkEx) continue;

                Bifrost.VFS.File? file = null;
                if (!sm.FDs.TryGetValue(i, out file))
                {
                    return -(int)Errno.EBADF; 
                }

                short pollEvents = 0;
                if (checkRead) pollEvents |= PollEvents.POLLIN;
                if (checkWrite) pollEvents |= PollEvents.POLLOUT;
                if (checkEx) pollEvents |= PollEvents.POLLPRI;

                short revents = file.Poll(pollEvents);

                if ((revents & PollEvents.POLLIN) != 0 && checkRead)
                {
                    resIn[wordIndex] |= mask;
                    ready++;
                }
                if ((revents & PollEvents.POLLOUT) != 0 && checkWrite)
                {
                    resOut[wordIndex] |= mask;
                    ready++;
                }
                if ((revents & PollEvents.POLLPRI) != 0 && checkEx)
                {
                    resEx[wordIndex] |= mask;
                    ready++;
                }
            }
            return ready;
        }

        private static void WriteSelectResults(SyscallManager sm, uint inp, uint outp, uint exp, 
                                               uint[] resIn, uint[] resOut, uint[] resEx)
        {
            if (inp != 0) WriteSpan(sm.Engine, inp, resIn);
            if (outp != 0) WriteSpan(sm.Engine, outp, resOut);
            if (exp != 0) WriteSpan(sm.Engine, exp, resEx);
        }

        // --- Memory Helpers ---
        private static T ReadStruct<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(Engine engine, uint addr) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] buf = new byte[size];
            if (!engine.CopyFromUser(addr, buf))
                throw new InvalidOperationException($"EFAULT: Failed to read struct {typeof(T).Name} from 0x{addr:x}");
            
            GCHandle handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            } finally {
                handle.Free();
            }
        }

        private static void WriteStruct<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(Engine engine, uint addr, T val) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] buf = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try {
                Marshal.StructureToPtr(val, ptr, false);
                Marshal.Copy(ptr, buf, 0, size);
            } finally {
                Marshal.FreeHGlobal(ptr);
            }
            if (!engine.CopyToUser(addr, buf))
                throw new InvalidOperationException($"EFAULT: Failed to write struct {typeof(T).Name} to 0x{addr:x}");
        }

        private static void ReadSpan(Engine engine, uint addr, uint[] dest)
        {
            int bytes = dest.Length * 4;
            byte[] buf = new byte[bytes];
            if (!engine.CopyFromUser(addr, buf))
                throw new InvalidOperationException($"EFAULT: Failed to read span from 0x{addr:x}");
            Buffer.BlockCopy(buf, 0, dest, 0, bytes);
        }

        private static void WriteSpan(Engine engine, uint addr, uint[] src)
        {
            int bytes = src.Length * 4;
            byte[] buf = new byte[bytes];
            Buffer.BlockCopy(src, 0, buf, 0, bytes);
            if (!engine.CopyToUser(addr, buf))
                 throw new InvalidOperationException($"EFAULT: Failed to write span to 0x{addr:x}");
        }
    }
}
