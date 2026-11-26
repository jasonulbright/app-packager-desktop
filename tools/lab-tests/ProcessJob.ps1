<#
.SYNOPSIS
    Stops the processes of a lab run through a Windows job object, so that a run can never stop a process that it did not start.

.DESCRIPTION
    A process that is put into a job stays in it, and every process that it starts afterwards joins the same job. Stopping the job stops its
    members and nothing else. No process is looked up by its id, its parent id or its name: Windows uses process ids again, and a walk over
    parent ids once reached a system process of the signed-in session and ended that session.

    A process is put into the job right after its start. A child that it starts before that moment stays outside the job and keeps running.
    That failure is harmless; stopping a process that the run does not own is not.

    The job closes with its handle: when the script ends in any way, Windows stops the members that are still running.
#>
if (-not ('ApdLab.JobNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace ApdLab
{
    public static class JobNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BasicLimits
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
        public struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        public static IntPtr CreateKillOnCloseJob()
        {
            // A job without a name belongs to this process alone.
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
    }
}
'@
}

# Starts a process in a job of its own. Returns the process and the job. Takes the arguments of Start-Process.
function Start-JobProcess {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$ArgumentList = @(), [string]$StandardOutput, [string]$StandardError)
    $job = [ApdLab.JobNative]::CreateKillOnCloseJob()
    $parameters = @{ FilePath = $FilePath; PassThru = $true; NoNewWindow = $true }
    if ($ArgumentList.Count -gt 0) { $parameters.ArgumentList = $ArgumentList }
    if ($StandardOutput) { $parameters.RedirectStandardOutput = $StandardOutput }
    if ($StandardError) { $parameters.RedirectStandardError = $StandardError }
    $process = Start-Process @parameters
    # The handle is read at once, so that the exit code stays readable and the handle is the one of this very process.
    $handle = $process.Handle
    if (-not [ApdLab.JobNative]::AssignProcessToJobObject($job, $handle) -and -not $process.HasExited) {
        $reason = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $process.Kill()
        $null = [ApdLab.JobNative]::CloseHandle($job)
        throw "The process could not join the job (error $reason), so it was ended and not used."
    }
    [pscustomobject]@{ Process = $process; Job = $job }
}

# Stops every member of the job that still runs, then closes the job. Safe to call for a process that has ended.
function Stop-JobProcess {
    param([Parameter(Mandatory)]$Run)
    if ($Run.Job -eq [IntPtr]::Zero) { return }
    $null = [ApdLab.JobNative]::TerminateJobObject($Run.Job, 1)
    $null = [ApdLab.JobNative]::CloseHandle($Run.Job)
    $Run.Job = [IntPtr]::Zero
}
