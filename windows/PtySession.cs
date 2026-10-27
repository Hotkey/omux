using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Omux;

/// <summary>
/// A process running in a Windows pseudo console (ConPTY). The process is placed in a job
/// object, so killing the session takes down everything it spawned (claude, node, …).
/// </summary>
public sealed class PtySession : IDisposable
{
    /// <summary>Decoded UTF-8 output; raised on a background thread.</summary>
    public event Action<string>? Output;
    /// <summary>Raised once, on a background thread, after the process exits and its output is drained.</summary>
    public event Action? Exited;

    public int ProcessId { get; }

    private readonly IntPtr _pseudoConsole;
    private readonly SafeFileHandle _input;
    private readonly SafeFileHandle _output;
    private readonly FileStream _inputStream;
    private readonly IntPtr _process;
    private readonly IntPtr _job;
    private int _closed;

    private PtySession(IntPtr pseudoConsole, SafeFileHandle input, SafeFileHandle output, IntPtr process, int processId, IntPtr job)
    {
        _pseudoConsole = pseudoConsole;
        _input = input;
        _output = output;
        _process = process;
        ProcessId = processId;
        _job = job;
        _inputStream = new FileStream(_input, FileAccess.Write, 1, false);
    }

    public static PtySession Start(string commandLine, string directory, IDictionary<string, string> environment, short columns, short rows)
    {
        if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
            !Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        var hr = Native.CreatePseudoConsole(new Native.Coord(columns, rows), inputRead, outputWrite, 0, out var pseudoConsole);
        if (hr != 0) throw new Win32Exception(hr);

        var attributeSize = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
        var attributes = Marshal.AllocHGlobal(attributeSize);
        var environmentBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeSize) ||
                !Native.UpdateProcThreadAttribute(attributes, 0, Native.ProcThreadAttributePseudoConsole, pseudoConsole,
                    IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new Native.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<Native.StartupInfoEx>();
            // Null std handles: the child must use the pseudo console, never handles omux inherited.
            startup.StartupInfo.dwFlags = Native.StartfUseStdHandles;

            var command = new StringBuilder(commandLine);
            if (!Native.CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false,
                    Native.ExtendedStartupInfoPresent | Native.CreateUnicodeEnvironment | Native.CreateSuspended,
                    environmentBlock, directory, ref startup, out var info))
                throw new Win32Exception();

            var job = Native.CreateJobObjectW(IntPtr.Zero, null);
            var limits = new Native.JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = Native.JobObjectLimitKillOnJobClose;
            Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf(limits));
            Native.AssignProcessToJobObject(job, info.hProcess);
            Native.ResumeThread(info.hThread);
            Native.CloseHandle(info.hThread);

            // The pseudo console holds its own ends of the pipes now.
            inputRead.Dispose();
            outputWrite.Dispose();

            var session = new PtySession(pseudoConsole, inputWrite, outputRead, info.hProcess, info.dwProcessId, job);
            session.StartThreads();
            return session;
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static string EnvironmentBlock(IDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(key).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    private void StartThreads()
    {
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "omux pty reader" };
        reader.Start();
        var waiter = new Thread(() =>
        {
            Native.WaitForSingleObject(_process, Native.Infinite);
            // Closing the pseudo console flushes the last output and ends the read loop.
            Close();
        }) { IsBackground = true, Name = "omux pty waiter" };
        waiter.Start();
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        using var stream = new FileStream(_output, FileAccess.Read, 1, false);
        while (true)
        {
            int read;
            try { read = stream.Read(bytes, 0, bytes.Length); }
            catch { break; }
            if (read <= 0) break;
            var count = decoder.GetChars(bytes, 0, read, chars, 0);
            if (count > 0) Output?.Invoke(new string(chars, 0, count));
        }
        Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (_closed != 0) return;
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            _inputStream.Write(data, 0, data.Length);
            _inputStream.Flush();
        }
        catch { }
    }

    public void Resize(int columns, int rows)
    {
        if (_closed != 0 || columns < 1 || rows < 1) return;
        Native.ResizePseudoConsole(_pseudoConsole, new Native.Coord((short)columns, (short)rows));
    }

    /// <summary>Kills the shell and everything it spawned, not just the shell.</summary>
    public void Kill() => Native.TerminateJobObject(_job, 1);

    /// <summary>Whether the shell has a child process (claude, a build, …).</summary>
    public bool HasChildProcess()
    {
        if (_closed != 0) return false;
        var snapshot = Native.CreateToolhelp32Snapshot(Native.Th32csSnapProcess, 0);
        if (snapshot == Native.InvalidHandle) return false;
        try
        {
            var entry = new Native.ProcessEntry32 { dwSize = Marshal.SizeOf<Native.ProcessEntry32>() };
            if (!Native.Process32FirstW(snapshot, ref entry)) return false;
            do
            {
                if (entry.th32ParentProcessID == ProcessId && entry.th32ProcessID != ProcessId) return true;
            } while (Native.Process32NextW(snapshot, ref entry));
            return false;
        }
        finally { Native.CloseHandle(snapshot); }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _inputStream.Dispose(); } catch { }
        Native.ClosePseudoConsole(_pseudoConsole);
    }

    public void Dispose()
    {
        Kill();
        // ClosePseudoConsole can block until output is drained; never on the UI thread.
        Task.Run(() =>
        {
            Close();
            Native.CloseHandle(_job);
            Native.CloseHandle(_process);
        });
    }

    private static class Native
    {
        public const uint ExtendedStartupInfoPresent = 0x00080000;
        public const uint CreateUnicodeEnvironment = 0x00000400;
        public const uint CreateSuspended = 0x00000004;
        public const int StartfUseStdHandles = 0x00000100;
        public static readonly IntPtr ProcThreadAttributePseudoConsole = 0x00020016;
        public const uint JobObjectLimitKillOnJobClose = 0x00002000;
        public const int JobObjectExtendedLimitInformationClass = 9;
        public const uint Th32csSnapProcess = 0x00000002;
        public const uint Infinite = 0xFFFFFFFF;
        public static readonly IntPtr InvalidHandle = -1;

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Coord(short x, short y)
        {
            public readonly short X = x;
            public readonly short Y = y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32
        {
            public int dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll")]
        public static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfoEx lpStartupInfo, out ProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

        [DllImport("kernel32.dll")]
        public static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    }
}
