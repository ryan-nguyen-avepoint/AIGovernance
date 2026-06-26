using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using ProcessFileMonitor.Logging;

namespace ProcessFileMonitor.Core
{
    [SupportedOSPlatform("windows")]
    public class ProcessTreeTracker
    {
        private readonly AuditLogger _logger;

        // rootPid → (pid → ProcessInfo)
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, ProcessInfo>> _trees = new();

        // pid → rootPid (reverse lookup, make sure no duplicate)
        private readonly ConcurrentDictionary<int, int> _pidToRoot = new();

        public IReadOnlyCollection<int> TrackedPids => _pidToRoot.Keys.ToList();

        public ProcessTreeTracker(AuditLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Add new processid OpenclawProcessMonitor.
        /// Auto scan child proccess, if PID already exists, then ignore
        /// </summary>
        public void AddRoot(int rootPid)
        {
            if (_pidToRoot.ContainsKey(rootPid))
            {
                _logger.LogInfo($"[Tree] PID={rootPid} already tracked under root={_pidToRoot[rootPid]}, skipping.");
                return;
            }

            // Create new tree for this root
            var tree = new ConcurrentDictionary<int, ProcessInfo>();
            if (!_trees.TryAdd(rootPid, tree))
            {
                // Race condition: tree is already added by another process
                _logger.LogInfo($"[Tree] Root PID={rootPid} already being added, skipping.");
                return;
            }

            _logger.LogInfo($"[Tree] Adding new root PID={rootPid}");
            ScanIntoTree(rootPid, rootPid, tree, depth: 0, quiet: false);
        }

        /// <summary>
        /// Delete PID from tree, delete child process too
        /// </summary>
        public void RemovePid(int pid)
        {
            if (!_pidToRoot.TryGetValue(pid, out int rootPid)) return;

            if (!_trees.TryGetValue(rootPid, out var tree)) return;

            // Collect all PID to delete
            var toRemove = new List<int>();
            CollectSubtree(pid, tree, toRemove);

            foreach (var p in toRemove)
            {
                tree.TryRemove(p, out var info);
                _pidToRoot.TryRemove(p, out _);
                _logger.LogInfo($"[Tree] Removed PID={p} ({info?.Name}) from tree root={rootPid}");
            }

            // If root is deleted, then remove the whole tree
            if (pid == rootPid)
            {
                _trees.TryRemove(rootPid, out _);
                _logger.LogInfo($"[Tree] Removed entire tree PID={pid}");
            }
        }
        private static void CollectSubtree(int pid, ConcurrentDictionary<int, ProcessInfo> tree, List<int> result)
        {
            result.Add(pid);
            foreach (int childPid in NativeProcessHelper.GetChildPids(pid))
            {
                if (tree.ContainsKey(childPid))
                    CollectSubtree(childPid, tree, result);
            }
        }
        public bool IsTracked(int pid) => _pidToRoot.ContainsKey(pid);

        public bool IsEmpty()
        {
            bool empty = _pidToRoot.IsEmpty;
            if (empty)
                _logger.LogInfo("[Tree] All process trees are empty. No openclaw processes running.");
            return empty;
        }

        /// <summary>
        /// Delete dead process, renew child process
        /// </summary>
        public void RefreshAll()
        {
            foreach (var (rootPid, tree) in _trees.ToArray())
            {
                if (IsProcessDead(rootPid))
                {
                    // Root is dead, remove whole tree
                    if (_trees.TryRemove(rootPid, out var deadTree))
                    {
                        foreach (var pid in deadTree.Keys)
                            _pidToRoot.TryRemove(pid, out _);
                        _logger.LogInfo($"[Tree] Root PID={rootPid} dead, entire tree removed.");
                    }
                    continue;
                }

                // Delete child process and child of child
                foreach (var pid in tree.Keys.ToArray())
                {
                    if (pid != rootPid && IsProcessDead(pid))
                        RemovePid(pid);
                }

                // Scan new pid
                ScanIntoTree(rootPid, rootPid, tree, depth: 0, quiet: true);
            }
        }

        private void ScanIntoTree(
            int pid,
            int rootPid,
            ConcurrentDictionary<int, ProcessInfo> tree,
            int depth,
            bool quiet)
        {
            if (depth > 10) return;

            // if pid belongs to other tree, then ignore
            if (_pidToRoot.TryGetValue(pid, out int existingRoot) && existingRoot != rootPid)
            {
                if (!quiet)
                    _logger.LogWarning($"[Tree] PID={pid} already belongs to root={existingRoot}, skipping.");
                return;
            }

            // newpid
            if (!tree.ContainsKey(pid))
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    var info = new ProcessInfo(pid, process.ProcessName, SafeGetStartTime(process));

                    if (tree.TryAdd(pid, info))
                    {
                        _pidToRoot[pid] = rootPid;
                        if (!quiet)
                            _logger.LogInfo($"[Tree] Tracking PID={pid} '{process.ProcessName}' root={rootPid} depth={depth}");
                    }
                }
                catch (Exception ex)
                {
                    if (!quiet)
                        _logger.LogWarning($"[Tree] Cannot access PID={pid}: {ex.Message}");
                    return;
                }
            }

            // Scan children
            foreach (int childPid in NativeProcessHelper.GetChildPids(pid))
                ScanIntoTree(childPid, rootPid, tree, depth + 1, quiet);
        }

        private static bool IsProcessDead(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                return p.HasExited;
            }
            catch { return true; }
        }

        private static DateTime SafeGetStartTime(Process p)
        {
            try { return p.StartTime; }
            catch { return DateTime.MinValue; }
        }
    }

    public record ProcessInfo(int Pid, string Name, DateTime StartTime);
}