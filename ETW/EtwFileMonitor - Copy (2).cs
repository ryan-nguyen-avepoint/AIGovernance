//using Microsoft.Diagnostics.Tracing;
//using Microsoft.Diagnostics.Tracing.Parsers;
//using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
//using Microsoft.Diagnostics.Tracing.Session;
//using ProcessFileMonitor.Core;
//using ProcessFileMonitor.Logging;
//using System.Collections.Concurrent;
//using System.Diagnostics.Tracing;
//using System.Runtime.Versioning;
//using System.Threading.Channels;

//namespace ProcessFileMonitor.Etw
//{
//    [SupportedOSPlatform("windows")]
//    public class EtwFileMonitor : IDisposable
//    {
//        private const string SessionName = "ProcessFileMonitor_ETW_Session";

//        private readonly ProcessTreeTracker _tree;
//        private readonly AuditLogger _logger;
//        private TraceEventSession? _session;
//        private FilePathFilter _fileFilter;
//        //private static ConcurrentQueue<FileEventDetails> _eventQueue = new ConcurrentQueue<FileEventDetails>();
//        //private static ConcurrentDictionary<string, DateTime> _logHistory = new ConcurrentDictionary<string, DateTime>();

//        public EtwFileMonitor(ProcessTreeTracker tree, AuditLogger logger)
//        {
//            _tree = tree;
//            _logger = logger;
//            _fileFilter = new FilePathFilter(logger);
//            _eventChannel = Channel.CreateBounded<T>(new BoundedChannelOptions(eventChannelCapacity)
//            {
//                FullMode = BoundedChannelFullMode.DropOldest, // drop event cũ, ưu tiên mới
//                SingleReader = true,   // chỉ Batcher đọc
//                SingleWriter = false,  // nhiều producer có thể ghi đồng thời
//            });
//        }
//        public enum ActionType
//        {
//            CREATE, READ, WRITE, DELETE, CLOSE, RENAME
//        }
//        public Task StartAsync(CancellationToken ct)
//        {
//            return Task.Run(() => RunSession(ct), ct);
//        }
//        private void RunSession(CancellationToken ct)
//        {
//            TraceEventSession.GetActiveSession(SessionName)?.Dispose(); // Dispose any stale session with same name
//            _logger.LogInfo("[ETW] Creating kernel trace session...");
//            using var _session = new TraceEventSession(SessionName)
//            {
//                StopOnDispose = true
//            };
//            _session.EnableKernelProvider(
//                KernelTraceEventParser.Keywords.FileIOInit |
//                KernelTraceEventParser.Keywords.FileIO |
//                KernelTraceEventParser.Keywords.DiskFileIO |
//                KernelTraceEventParser.Keywords.DiskIO |
//                KernelTraceEventParser.Keywords.Process
//            );
//            var parser = _session.Source.Kernel;

//            void OnEventReceived(FileEventDetails e, int createOptions = 0)
//            {
//                if (
//                    !_tree.IsTracked(e.ProcessID) ||
//                    e.ProcessID == Environment.ProcessId ||
//                    !_fileFilter.QuickValidateFile(e.FileName)
//                ) return;
//                if (e.Action == ActionType.CREATE)
//                {
//                    if ((createOptions & 0x00000001) != 0) // Ignore folder
//                    {
//                        return;
//                    }
//                    if ((createOptions & 0x00200000) != 0) // Only for link/junction/reparse point
//                    {
//                        return;
//                    }
//                    if ((createOptions & 0x1000) != 0) // OPEN TO DELETE
//                    {
//                        e.Action = ActionType.DELETE;
//                    }
//                }
//                _eventQueue.Enqueue(e);
//            }
//            parser.FileIOCreate += e => OnEventReceived(new FileEventDetails
//            {
//                FileName = e.FileName,
//                ProcessID = e.ProcessID,
//                ProcessName = e.ProcessName,
//                TimeStamp = e.TimeStamp,
//                FileObject = e.FileObject,
//                Action = ActionType.CREATE
//            }, (int)e.CreateOptions);
//            parser.FileIORead += e => OnEventReceived(new FileEventDetails
//            {
//                FileName = e.FileName,
//                ProcessID = e.ProcessID,
//                ProcessName = e.ProcessName,
//                TimeStamp = e.TimeStamp,
//                FileObject = e.FileObject,
//                Action = ActionType.READ
//            });
//            parser.FileIOWrite += e => OnEventReceived(new FileEventDetails
//            {
//                FileName = e.FileName,
//                ProcessID = e.ProcessID,
//                ProcessName = e.ProcessName,
//                TimeStamp = e.TimeStamp,
//                FileObject = e.FileObject,
//                Action = ActionType.WRITE
//            });
//            parser.FileIODelete += e => OnEventReceived(new FileEventDetails
//            {
//                FileName = e.FileName,
//                ProcessID = e.ProcessID,
//                ProcessName = e.ProcessName,
//                TimeStamp = e.TimeStamp,
//                FileObject = e.FileObject,
//                Action = ActionType.DELETE
//            });
//            parser.FileIOClose += e => OnEventReceived(new FileEventDetails
//            {
//                FileName = e.FileName,
//                ProcessID = e.ProcessID,
//                ProcessName = e.ProcessName,
//                TimeStamp = e.TimeStamp,
//                FileObject = e.FileObject,
//                Action = ActionType.CLOSE
//            });
//            parser.FileIORename += e => {
//                OnEventReceived(new FileEventDetails
//                {
//                    FileName = e.FileName,
//                    ProcessID = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    TimeStamp = e.TimeStamp,
//                    FileObject = e.FileObject,
//                    Action = ActionType.RENAME
//                });
//            };

//            parser.ProcessStart += e =>
//            {
//                if (_tree.IsTracked(e.ParentID))
//                {
//                    _logger.LogInfo($"[PROCESS] New child process spawned PID={e.ProcessID} by ParentPID={e.ParentID} ProcessName={e.ImageFileName} CMD={e.CommandLine}");
//                    _tree.RefreshChildren();
//                }
//            };
//            parser.ProcessStop += e =>
//            {
//                if (_tree.IsTracked(e.ProcessID))
//                {
//                    _logger.LogInfo($"[PROCESS] Tracked process exited PID={e.ProcessID} ParentPID={e.ParentID} ProcessName={e.ProcessName} ExitCode={e.ExitStatus}");
//                }
//            };
//            ct.Register(() =>
//            {
//                _logger.LogInfo("[ETW] Stopping session...");
//                _session?.Stop();
//                TraceEventSession.GetActiveSession(SessionName)?.Dispose();
//            });
//            _logger.LogInfo("[ETW] Session started. Listening for file system events...");
//            _logger.LogInfo(new string('─', 80));

//            // Blocking call - processes events until session is stopped
//            Task.Run(ProcessQueueSequential);
//            _session.Source.Process();
//            _logger.LogInfo("[ETW] Session ended.");
//        }

//        private void ProcessQueueSequential()
//        {
//            DateTime lastTimeHandled = DateTime.Now;
//            while (true)
//            {
//                if (_eventQueue.TryDequeue(out var data))
//                {

//                }
//                else
//                {
//                    Task.Delay(1000).Wait();
//                }
//            }
//        }

//        //private bool ExistInCache(string uniqueKey)
//        //{
//        //    var policy = new CacheItemPolicy
//        //    {
//        //        SlidingExpiration = TimeSpan.FromSeconds(10)
//        //    };
//        //    if (_eventCache.Contains(uniqueKey))
//        //    {
//        //        var existingKey = (_eventCache.Get(uniqueKey) as int?) ?? 0 + 1;
//        //        _eventCache.Set(uniqueKey, existingKey, policy);
//        //        return true;
//        //    }
//        //    _eventCache.Set(uniqueKey, 1, policy);
//        //    return false;
//        //}
//        private void OnFileCreate(FileEventDetails e)
//        {
//            var operation = "CREATE/OPEN";
//            try
//            {
//                if (!_fileFilter.IsValidFile(e.FileName)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }
//        private void OnFileCommonAction(FileEventDetails e)
//        {
//            var operation = e.Action.ToString();
//            try
//            {
//                if (!_fileFilter.IsValidFile(e.FileName)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }

//        private void OnFileDelete(FileEventDetails e)
//        {
//            var operation = "DELETE";
//            try
//            {
//                if (!_fileFilter.IsValidFile(e.FileName, false)) return;

//                var isDeleted = true;
//                try
//                {
//                    if (File.Exists(e.FileName)) isDeleted = false;
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogDebug($"[WARN]Can not get file {e.FileName} -- {ex}");
//                    isDeleted = false;
//                }

//                if (isDeleted)
//                {
//                    _logger.LogFileEvent(new FileAuditEvent
//                    {
//                        Timestamp = e.TimeStamp,
//                        Operation = operation,
//                        Pid = e.ProcessID,
//                        ProcessName = e.ProcessName,
//                        FileName = e.FileName,
//                    });
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }

//        public void Dispose()
//        {
//            _session?.Dispose();
//        }

//        private class FileEventDetails
//        {
//            public string FileName { get; set; } = "";
//            public int ProcessID { get; set; }
//            public string ProcessName { get; set; } = "";
//            public DateTime TimeStamp { get; set; }
//            public UInt64 FileObject { get; set; }
//            public ActionType Action { get; set; }
//        }
//    }
//}
