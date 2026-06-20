using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using ProcessFileMonitor.Logging;

namespace ProcessFileMonitor.Core
{
    /// <summary>
    /// Maintains a live set of PIDs for the target process and its entire descendant tree.
    /// Uses WMI/ToolHelp32 to enumerate child processes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class ProcessTreeTracker
    {
        private readonly AuditLogger _logger;
        private readonly ConcurrentDictionary<int, ProcessInfo> _tracked = new();
        private int _rootPid;

        public IReadOnlyCollection<int> TrackedPids => _tracked.Keys.ToList();

        public ProcessTreeTracker(AuditLogger logger)
        {
            _logger = logger;
        }

        public void Initialize(int rootPid)
        {
            _rootPid = rootPid;
            _tracked.Clear();
            ScanTree(rootPid, depth: 0);
        }

        /// <summary>Returns true if the given PID is part of the tracked tree.</summary>
        public bool IsTracked(int pid) => _tracked.ContainsKey(pid);

        /// <summary>
        /// Periodically called to find newly spawned child processes.
        /// </summary>
        public void RefreshChildren()
        {
            // Remove dead processes
            foreach (var kvp in _tracked.ToArray())
            {
                try
                {
                    var p = Process.GetProcessById(kvp.Key);
                    if (p.HasExited)
                    {
                        if (_tracked.TryRemove(kvp.Key, out _))
                            _logger.LogInfo($"[Tree] Process exited: PID={kvp.Key} ({kvp.Value.Name})");
                    }
                }
                catch
                {
                    if (_tracked.TryRemove(kvp.Key, out var info))
                        _logger.LogInfo($"[Tree] Process gone: PID={kvp.Key} ({info.Name})");
                }
            }

            // Scan for new children
            ScanTree(_rootPid, depth: 0, quiet: true);
        }

        private void ScanTree(int pid, int depth, bool quiet = false)
        {
            if (depth > 10) return; // Safety limit

            try
            {
                var process = Process.GetProcessById(pid);
                if (!_tracked.ContainsKey(pid))
                {
                    var info = new ProcessInfo(pid, process.ProcessName,
                        SafeGetStartTime(process));
                    if (_tracked.TryAdd(pid, info) && !quiet)
                        _logger.LogInfo($"[Tree] Tracking PID={pid} '{process.ProcessName}' (depth={depth})");
                }
            }
            catch (Exception ex)
            {
                if (!quiet)
                    _logger.LogWarning($"[Tree] Cannot access PID={pid}: {ex.Message}");
                return;
            }

            // Find children via snapshot
            foreach (int childPid in GetChildPids(pid))
            {
                ScanTree(childPid, depth + 1, quiet);
            }
        }

        /// <summary>
        /// Gets direct child PIDs using ToolHelp32 snapshot (via NativeProcessHelper).
        /// </summary>
        private static IEnumerable<int> GetChildPids(int parentPid)
        {
            return NativeProcessHelper.GetChildPids(parentPid);
        }

        private static DateTime SafeGetStartTime(Process p)
        {
            try { return p.StartTime; }
            catch { return DateTime.MinValue; }
        }
    }

    public record ProcessInfo(int Pid, string Name, DateTime StartTime);
}
