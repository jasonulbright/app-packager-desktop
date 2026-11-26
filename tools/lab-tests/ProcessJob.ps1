<#
.SYNOPSIS
    Starts the processes of a lab run inside a Windows job object, so that a run can never stop a process that it did not start.

.DESCRIPTION
    A process is created suspended, put into a job of its own with the handle that its creation returned, and then resumed. Everything it starts
    joins the same job. Stopping the job stops its members and nothing else. No process is looked up by its id, its parent id or its name:
    Windows uses process ids again, and a walk over parent ids once reached a system process of the signed-in session and ended that session.

    The process object that is returned is opened while the creation handle is still open. Windows does not use an id again while a handle to its
    process is open, so the object names the process that was started and no other.

    The job is set to end its members when its last handle closes. The script that starts a run closes the job in a finally block; when the
    whole PowerShell process ends in any way, Windows closes the handle.
#>
if (-not ('ApdLab.JobProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ApdLab
{
    public sealed class JobProcess
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);

        private IntPtr _job;

        public Process Process { get; private set; }

        private static IntPtr OutputFile(string path)
        {
            var attributes = new SecurityAttributes();
            attributes.Length = Marshal.SizeOf(typeof(SecurityAttributes));
            attributes.InheritHandle = 1;
            // GENERIC_WRITE, FILE_SHARE_READ, CREATE_ALWAYS
            IntPtr handle = CreateFile(path ?? "NUL", 0x40000000, 1, ref attributes, 2, 0x80, IntPtr.Zero);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }

        public static JobProcess Start(string commandLine, string standardOutput, string standardError)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null); // no name: the job belongs to this process alone
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr output = IntPtr.Zero, error = IntPtr.Zero;
            var information = new ProcessInformation();
            bool resumed = false;
            try
            {
                var limits = new ExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits)))) throw new Win32Exception(Marshal.GetLastWin32Error());
                output = OutputFile(standardOutput);
                error = OutputFile(standardError);
                var startup = new StartupInfo();
                startup.cb = Marshal.SizeOf(typeof(StartupInfo));
                startup.dwFlags = 0x100; // STARTF_USESTDHANDLES
                startup.hStdOutput = output;
                startup.hStdError = error;
                // CREATE_SUSPENDED | CREATE_NO_WINDOW: the child gets a console of its own, so no console event is shared with the caller.
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, 0x4 | 0x08000000, IntPtr.Zero, null, ref startup, out information))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!AssignProcessToJobObject(job, information.hProcess)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var process = System.Diagnostics.Process.GetProcessById(information.dwProcessId);
                IntPtr keep = process.Handle; // opened while the creation handle is open, so it is this process
                if (ResumeThread(information.hThread) == 0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
                resumed = true;
                var run = new JobProcess();
                run._job = job;
                run.Process = process;
                return run;
            }
            finally
            {
                if (!resumed)
                {
                    // Only the handle of the creation above is used here.
                    if (information.hProcess != IntPtr.Zero) TerminateProcess(information.hProcess, 1);
                    CloseHandle(job);
                }
                if (information.hThread != IntPtr.Zero) CloseHandle(information.hThread);
                if (information.hProcess != IntPtr.Zero) CloseHandle(information.hProcess);
                if (output != IntPtr.Zero) CloseHandle(output);
                if (error != IntPtr.Zero) CloseHandle(error);
            }
        }

        public void Stop()
        {
            if (_job == IntPtr.Zero) return;
            TerminateJobObject(_job, 1);
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }
}
'@
}

# Starts a program in a job of its own. The arguments are joined with spaces, so an argument with a space carries its own quotes.
function Start-JobProcess {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$ArgumentList = @(), [string]$StandardOutput, [string]$StandardError)
    $commandLine = (@("`"$FilePath`"") + $ArgumentList) -join ' '
    $out = if ($StandardOutput) { $StandardOutput } else { [NullString]::Value }
    $err = if ($StandardError) { $StandardError } else { [NullString]::Value }
    [ApdLab.JobProcess]::Start($commandLine, $out, $err)
}

# Stops every member of the job that still runs, then closes the job. Safe to call again, and for a process that has ended.
function Stop-JobProcess {
    param([Parameter(Mandatory)]$Run)
    $Run.Stop()
}
