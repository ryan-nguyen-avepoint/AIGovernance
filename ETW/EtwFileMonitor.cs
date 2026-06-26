using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Caching.Memory;
using ProcessFileMonitor.Core;
using ProcessFileMonitor.Logging;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;

namespace ProcessFileMonitor.Etw
{
    [SupportedOSPlatform("windows")]
    public class EtwFileMonitor : IDisposable
    {
        private const string SessionName = "ProcessFileMonitor_ETW_Session";

        private readonly ProcessTreeTracker _tree;
        private readonly AuditLogger _logger;
        private readonly TraceEventSession _session;
        private FilePathFilter _fileFilter;
        private readonly Channel<FileEventDetails> _eventChannel;
        private readonly ConcurrentDictionary<string, List<FileEventDetails>> _allEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<FileEventDetails> _newEventsQueue = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTimeEvents = new();
        private MemoryCache _eventCache = new(new MemoryCacheOptions());
        private MemoryCache _lastActionCache = new(new MemoryCacheOptions());
        private readonly NdjsonFileWriter _writer;

        public EtwFileMonitor(ProcessTreeTracker tree, AuditLogger logger)
        {
            _tree = tree;
            _logger = logger;
            _fileFilter = new FilePathFilter(logger);
            _eventChannel = Channel.CreateBounded<FileEventDetails>(new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true
            };
            _writer = new NdjsonFileWriter("events.ndjson");
        }
        public enum ActionType
        {
            CLOSE, CREATE, READ, WRITE, RENAME, DELETE // Sort by priority
        }
        public async Task StartAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var collectBatchTask = Task.Run(async () => await CollectBatchEvent(cts.Token), cts.Token);
            var _ = Task.Run(async () => await ProcessEvent(ct));
            try
            {
                RunSession(ct);
            }
            finally
            {
                cts.Cancel();
            }
            await collectBatchTask;
        }
        private void RunSession(CancellationToken ct)
        {
            if (TraceEventSession.GetActiveSession(SessionName) != null)
                TraceEventSession.GetActiveSession(SessionName)?.Stop();
            TraceEventSession.GetActiveSession(SessionName)?.Dispose(); // Dispose any stale session with same name
            _logger.LogInfo("[ETW] Creating kernel trace session...");
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.DiskFileIO |
                KernelTraceEventParser.Keywords.Process
            );
            var parser = _session.Source.Kernel;
            //parser.FileIOCreate += e => OnEventReceived(new FileEventDetails
            //{
            //    ProviderName = e.ProviderName,
            //    OpcodeName = e.OpcodeName,
            //    TimeStamp = e.TimeStamp,
            //    FileName = e.FileName.ToLower(),
            //    ProcessID = e.ProcessID,
            //    ProcessName = e.ProcessName,
            //    ThreadID = e.ThreadID,
            //    CreateOptions = e.CreateOptions,
            //    CreateDisposition = e.CreateDisposition,
            //    FileAttributes = e.FileAttributes,
            //    ShareAccess = e.ShareAccess,
            //    Action = ActionType.CREATE
            //});
            parser.FileIORead += e => OnEventReceived(new FileEventDetails
            {
                ProviderName = e.ProviderName,
                OpcodeName = e.OpcodeName,
                TimeStamp = e.TimeStamp,
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                ThreadID = e.ThreadID,
                IoSize = e.IoSize,
                Action = ActionType.READ
            });
            parser.FileIOWrite += e => OnEventReceived(new FileEventDetails
            {
                ProviderName = e.ProviderName,
                OpcodeName = e.OpcodeName,
                TimeStamp = e.TimeStamp,
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                ThreadID = e.ThreadID,
                IoSize = e.IoSize,
                Action = ActionType.WRITE,
            });
            parser.FileIODelete += e => OnEventReceived(new FileEventDetails
            {
                ProviderName = e.ProviderName,
                OpcodeName = e.OpcodeName,
                TimeStamp = e.TimeStamp,
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                ThreadID = e.ThreadID,
                InfoClass = e.InfoClass,
                Action = ActionType.DELETE
            });
            parser.FileIOClose += e => OnEventReceived(new FileEventDetails
            {
                ProviderName = e.ProviderName,
                OpcodeName = e.OpcodeName,
                TimeStamp = e.TimeStamp,
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                ThreadID = e.ThreadID,
                Action = ActionType.CLOSE
            });
            parser.FileIORename += e => OnEventReceived(new FileEventDetails
            {
                ProviderName = e.ProviderName,
                OpcodeName = e.OpcodeName,
                TimeStamp = e.TimeStamp,
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                ThreadID = e.ThreadID,
                InfoClass = e.InfoClass,
                Action = ActionType.RENAME
            });

            parser.ProcessStart += e =>
            {
                try
                {
                    bool selfMatch = OpenclawProcessMonitor.IsOpenclaw(e.CommandLine) || OpenclawProcessMonitor.IsOpenclaw(e.ImageFileName);
                    if (!selfMatch) return;
                    string parentCmd = OpenclawProcessMonitor.GetCommandLine(e.ParentID);
                    if (!OpenclawProcessMonitor.IsOpenclaw(parentCmd)) return;

                    OpenclawProcessMonitor.TrackedPids[e.ProcessID] = e.CommandLine;
                    _tree.AddRoot(e.ProcessID);
                    _logger.LogInfo($"[PROCESS] New child process spawned PID={e.ProcessID} by ParentPID={e.ParentID} ProcessName={e.ImageFileName} CMD={e.CommandLine}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[PROCESS] Error processing start event for PID={e.ProcessID}: {ex.Message}");
                }
            };

            parser.ProcessStop += e =>
            {
                try
                {
                    if (_tree.IsTracked(e.ProcessID))
                    {
                        _logger.LogInfo($"[PROCESS] Tracked process exited PID={e.ProcessID} ParentPID={e.ParentID} ProcessName={e.ProcessName} ExitCode={e.ExitStatus}");
                        if (OpenclawProcessMonitor.TrackedPids.TryRemove(e.ProcessID, out _))
                        {
                            _logger.LogInfo($"This PID is openclaw process");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[PROCESS] Error processing stop event for PID={e.ProcessID}: {ex.Message}");
                }
            };
            ct.Register(() =>
            {
                _logger.LogInfo("[ETW] Stopping session...");
                _session?.Stop();
                TraceEventSession.GetActiveSession(SessionName)?.Dispose();
            });
            _logger.LogInfo("[ETW] Session started. Listening for file system events...");
            _logger.LogInfo(new string('─', 80));
            _session.Source.Process();
            _logger.LogInfo("[ETW] Session ended.");
        }
        private void OnEventReceived(FileEventDetails e)
        {
            if(e.FileName.Contains("test2.txt"))
            {

            }
            if (
                !_tree.IsTracked(e.ProcessID) ||
                e.ProcessID == Environment.ProcessId ||
                !_fileFilter.QuickValidateFile(e.FileName)
            ) return;
            e.FileName = NormalizePath(e.FileName);
            if (e.Action == ActionType.CREATE)
            {
                if (((int)e.CreateOptions! & 0x00000001) != 0) // Ignore folder
                {
                    return;
                }
                if (((int)e.CreateOptions! & 0x00200000) != 0) // Only for link/junction/reparse point
                {
                    return;
                }
                if (((int)e.CreateOptions! & 0x1000) != 0) // OPEN TO DELETE
                {
                    e.Action = ActionType.DELETE;
                }
            }
            (var parentPid, var rootPid) = _tree.GetParentAndRoot(e.ProcessID);
            e.ParentProcessId = parentPid;
            e.RootProcessId = rootPid;
            if (!_eventChannel.Writer.TryWrite(e))
            {
                _logger.LogWarning($"[ETW] Cannot write event {e.Action} to event channel. Check if channel is full");
            }
        }

        private async Task CollectBatchEvent(CancellationToken ct)
        {
            var eventReader = _eventChannel.Reader;
            try
            {
                while (!ct.IsCancellationRequested && await eventReader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    var batch = new List<FileEventDetails>(100);
                    var deadline = DateTime.UtcNow.Add(TimeSpan.FromMilliseconds(1500));
                    while (batch.Count < 100)
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) break;
                        if (eventReader.TryRead(out var item))
                        {
                            batch.Add(item);
                            continue;
                        }
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(remaining);
                        try
                        {
                            if (!await eventReader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false)) break;
                        }
                        catch (OperationCanceledException)
                        {
                            break; // Finish 2s
                        }
                    }
                    if (batch.Count == 0) continue;
                    try
                    {
                        var newEvents = batch.GroupBy(b => b.FileName).Select(g =>
                        {
                            var sortedGroup = g.OrderByDescending(e => e.TimeStamp).ToList();
                            FileEventDetails targetEvent = sortedGroup.First();
                            foreach (var sg in sortedGroup)
                            {
                                if (sg.Action > targetEvent.Action)
                                {
                                    targetEvent = sg;
                                }
                            }
                            return targetEvent;
                        }).ToList();
                        var renameEvent = newEvents.Where(n => n.Action == ActionType.RENAME);
                        if (renameEvent.SingleOrDefault() is { } e)
                        {
                            var firstCloseEvent = newEvents.FirstOrDefault(n =>
                                n.TimeStamp > e.TimeStamp &&
                                n.Action == ActionType.CLOSE &&
                                Path.GetExtension(e.FileName) == Path.GetExtension(n.FileName) &&
                                Path.GetDirectoryName(e.FileName) == Path.GetDirectoryName(n.FileName)
                            );
                            if (firstCloseEvent != null)
                            {
                                e.NewFileName = firstCloseEvent.FileName;
                                newEvents.Remove(firstCloseEvent);
                            }
                        }
                        newEvents.ForEach(_newEventsQueue.Enqueue);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Processor] Failed to handle {batch.Count} event: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task ProcessEvent(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var listEventToTrigger = new List<FileEventDetails>();
                    try
                    {
                        var batch = new List<FileEventDetails>(20);
                        while (batch.Count < 20 && _newEventsQueue.TryDequeue(out var e))
                        {
                            if (!_eventCache.TryGetValue(e.FileName, out var _) && !_allEvents.TryGetValue(e.FileName, out var _))
                            {
                                listEventToTrigger.Add(e);
                            }
                            else if (_lastActionCache.TryGetValue<ActionType>(e.FileName, out var lastEvent) && lastEvent < e.Action)
                            {
                                _allEvents.TryRemove(e.FileName, out var _);
                                _lastTimeEvents.TryRemove(e.FileName, out var _);
                                listEventToTrigger.Add(e);
                            }
                            else if (_allEvents.TryGetValue(e.FileName, out var listEvent)) listEvent.Add(e);
                            else
                            {
                                _lastTimeEvents[e.FileName] = DateTime.UtcNow.Add(TimeSpan.FromSeconds(20));
                                _allEvents[e.FileName] = [e];
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ProcessEvent] Failed to get event from queue event: {ex.Message}");
                        _newEventsQueue.Clear();
                    }
                    try
                    {
                        var listEventIdToTrigger = new List<string>();
                        var now = DateTime.UtcNow;
                        var expiredKeys = _lastTimeEvents.Where(x => x.Value < now)
                            .Select(x => _lastTimeEvents.TryRemove(x.Key, out _) ? x.Key : null)
                            .OfType<string>().ToList();
                        listEventIdToTrigger.AddRange(expiredKeys);
                        foreach (var id in listEventIdToTrigger)
                        {
                            if (_allEvents.TryRemove(id, out var events))
                            {
                                var sortedEvents = events.OrderByDescending(e => e.TimeStamp).ToList();
                                if (_lastActionCache.TryGetValue<ActionType>(id, out var action))
                                {
                                    sortedEvents = sortedEvents.Where(s => s.Action != action && s.Action != ActionType.CREATE && s.Action != ActionType.CLOSE).ToList();

                                }
                                if (sortedEvents.Count <= 0)
                                {
                                    break;
                                }
                                FileEventDetails targetEvent = sortedEvents.First();
                                foreach (var g in sortedEvents)
                                {
                                    if (g.Action > targetEvent.Action)
                                    {
                                        targetEvent = g;
                                    }
                                }
                                listEventToTrigger.Add(targetEvent);
                            }
                        }
                        foreach (var e in listEventToTrigger)
                        {
                            _eventCache.Set(e.FileName, true, TimeSpan.FromSeconds(30));
                            _lastActionCache.Set(e.FileName, e.Action, TimeSpan.FromSeconds(60));
                            switch (e.Action)
                            {
                                case ActionType.CREATE:
                                    OnFileCreate(e);
                                    break;
                                case ActionType.READ:
                                    OnFileCommonAction(e);
                                    break;
                                case ActionType.DELETE:
                                    OnFileDelete(e);
                                    break;
                                case ActionType.WRITE:
                                    OnFileCommonAction(e);
                                    break;
                                //case ActionType.CLOSE:
                                //    OnFileCommonAction(e);
                                //    break;
                                case ActionType.RENAME:
                                    OnRenameFileAction(e);
                                    break;
                            }
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ProcessEvent] Failed to handle {listEventToTrigger.Count} event: {ex.Message}");
                        _lastTimeEvents.Clear();
                        _allEvents.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        private void OnFileCreate(FileEventDetails e)
        {
            var operation = "CREATE/OPEN";
            try
            {
                if (!_fileFilter.IsValidFile(e.FileName)) return;

                _logger.LogFileEvent(new FileAuditEvent
                {
                    Timestamp = e.TimeStamp,
                    Operation = operation,
                    Pid = e.ProcessID,
                    ProcessName = e.ProcessName,
                    FileName = e.FileName,
                });
                _writer.Write(JsonSerializer.Serialize(e));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
            }
        }
        private void OnRenameFileAction(FileEventDetails e)
        {
            var operation = e.Action.ToString();
            try
            {
                if (!_fileFilter.IsValidFile(e.FileName, false)) return;

                var auditEvent = new FileAuditEvent
                {
                    Timestamp = e.TimeStamp,
                    Operation = operation,
                    Pid = e.ProcessID,
                    ProcessName = e.ProcessName,
                    FileName = e.FileName,
                };
                if (!string.IsNullOrEmpty(e.NewFileName))
                {
                    auditEvent.ExtraInfo = $"NewFileName={e?.NewFileName}";
                }
                _logger.LogFileEvent(auditEvent);
                _writer.Write(JsonSerializer.Serialize(e));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
            }
        }
        private void OnFileCommonAction(FileEventDetails e)
        {
            var operation = e.Action.ToString();
            try
            {
                if (!_fileFilter.IsValidFile(e.FileName)) return;

                _logger.LogFileEvent(new FileAuditEvent
                {
                    Timestamp = e.TimeStamp,
                    Operation = operation,
                    Pid = e.ProcessID,
                    ProcessName = e.ProcessName,
                    FileName = e.FileName,
                });
                _writer.Write(JsonSerializer.Serialize(e));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
            }
        }

        private void OnFileDelete(FileEventDetails e)
        {
            var operation = "DELETE";
            try
            {
                if (!_fileFilter.IsValidFile(e.FileName, false)) return;

                var isDeleted = true;
                try
                {
                    if (File.Exists(e.FileName)) isDeleted = false;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"[WARN]Can not get file {e.FileName} -- {ex}");
                    isDeleted = false;
                }

                if (isDeleted)
                {
                    _logger.LogFileEvent(new FileAuditEvent
                    {
                        Timestamp = e.TimeStamp,
                        Operation = operation,
                        Pid = e.ProcessID,
                        ProcessName = e.ProcessName,
                        FileName = e.FileName,
                    });
                    _writer.Write(JsonSerializer.Serialize(e));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
            }
        }
        public void Dispose()
        {
            _session?.Dispose();
            _eventChannel.Writer.TryComplete();
        }
        public string NormalizePath(string path)
        {
            try
            {
                return new FileInfo(path).FullName;
            }
            catch
            {
                return path;
            }
        }

        private class FileEventDetails : BaseFileEventDetails
        {
            // Rename
            public string? NewFileName { get; set; } = null;

            // Create/Open
            public CreateOptions? CreateOptions { get; set; }
            public CreateDisposition? CreateDisposition { get; set; }
            public FileAttributes? FileAttributes { get; set; }
            public FileShare? ShareAccess { get; set; }

            // Read, Write
            public Int32? IoSize { get; set; }

            // Rename, Delete
            public Int32? InfoClass { get; set; }
        }
        private class BaseFileEventDetails
        {
            public String ProviderName { get; set; } = string.Empty;
            public String OpcodeName { get; set; } = string.Empty;
            public DateTime TimeStamp { get; set; } = DateTime.Now;
            public string FileName { get; set; } = string.Empty;
            public int ProcessID { get; set; }
            public string ProcessName { get; set; } = string.Empty;
            public Int32 ThreadID { get; set; }
            public int? ParentProcessId { get; set; }
            public int? RootProcessId { get; set; }
            public ActionType Action { get; set; }
        }
    }
}
