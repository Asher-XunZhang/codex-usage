using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

internal sealed class ChildJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Basic { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Minimum, Maximum; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public Basic Basic; public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref Limits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private IntPtr handle;
    public ChildJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var limits = new Limits { Basic = new Basic { Flags = 0x2000 } };
        if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<Limits>())) { Dispose(); throw new System.ComponentModel.Win32Exception(); }
    }
    public Process Start(string executable, params string[] args) => StartWithEnvironment(executable, null, args);
    public Process StartWithEnvironment(string executable, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (environment != null) foreach (var entry in environment) info.Environment[entry.Key] = entry.Value;
        var child = Process.Start(info) ?? throw new IOException("无法启动进程");
        if (!AssignProcessToJobObject(handle, child.Handle)) { child.Kill(true); child.Dispose(); throw new System.ComponentModel.Win32Exception(); }
        return child;
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}

internal static class Processes
{
    public static async Task<string> ReadBounded(StreamReader reader, int maximum, CancellationToken token)
    {
        var output = new StringBuilder(); char[] buffer = new char[4096];
        int length; while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (output.Length + length > maximum) throw new IOException("组件响应超出大小限制"); output.Append(buffer, 0, length);
        }
        return output.ToString();
    }
    public static async Task<string> Run(ChildJob job, string executable, string[] args, int timeout = 120000)
    {
        using var child = job.Start(executable, args);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            child.StandardInput.Close();
            var stdout = ReadBounded(child.StandardOutput, 2_097_152, cancellation.Token);
            var stderr = ReadBounded(child.StandardError, 65536, cancellation.Token);
            await Task.WhenAll(stdout, stderr, child.WaitForExitAsync(cancellation.Token));
            if (child.ExitCode != 0) throw new IOException((await stderr).Trim() is { Length: > 0 } message ? message : "统计组件退出异常");
            return await stdout;
        }
        finally { if (!child.HasExited) child.Kill(true); }
    }
}
