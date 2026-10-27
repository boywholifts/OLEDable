using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OLEDable
{
    /// <summary>
    /// Every helper the app starts (ffmpeg, RIFE, Real-ESRGAN, RePKG) goes into a Windows job object that is
    /// closed with the app, so Windows ends them if OLEDable exits or crashes in the middle of an export.
    /// Without it they'd keep running in the background, holding the GPU and the temporary files.
    /// </summary>
    public static class ChildProcesses
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong R, W, O, RB, WB, OB; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        const int ExtendedLimitInformation = 9;
        const uint KillOnJobClose = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static readonly IntPtr job = Create();

        static IntPtr Create()
        {
            try
            {
                var h = CreateJobObject(IntPtr.Zero, null);
                if (h == IntPtr.Zero) return IntPtr.Zero;
                var info = new ExtendedLimits();
                info.Basic.LimitFlags = KillOnJobClose;
                SetInformationJobObject(h, ExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(ExtendedLimits)));
                return h;   // kept open for the app's lifetime; Windows closes it (and ends the helpers) when the app exits
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>Ties a just-started helper process to the app's lifetime.</summary>
        public static void Track(Process p)
        {
            if (job == IntPtr.Zero || p == null) return;
            try { AssignProcessToJobObject(job, p.Handle); } catch { }
        }
    }
}
