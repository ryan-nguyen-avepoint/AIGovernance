using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Caching.Memory;
using ProcessFileMonitor.Core;
using ProcessFileMonitor.Logging;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml.Linq;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ProcessFileMonitor.Etw
{
    [SupportedOSPlatform("windows")]
    public class EtwFileMonitor : IDisposable
    {
        private const string SessionName = "ProcessFileMonitor_ETW_Session";

        private readonly ProcessTreeTracker _tree;
        private readonly AuditLogger _logger;
        private TraceEventSession? _session;
        private FilePathFilter _fileFilter;
        private readonly Channel<FileEventDetails> _eventChannel;
        private readonly ConcurrentDictionary<string, List<FileEventDetails>> _allEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<FileEventDetails> _newEventsQueue = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTimeEvents = new();
        private MemoryCache _eventCache = new(new MemoryCacheOptions());
        private MemoryCache _lastActionCache = new(new MemoryCacheOptions());

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
            TraceEventSession.GetActiveSession(SessionName)?.Dispose(); // Dispose any stale session with same name
            _logger.LogInfo("[ETW] Creating kernel trace session...");
            using var _session = new TraceEventSession(SessionName)
            {
                StopOnDispose = true
            };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.FileIO |
                KernelTraceEventParser.Keywords.DiskFileIO |
                KernelTraceEventParser.Keywords.DiskIO |
                KernelTraceEventParser.Keywords.Process
                //KernelTraceEventParser.Keywords.NetworkTCPIP
            );
            var parser = _session.Source.Kernel;
            parser.FileIOCreate += e => OnEventReceived(new FileEventDetails
            {
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                TimeStamp = e.TimeStamp,
                Action = ActionType.CREATE
            }, (int)e.CreateOptions);
            parser.FileIORead += e => OnEventReceived(new FileEventDetails
            {
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                TimeStamp = e.TimeStamp,
                Action = ActionType.READ
            });
            parser.FileIOWrite += e => OnEventReceived(new FileEventDetails
            {
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                TimeStamp = e.TimeStamp,
                Action = ActionType.WRITE,
            });
            parser.FileIODelete += e => OnEventReceived(new FileEventDetails
            {
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                TimeStamp = e.TimeStamp,
                Action = ActionType.DELETE
            });
            parser.FileIOClose += e => OnEventReceived(new FileEventDetails
            {
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                TimeStamp = e.TimeStamp,
                Action = ActionType.CLOSE
            });
            parser.FileIORename += e => OnEventReceived(new FileEventDetails
            {
                FileName = e.FileName.ToLower(),
                ProcessID = e.ProcessID,
                ProcessName = e.ProcessName,
                TimeStamp = e.TimeStamp,
                Action = ActionType.RENAME
            });
            //parser.TcpIpSend += e => OnEventReceived(new NetworkEventDetails
            //{
            //    ProcessID = e.ProcessID,
            //    ProcessName = e.ProcessName,
            //    TimeStamp = e.TimeStamp,
            //    Protocol = "TCP",
            //    Action = NetActionType.SEND,
            //    saddr = e.saddr.ToString(),
            //    daddr = e.daddr.ToString(),
            //    dport = e.dport,
            //    sport = e.sport,
            //    size = e.size
            //});

            //parser.TcpIpRecv += e => OnEventReceived(new NetworkEventDetails
            //{
            //    ProcessID = e.ProcessID,
            //    ProcessName = e.ProcessName,
            //    TimeStamp = e.TimeStamp,
            //    Protocol = "TCP",
            //    Action = NetActionType.RECV,
            //    saddr = e.saddr.ToString(),
            //    daddr = e.daddr.ToString(),
            //    dport = e.dport,
            //    sport = e.sport,
            //    size = e.size
            //});
            //parser.UdpIpSend += e => OnEventReceived(new NetworkEventDetails
            //{
            //    ProcessID = e.ProcessID,
            //    ProcessName = e.ProcessName,
            //    TimeStamp = e.TimeStamp,
            //    Protocol = "UDP",
            //    Action = NetActionType.SEND,
            //    saddr = e.saddr.ToString(),
            //    daddr = e.daddr.ToString(),
            //    dport = e.dport,
            //    sport = e.sport,
            //    size = e.size
            //});

            //parser.UdpIpRecv += e => OnEventReceived(new NetworkEventDetails
            //{
            //    ProcessID = e.ProcessID,
            //    ProcessName = e.ProcessName,
            //    TimeStamp = e.TimeStamp,
            //    Protocol = "UDP",
            //    Action = NetActionType.RECV,
            //    saddr = e.saddr.ToString(),
            //    daddr = e.daddr.ToString(),
            //    dport = e.dport,
            //    sport = e.sport,
            //    size = e.size
            //});

            parser.ProcessStart += e =>
            {
                if (_tree.IsTracked(e.ParentID))
                {
                    _logger.LogInfo($"[PROCESS] New child process spawned PID={e.ProcessID} by ParentPID={e.ParentID} ProcessName={e.ImageFileName} CMD={e.CommandLine}");
                    _tree.RefreshChildren();
                }
            };
            parser.ProcessStop += e =>
            {
                if (_tree.IsTracked(e.ProcessID))
                {
                    _logger.LogInfo($"[PROCESS] Tracked process exited PID={e.ProcessID} ParentPID={e.ParentID} ProcessName={e.ProcessName} ExitCode={e.ExitStatus}");
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
            _session.Source.Process(); // Blocking call - processes events until session is stopped
            _logger.LogInfo("[ETW] Session ended.");
        }
        private void OnEventReceived(FileEventDetails e, int createOptions = 0)
        {
            if (
                !_tree.IsTracked(e.ProcessID) ||
                e.ProcessID == Environment.ProcessId ||
                !_fileFilter.QuickValidateFile(e.FileName)
            ) return;
            if (e.Action == ActionType.CREATE)
            {
                if ((createOptions & 0x00000001) != 0) // Ignore folder
                {
                    return;
                }
                if ((createOptions & 0x00200000) != 0) // Only for link/junction/reparse point
                {
                    return;
                }
                if ((createOptions & 0x1000) != 0) // OPEN TO DELETE
                {
                    e.Action = ActionType.DELETE;
                }
            }
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
                            if (targetEvent.Action == ActionType.RENAME)
                            {
                                targetEvent = new FileRenameEventDetails
                                {
                                    FileName = targetEvent.FileName,
                                    ProcessID = targetEvent.ProcessID,
                                    ProcessName = targetEvent.ProcessName,
                                    TimeStamp = targetEvent.TimeStamp,
                                    Action = targetEvent.Action,
                                    NewFileName = null
                                };
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
                                (e as FileRenameEventDetails)!.NewFileName = firstCloseEvent.FileName;
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
                                case ActionType.CLOSE:
                                    OnFileCommonAction(e);
                                    break;
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
            // Get from batch and output one result + merge with list + check time + timeok then push all + save 2 last records + reset time
            // Dk thoa man: 2 event tu cung 1 process va action se k push trong ktg ngan + 1 file chi cung 1 sk trong 20s, hoac khi queue trong qua 5s tuc la khi do ta reset expire time + neu 2 lan lien tiep cung 1 event thi lay event t2 khac voi event vua gui, neu la open thi bo qua + lan dau tien thi giam 20s xuong con 10s + lan dau cung in luon
            // RENAME > DELETE > UPDATE > READ > CREATE/OPEN
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
                var renameEvent = (e as FileRenameEventDetails)!;
                if (!_fileFilter.IsValidFile(renameEvent.FileName, false)) return;

                var auditEvent = new FileAuditEvent
                {
                    Timestamp = e.TimeStamp,
                    Operation = operation,
                    Pid = e.ProcessID,
                    ProcessName = e.ProcessName,
                    FileName = e.FileName,
                };
                if (!string.IsNullOrEmpty(renameEvent.NewFileName))
                {
                    auditEvent.ExtraInfo = $"NewFileName={renameEvent?.NewFileName}";
                }
                _logger.LogFileEvent(auditEvent);
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

        private class FileEventDetails
        {
            public string FileName { get; set; } = "";
            public int ProcessID { get; set; }
            public string ProcessName { get; set; } = "";
            public DateTime TimeStamp { get; set; }
            public ActionType Action { get; set; }
        }

        private class FileRenameEventDetails: FileEventDetails
        {
            public string? NewFileName { get; set; } = "";
        }
    }
}
