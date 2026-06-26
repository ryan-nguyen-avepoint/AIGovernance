using System.Collections.Concurrent;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ProcessFileMonitor.Logging;

namespace ProcessFileMonitor.Core
{
    [SupportedOSPlatform("windows")]
    public class OpenclawProcessMonitor
    {
        private readonly AuditLogger _logger;
        public static readonly ConcurrentDictionary<int, string> TrackedPids = new();

        public OpenclawProcessMonitor(AuditLogger logger)
        {
            _logger = logger;
        }

        public static bool IsOpenclaw(string? text) => text?.Contains("openclaw", StringComparison.OrdinalIgnoreCase) == true;
        public static string GetCommandLine(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    new ObjectQuery($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}"));
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        return obj["CommandLine"]?.ToString() ?? "";
                    }
                }
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80071069))
            {
                // Process no longer exists - this is expected behavior
                return "";
            }
            catch { }
            return "";
        }

        public void InitExistingProcesses()
        {
            var allCmdLines = new Dictionary<int, string>();
            var candidates = new List<(int pid, int parentPid, string cmd, string exe)>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath FROM Win32_Process");

            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    int parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                    string cmd = obj["CommandLine"]?.ToString() ?? "";
                    string exe = obj["ExecutablePath"]?.ToString() ?? "";
                    allCmdLines[pid] = cmd;
                    candidates.Add((pid, parentPid, cmd, exe));
                }
                catch
                {
                }
            }

            foreach (var (pid, parentPid, cmd, exe) in candidates)
            {
                string parentCmd = allCmdLines.GetValueOrDefault(parentPid, "");
                if (IsOpenclaw(parentCmd) && (IsOpenclaw(cmd) || IsOpenclaw(exe)))
                {
                    TrackedPids[pid] = cmd;
                    _logger.LogInfo($"[SEED] PID={pid} | {cmd}");
                }
            }
        }
    }
}