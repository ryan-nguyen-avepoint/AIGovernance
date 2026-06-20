using ProcessFileMonitor.Core;
using ProcessFileMonitor.Etw;
using ProcessFileMonitor.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ProcessFileMonitor
{
    [SupportedOSPlatform("windows")]
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var logger = new AuditLogger("ProcessFileMonitor");
            logger.LogInfo($"Arguments: [{string.Join(", ", args)}]");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                logger.LogError("This tool requires Windows (ETW is Windows-only).");
                return 1;
            }
            if (!WindowsPrivilegeHelper.IsAdministrator())
            {
                logger.LogError("Administrator privileges are required to use ETW. Please run as Administrator.");
                return 1;
            }

            Process? launchedProcess = null;
            int? targetPid = null;
            string? targetName = null;
            try
            {
                var parsed = ArgumentParser.Parse(args);
                
                switch (parsed.Mode)
                {
                    case RunMode.LaunchClaude:
                        launchedProcess = LaunchHelper.LaunchClaude(logger);
                        if (launchedProcess == null) { logger.LogError("Failed to start Claude."); return 1; }
                        targetPid = launchedProcess.Id;
                        logger.LogInfo($"Launched Claude: PID={targetPid}");
                        break;

                    case RunMode.LaunchOpenclaw:
                        string? openclawCmd = null;
                        for (int i = 0; i < args.Length - 1; i++)
                            if (args[i].Equals("--openclaw-cmd", StringComparison.OrdinalIgnoreCase))
                                openclawCmd = args[i + 1];
                        launchedProcess = LaunchHelper.LaunchOpenclaw(logger, openclawCmd);
                        if (launchedProcess == null) { logger.LogError("Failed to start Openclaw."); return 1; }
                        targetPid = launchedProcess.Id;
                        logger.LogInfo($"Launched Openclaw: PID={targetPid}");
                        break;

                    case RunMode.AttachByPid:
                        targetPid = parsed.Pid;
                        break;

                    case RunMode.AttachByName:
                        targetName = parsed.ProcessName;
                        break;

                    default:
                        PrintUsage();
                        return 0;
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogError($"Argument error: {ex.Message}");
                PrintUsage();
                return 1;
            }

            // --- Resolve PID by name ---
            if (targetPid == null && targetName != null)
            {
                var procs = Process.GetProcessesByName(targetName);
                procs = procs.Where(p => p.Id != Environment.ProcessId).ToArray();
                if (procs.Length == 0)
                {
                    logger.LogError($"No process found with name '{targetName}'.");
                    return 1;
                }
                var first = procs.Where(p => p.Id != Environment.ProcessId).OrderByDescending(p => p.StartTime).First();
                targetPid = first.Id;
                logger.LogInfo($"Found process '{targetName}' -> PID={targetPid} (StartTime={first.StartTime:yyyy-MM-dd HH:mm:ss})");
            }
            if (targetPid == null)
            {
                logger.LogError("Could not determine target PID.");
                return 1;
            }

            // --- Build process tree ---
            var processTree = new ProcessTreeTracker(logger);
            processTree.Initialize(targetPid.Value);

            logger.LogInfo($"Monitoring PID={targetPid} and process tree: [{string.Join(", ", processTree.TrackedPids)}]");
            logger.LogInfo("Press Ctrl+C to stop monitoring.\n");

            // --- Start ETW session ---
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                logger.LogInfo("Shutdown requested (Ctrl+C)...");
                cts.Cancel();
            };

            // Background task: keep updating process tree with new children
            var monitor = new EtwFileMonitor(processTree, logger);
            var treeRefreshTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if(processTree.GetProcessNumber() <= 0)
                        {
                            cts.Cancel();
                            break;
                        }
                        processTree.RefreshChildren();
                        await Task.Delay(3000, cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"Tree refresh error: {ex.Message}");
                    }
                }
            }, cts.Token);

            try
            {
                await monitor.StartAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError($"ETW monitor error: {ex.Message}");
                logger.LogError(ex.StackTrace ?? "");
                return 1;
            }
            finally
            {
                logger.LogInfo("=== ProcessFileMonitor stopped ===");
                logger.Flush();
            }
            return 0;
        }

        static void PrintUsage()
        {
            Console.WriteLine("""
            ╔══════════════════════════════════════════════════════╗
            ║         ProcessFileMonitor - ETW File Auditor        ║
            ╚══════════════════════════════════════════════════════╝

            USAGE:
              ProcessFileMonitor claude          Launch Claude and monitor it
              ProcessFileMonitor openclaw        Launch Openclaw and monitor it
              ProcessFileMonitor -p <PID>        Attach to process by PID
              ProcessFileMonitor -c <name>       Attach to first process by name

            OPTIONS:
              --openclaw-cmd <subcommand>                 Openclaw subcommand (default: chat)

            EXAMPLES:
              ProcessFileMonitor claude
              ProcessFileMonitor -p 1234
              ProcessFileMonitor -c notepad
            """);
        }
    }
}
