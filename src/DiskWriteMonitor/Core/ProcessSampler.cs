using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace DiskWriteMonitor.Core;

public readonly record struct ProcWrite(int Pid, string Name, ulong Bytes);

/// <summary>
/// 进程级写入量。对应 macOS 的 proc_pid_rusage / ri_diskio_byteswritten。
/// Windows 用 GetProcessIoCounters 的 WriteTransferCount，口径是"系统写请求量"，
/// 含管道/套接字/控制台写入，通常远大于固件落盘量 —— 这一点由契约 §3.2 明确要求如实展示。
/// </summary>
public static class ProcessSampler
{
    public static Dictionary<int, ProcWrite> Snapshot(out int total, out int failed)
    {
        total = 0;
        failed = 0;
        var result = new Dictionary<int, ProcWrite>(512);

        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return result; }

        total = procs.Length;

        foreach (var pr in procs)
        {
            string name;
            int pid;
            try
            {
                pid = pr.Id;
                name = pr.ProcessName;
            }
            catch { continue; }

            var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid)
            {
                h.Dispose();
                // 有些进程不认 LIMITED，退回 QUERY_INFORMATION 再试一次
                h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, pid);
                if (h.IsInvalid)
                {
                    failed++;
                    h.Dispose();
                    continue;
                }
            }

            try
            {
                if (Native.GetProcessIoCounters(h, out var io))
                    result[pid] = new ProcWrite(pid, name, io.WriteTransferCount);
            }
            finally
            {
                h.Dispose();
            }
        }

        return result;
    }
}
