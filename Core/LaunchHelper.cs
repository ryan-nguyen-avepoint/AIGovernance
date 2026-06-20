using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProcessFileMonitor.Logging;

namespace ProcessFileMonitor.Core
{
    /// <summary>
    /// Resolves executable paths for Claude and Openclaw via environment variables,
    /// then launches them.
    ///
    /// Openclaw is launched with CREATE_NEW_CONSOLE so it gets its own terminal
    /// window and does NOT inherit / clobber the monitor's console.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class LaunchHelper
    {
        // ── Environment variables checked (priority order) ────────────────────
        private static readonly string[] ClaudeEnvVars =
        [
            "CLAUDE_EXECUTABLE",
            "CLAUDE_PATH",
            "CLAUDE_HOME",
        ];

        private static readonly string[] OpenclawEnvVars =
        [
            "OPENCLAW_EXECUTABLE",
            "OPENCLAW_PATH",
            "OPENCLAW_HOME",
        ];

        // ── Default install locations ─────────────────────────────────────────
        private static readonly string[] ClaudeDefaults =
        [
            @"%LOCALAPPDATA%\AnthropicClaude\claude.exe",
            @"%PROGRAMFILES%\Anthropic\Claude\claude.exe",
            @"%PROGRAMFILES(X86)%\Anthropic\Claude\claude.exe",
            @"%APPDATA%\Claude\claude.exe",
        ];

        private static readonly string[] OpenclawDefaults =
        [
            @"%LOCALAPPDATA%\Openclaw\openclaw.exe",
            @"%PROGRAMFILES%\Openclaw\openclaw.exe",
        ];

        private const string DefaultOpenclawCommand = "chat";

        // ── Public builders ───────────────────────────────────────────────────

        public static Process? LaunchClaude(AuditLogger logger)
        {
            string? exe = ResolveExecutable("claude", ClaudeEnvVars, ClaudeDefaults, logger);
            if (exe == null) return null;

            logger.LogInfo($"[LaunchHelper] Launching Claude: {exe}");

            // Claude is a GUI app — UseShellExecute=true is cleanest,
            // it won't touch our console at all.
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            };
            return Process.Start(psi);
        }

        public static Process? LaunchOpenclaw(AuditLogger logger,
            string? commandOverride = null)
        {
            string? exe = ResolveExecutable("openclaw", OpenclawEnvVars, OpenclawDefaults, logger);
            if (exe == null) return null;

            string command = commandOverride
                ?? Environment.GetEnvironmentVariable("OPENCLAW_COMMAND")
                ?? DefaultOpenclawCommand;

            logger.LogInfo($"[LaunchHelper] Launching Openclaw: {exe} {command}");
            logger.LogInfo("[LaunchHelper] Openclaw will open in its OWN console window.");

            // Openclaw is a console app (interactive TUI/chat).
            // We MUST NOT let it share our console — it would overwrite our output.
            //
            // Solution: Win32 CreateProcess with CREATE_NEW_CONSOLE (0x00000010).
            // .NET's ProcessStartInfo has no direct flag for this, but we can reach
            // it via UseShellExecute=true + the shell respects CREATE_NEW_CONSOLE,
            // OR we call CreateProcess directly.
            //
            // Simplest reliable method: ProcessStartInfo with UseShellExecute=true
            // launches via ShellExecuteEx which always creates a new console for
            // console subsystem executables.
            //
            // If that is not sufficient (e.g. the exe is a Windows subsystem app
            // pretending to be console), we fall back to the raw Win32 path.
            return LaunchInNewConsole(exe, command, logger);
        }

        // ── Core: launch a console app in a completely separate window ────────

        private static Process? LaunchInNewConsole(string exe, string args,
            AuditLogger logger)
        {
            // Strategy A: UseShellExecute = true
            // ShellExecuteEx always allocates a new console for console-subsystem
            // executables. This is the simplest approach and works for >99% of cases.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,   // ← key: new console, no handle inheritance
                    WindowStyle = ProcessWindowStyle.Normal,
                };

                var p = Process.Start(psi);
                if (p != null)
                {
                    logger.LogInfo($"[LaunchHelper] Started via ShellExecute: PID={p.Id}");
                    return p;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[LaunchHelper] ShellExecute failed ({ex.Message}), " +
                                  "falling back to CreateProcess...");
            }

            // Strategy B: raw Win32 CreateProcess with CREATE_NEW_CONSOLE flag.
            // Used when ShellExecute is unavailable (e.g. service context, no desktop).
            return LaunchViaCreateProcess(exe, args, logger);
        }

        // ── Win32 CreateProcess fallback ──────────────────────────────────────

        // dwCreationFlags values
        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        // Combine: new console + new process group (clean Ctrl+C separation)
        private const uint LaunchFlags = CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize;
            public uint dwXCountChars, dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,      // false → no handle inheritance
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static Process? LaunchViaCreateProcess(string exe, string args,
            AuditLogger logger)
        {
            // CommandLine must be writable (CreateProcess may modify it)
            string cmdLine = $"\"{exe}\" {args}";

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            bool ok = CreateProcess(
                lpApplicationName: null,
                lpCommandLine: cmdLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,          // ← do NOT inherit our console handles
                dwCreationFlags: LaunchFlags,    // ← CREATE_NEW_CONSOLE
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: null,
                lpStartupInfo: ref si,
                lpProcessInformation: out var pi);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                logger.LogError($"[LaunchHelper] CreateProcess failed: " +
                                $"Win32 error {err} — {new Win32Exception(err).Message}");
                return null;
            }

            // Close the handles we got back — we'll track the process by PID
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);

            logger.LogInfo($"[LaunchHelper] Started via CreateProcess: PID={pi.dwProcessId}");

            try { return Process.GetProcessById(pi.dwProcessId); }
            catch { return null; }
        }

        // ── Executable resolution (unchanged) ────────────────────────────────

        private static string? ResolveExecutable(
            string appName,
            string[] envVars,
            string[] defaults,
            AuditLogger logger)
        {
            // 1) Environment variables
            foreach (var envVar in envVars)
            {
                string? val = Environment.GetEnvironmentVariable(envVar);
                if (string.IsNullOrWhiteSpace(val)) continue;

                string expanded = Environment.ExpandEnvironmentVariables(val);
                logger.LogDebug($"[LaunchHelper] Checking env var {envVar} = '{expanded}'");

                if (Directory.Exists(expanded))
                {
                    string candidate = Path.Combine(expanded, $"{appName}.exe");
                    if (File.Exists(candidate))
                    {
                        logger.LogInfo($"[LaunchHelper] Found via {envVar} (dir): {candidate}");
                        return candidate;
                    }
                }
                else if (File.Exists(expanded))
                {
                    logger.LogInfo($"[LaunchHelper] Found via {envVar}: {expanded}");
                    return expanded;
                }
                else
                {
                    logger.LogWarning($"[LaunchHelper] {envVar} points to non-existent: {expanded}");
                }
            }

            // 2) PATH
            string? onPath = FindOnPath(appName, logger);
            if (onPath != null) return onPath;

            // 3) Default install locations
            foreach (var defaultPath in defaults)
            {
                string expanded = Environment.ExpandEnvironmentVariables(defaultPath);
                logger.LogDebug($"[LaunchHelper] Checking default: {expanded}");
                if (File.Exists(expanded))
                {
                    logger.LogInfo($"[LaunchHelper] Found at default: {expanded}");
                    return expanded;
                }
            }

            logger.LogError($"[LaunchHelper] Could not find '{appName}' executable.");
            logger.LogError("  Set one of these environment variables:");
            foreach (var v in envVars)
                logger.LogError($"    {v}=C:\\path\\to\\{appName}.exe");

            return null;
        }

        private static string? FindOnPath(string exeName, AuditLogger logger)
        {
            string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";

            foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir.Trim(), exeName + ext);
                    if (File.Exists(candidate))
                    {
                        logger.LogInfo($"[LaunchHelper] Found '{exeName}' on PATH: {candidate}");
                        return candidate;
                    }
                }
            }
            return null;
        }
    }
}
