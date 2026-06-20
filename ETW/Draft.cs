//using Microsoft.Diagnostics.Tracing;
//using Microsoft.Diagnostics.Tracing.Parsers;
//using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
//using Microsoft.Diagnostics.Tracing.Session;
//using ProcessFileMonitor.Core;
//using ProcessFileMonitor.Logging;
//using System.Data.Common;
//using System.Runtime.Caching;
//using System.Runtime.Versioning;
//using System.Security.Cryptography;
//using System.Text.RegularExpressions;
//using static System.Runtime.InteropServices.JavaScript.JSType;

//namespace ProcessFileMonitor.Etw
//{
//    [SupportedOSPlatform("windows")]
//    public class EtwFileMonitor : IDisposable
//    {
//        private const string SessionName = "ProcessFileMonitor_ETW_Session";

//        private readonly ProcessTreeTracker _tree;
//        private readonly AuditLogger _logger;
//        private TraceEventSession? _session;
//        private static MemoryCache _eventCache = MemoryCache.Default;

//        private const string KernelSessionName = "AIMonitor_KernelSession";
//        private const string FileSessionName = "AIMonitor_KernelFileSession";

//        // Microsoft-Windows-Kernel-File provider GUID
//        private static readonly Guid KernelFileProviderGuid =
//            new("EDD08927-9CC4-4E65-B970-C2560FB5C289");

//        // ── IRP CreateDisposition values (from wdm.h) ────────────────────────────
//        // We only want events that write/create, not plain opens.
//        private const uint FILE_SUPERSEDE = 0; // replace existing
//        private const uint FILE_CREATE = 2; // create new, fail if exists
//        private const uint FILE_OVERWRITE = 4; // open+overwrite, fail if not exists
//        private const uint FILE_OVERWRITE_IF = 5; // open+overwrite or create

//        // Microsoft-Windows-Kernel-File Event IDs
//        private static readonly TraceEventID EventId_Create = (TraceEventID)12;
//        private static readonly TraceEventID EventId_NameCreate = (TraceEventID)10;
//        private static readonly TraceEventID EventId_NameDelete = (TraceEventID)11;
//        private static readonly TraceEventID EventId_SetDelete = (TraceEventID)30;

//        private TraceEventSession? _kernelSession;
//        private TraceEventSession? _fileSession;
//        private volatile bool _disposed;

//        public EtwFileMonitor(ProcessTreeTracker tree, AuditLogger logger)
//        {
//            _tree = tree;
//            _logger = logger;
//        }

//        public Task StartAsync(CancellationToken ct)
//        {
//            return Task.Run(() => RunSession(ct), ct);
//        }
//        private void RunSession(CancellationToken ct)
//        {
//            //TraceEventSession.GetActiveSession(KernelSessionName)?.Dispose();

//            //_kernelSession = new TraceEventSession(KernelSessionName) { StopOnDispose = true };

//            //// FileIOInit kept ONLY for Rename — Create/Delete handled by provider #2
//            //_kernelSession.EnableKernelProvider(
//            //    KernelTraceEventParser.Keywords.FileIO |
//            //    KernelTraceEventParser.Keywords.Process |
//            //    KernelTraceEventParser.Keywords.NetworkTCPIP |
//            //    KernelTraceEventParser.Keywords.FileIOInit    // needed for FileIORename
//            //);

//            //StartProcessingThread(_kernelSession, "ETW-Kernel");


//            TraceEventSession.GetActiveSession(FileSessionName)?.Dispose();

//            _fileSession = new TraceEventSession(FileSessionName) { StopOnDispose = true };

//            // Enable all keywords (0xFFFFFFFF) — we filter in the callback
//            _fileSession.EnableProvider(KernelFileProviderGuid, TraceEventLevel.Verbose, 0xFFFFFFFF);

//            SubscribeKernelFileEvents();

//            StartProcessingThread(_fileSession, "ETW-KernelFile");




//            //TraceEventSession.GetActiveSession(SessionName)?.Dispose(); // Dispose any stale session with same name

//            //// LƯU Ý: Không dùng KernelSessionName nữa, dùng session thường
//            //using (var session = new TraceEventSession(SessionName))
//            //{
//            //    Console.CancelKeyPress += (s, e) => session.Dispose();

//            //    // Kích hoạt Provider hiện đại của Windows
//            //    session.EnableProvider("Microsoft-Windows-Kernel-File");

//            //    // Vì là Provider động, ta sẽ hứng dữ liệu qua sự kiện .Dynamic.All
//            //    session.Source.Dynamic.All += (TraceEvent data) =>
//            //    {
//            //        if (data.ProcessID == Environment.ProcessId)
//            //        {
//            //            // Chỉ lọc các sự kiện có tên là "Create" (tương đương Open/Create)
//            //            if (data.ProviderName == "Microsoft-Windows-Kernel-File" && data.EventName == "Create")
//            //            {
//            //                string fileName = (string)data.PayloadByName("FileName");

//            //                uint? desiredAccess = (uint?)data.PayloadByName("DesiredAccess");
//            //                int? createOptions = (int?)data.PayloadByName("CreateOptions");

//            //                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] PID={data.ProcessID} {Environment.ProcessId} ({data.ProcessName}) " +
//            //                                  $"Mở File: {fileName} | DesiredAccess: 0x{desiredAccess:X}");
//            //            }
//            //        }
//            //    };

//            //    Console.WriteLine("[+] Đang lắng nghe qua Modern ETW...");
//            //    session.Source.Process();
//            //}


//            //_logger.LogInfo("[ETW] Creating kernel trace session...");
//            //using var _session = new TraceEventSession(SessionName)
//            //{
//            //    StopOnDispose = true
//            //};
//            //_session.EnableKernelProvider(
//            //    KernelTraceEventParser.Keywords.FileIOInit |   // FileCreate, FileDelete, etc.
//            //    KernelTraceEventParser.Keywords.FileIO |   // Read/Write operations
//            //    KernelTraceEventParser.Keywords.DiskFileIO |   // Disk-level file I/O
//            //    KernelTraceEventParser.Keywords.Process          // For process lifetime
//            //);
//            //var parser = _session.Source.Kernel;

//            //parser.FileIOCreate += OnFileCreate;
//            //parser.FileIORead += OnFileRead;
//            //parser.FileIOWrite += OnFileUpdate;
//            //parser.FileIODelete += OnFileDelete;
//            //parser.FileIOClose += OnFileClose;
//            //parser.FileIOSetInfo += OnFileSetInfo;

//            //parser.ProcessStart += e =>
//            //{
//            //    if (_tree.IsTracked(e.ParentID))
//            //    {
//            //        _logger.LogInfo(
//            //            $"[PROCESS] New child process spawned: PID={e.ProcessID} " +
//            //            $"'{e.ImageFileName}' by ParentPID={e.ParentID}");
//            //        _tree.RefreshChildren();
//            //    }
//            //};
//            //parser.ProcessStop += e =>
//            //{
//            //    if (_tree.IsTracked(e.ProcessID))
//            //    {
//            //        _logger.LogInfo(
//            //            $"[PROCESS] Tracked process exited: PID={e.ProcessID} " +
//            //            $"'{e.ProcessName}' ExitCode={e.ExitStatus}");
//            //    }
//            //};

//            //// Stop when cancellation requested
//            //ct.Register(() =>
//            //{
//            //    _logger.LogInfo("[ETW] Stopping session...");
//            //    _session?.Stop();
//            //});
//            //_logger.LogInfo("[ETW] Session started. Listening for file system events...");
//            //_logger.LogInfo(new string('─', 80));

//            //// Blocking call - processes events until session is stopped
//            //_session.Source.Process();
//            //_logger.LogInfo("[ETW] Session ended.");
//        }
//        private void SubscribeKernelFileEvents()
//        {
//            _fileSession!.Source.Dynamic.All += e =>
//            {
//                if (e.ProcessID == Environment.ProcessId) return;
//                if (!_tree.IsTracked(e.ProcessID)) return;
//                if (e.ProviderGuid != KernelFileProviderGuid) return;
//                Console.WriteLine($"{e.ProviderName} {e.EventName} ID={e.ID}");

//                for (int i = 0; i < e.PayloadNames.Length; i++)
//                {
//                    Console.WriteLine(
//                        $"{e.PayloadNames[i]} = {e.PayloadValue(i)}");
//                }
//                switch (e.ID)
//                {
//                    // ── True new file created in namespace ───────────────────────
//                    case (TraceEventID)10:
//                        {
//                            var path = e.PayloadStringByName("FileName") ?? "";
//                            if (string.IsNullOrEmpty(path) || IsDirectory(path)) return;

//                            _logger.LogWarning($"Add key NAME_CREATE");
//                            //TryWrite(new RawEvent
//                            //{
//                            //    Pid = e.ProcessID,
//                            //    EventType = EventTypes.FileCreate,
//                            //    Path = path
//                            //});
//                            break;
//                        }

//                    // ── IRP_MJ_CREATE — filter by disposition ────────────────────
//                    // Only emit when disposition implies writing a new/replaced file.
//                    // Skips FILE_OPEN (1) and FILE_OPEN_IF (3) to eliminate the flood.
//                    case (TraceEventID)12:
//                        {
//                            var path = e.PayloadStringByName("OpenPath") ?? "";
//                            var disposition = GetUInt32Payload(e, "CreateDisposition");

//                            //if (string.IsNullOrEmpty(path)) return;
//                            if (IsDirectory(path)) return;


//                            // Ignore pure opens
//                            //if (disposition is not (FILE_SUPERSEDE or FILE_CREATE or
//                            //                        FILE_OVERWRITE or FILE_OVERWRITE_IF))
//                            //    return;

//                            // Prefer NameCreate for FILE_CREATE disposition (more reliable).
//                            // Emit here only for supersede/overwrite which NameCreate skips.
//                            //if (disposition == FILE_CREATE) return; // NameCreate will handle it

//                            _logger.LogWarning($"Add key CREATE - {disposition}");

//                            //TryWrite(new RawEvent
//                            //{
//                            //    Pid = e.ProcessID,
//                            //    EventType = EventTypes.FileCreate,
//                            //    Path = path
//                            //});
//                            break;
//                        }

//                    // ── Name removed from namespace ──────────────────────────────
//                    case (TraceEventID)11:
//                        {
//                            var path = e.PayloadStringByName("FileName") ?? "";
//                            if (string.IsNullOrEmpty(path) || IsDirectory(path)) return;
//                            _logger.LogWarning($"Add key NAME_DELETE");

//                            //TryWrite(new RawEvent
//                            //{
//                            //    Pid = e.ProcessID,
//                            //    EventType = EventTypes.FileDelete,
//                            //    Path = path
//                            //});
//                            break;
//                        }

//                    // ── Delete-on-close flag set ─────────────────────────────────
//                    // Fires when a process marks a handle for deletion on close.
//                    // Useful for tracking deferred deletes (e.g. temp files).
//                    case (TraceEventID)30:
//                        {
//                            var path = e.PayloadStringByName("FileName") ?? "";
//                            if (string.IsNullOrEmpty(path) || IsDirectory(path)) return;

//                            _logger.LogWarning($"Add key DELETE");
//                            //TryWrite(new RawEvent
//                            //{
//                            //    Pid = e.ProcessID,
//                            //    EventType = EventTypes.FileDelete,
//                            //    Path = path
//                            //});
//                            break;
//                        }
//                }
//            };
//        }
//        private static uint? GetUInt32Payload(TraceEvent e, string name)
//        {
//            try { return (uint?)e.PayloadByName(name); }
//            catch { return uint.MaxValue; }
//        }
//        private void StartProcessingThread(TraceEventSession session, string name)
//        {
//            session.Source.Process();
//        }

//        private bool IsValidEvent(TraceEvent e, string fileName)
//        {
//            // --- Process filter ---
//            int pid = e.ProcessID;
//            if (!_tree.IsTracked(pid)) return false;
//            if (string.IsNullOrEmpty(fileName)) return false;
//            if (pid == Environment.ProcessId) return false;

//            // --- File name filter ---
//            var lowerFileName = fileName.ToLower();
//            var regexpTempFile = new Regex(@"[\\/]\.[^\\/]+$");
//            if (lowerFileName.Contains("program files") || lowerFileName.Contains("c:\\windows") || lowerFileName.Contains("c:\\programdata") || regexpTempFile.Match("fileName").Success)
//            {
//                return false;
//            }
//            string ext = Path.GetExtension(fileName).ToLower();
//            if (ext is ".dll" or ".mui" or ".ini" or ".sys" or ".lnk" or ".exe" or ".bin" or ".bak" or ".ttf" or ".tmp" or ".pnf" or null or "" or ".pdb") // Ignore system file => should replace by whitelist
//            {
//                return false;
//            }
//            return true;
//        }

//        private bool ExistInCache(string uniqueKey)
//        {
//            var policy = new CacheItemPolicy
//            {
//                SlidingExpiration = TimeSpan.FromSeconds(10)
//            };
//            if (_eventCache.Contains(uniqueKey))
//            {
//                var existingKey = (_eventCache.Get(uniqueKey) as int?) ?? 0 + 1;
//                _eventCache.Set(uniqueKey, existingKey, policy);
//                return true;
//            }
//            _eventCache.Set(uniqueKey, 1, policy);
//            _logger.LogWarning($"Add key {uniqueKey}");
//            return false;
//        }
//        private static bool IsDirectory(string path) =>
//        path.EndsWith('\\') || path.EndsWith('/');
//        private void OnFileCreate(FileIOCreateTraceData e)
//        {
//            var operation = "CREATE";
//            try
//            {
//                if (!IsValidEvent(e, e.FileName)) return;

//                // --- Options filter ---
//                int createOptions = (int)e.CreateOptions;
//                int disposition = (int)e.CreateDisposition;
//                if ((createOptions & 0x00000001) != 0 || Directory.Exists(e.FileName)) // Ignore folder
//                {
//                    return;
//                }
//                if ((createOptions & 0x00200000) != 0) // Only for link/junction/reparse point
//                {
//                    return;
//                }
//                if (e.CreateDisposition == CreateDisposition.OPEN_EXISTING) // OPEN, not CREATE
//                {
//                    return;
//                }

//                if ((createOptions & 0x1000) != 0) // OPEN TO DELETE
//                {
//                    _logger.LogWarning($"This is delete on close operation, skip logging for {e.FileName}");
//                    return;
//                }

//                string uniqueKey = $"{e.ProcessID}_{operation}_{e.FileName.ToLower()}";
//                if (ExistInCache(uniqueKey)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                    ExtraInfo = $"CreateOptions=0x{e.CreateOptions:X} ShareAccess=0x{e.ShareAccess:X} Disposition={e.CreateDisposition} {e.FileName}"
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }
//        private void OnFileRead(FileIOReadWriteTraceData e)
//        {
//            var operation = "READ";
//            try
//            {
//                if (!IsValidEvent(e, e.FileName)) return;

//                string uniqueKey = $"{e.ProcessID}_{operation}_{e.FileName.ToLower()}";
//                if (ExistInCache(uniqueKey)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                    ExtraInfo = $"Offset={e.Offset} Size={e.IoSize}B"
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }

//        private void OnFileDelete(FileIOInfoTraceData e)
//        {
//            var operation = "DELETE";
//            try
//            {
//                if (!IsValidEvent(e, e.FileName)) return;

//                string uniqueKey = $"{e.ProcessID}_{operation}_{e.FileName.ToLower()}";
//                if (ExistInCache(uniqueKey)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                    ExtraInfo = ""
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }

//        private void OnFileClose(FileIOSimpleOpTraceData e)
//        {
//            var operation = "CLOSE";
//            try
//            {
//                if (!IsValidEvent(e, e.FileName)) return;

//                string uniqueKey = $"{e.ProcessID}_{operation}_{e.FileName.ToLower()}";
//                if (ExistInCache(uniqueKey)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                    ExtraInfo = ""
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }
//        private void OnFileSetInfo(FileIOInfoTraceData e)
//        {
//            var operation = "SETINFO";
//            try
//            {
//                if (!IsValidEvent(e, e.FileName)) return;

//                string uniqueKey = $"{e.ProcessID}_{operation}_{e.FileName.ToLower()}";
//                if (ExistInCache(uniqueKey)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                    ExtraInfo = ""
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogWarning($"[ETW] Error handling {operation} event: {ex.Message}");
//            }
//        }
//        private void OnFileUpdate(FileIOReadWriteTraceData e)
//        {
//            var operation = "UPDATE";
//            try
//            {
//                if (!IsValidEvent(e, e.FileName)) return;

//                string uniqueKey = $"{e.ProcessID}_{operation}_{e.FileName.ToLower()}";
//                if (ExistInCache(uniqueKey)) return;

//                _logger.LogFileEvent(new FileAuditEvent
//                {
//                    Timestamp = e.TimeStamp,
//                    Operation = operation,
//                    Pid = e.ProcessID,
//                    ProcessName = e.ProcessName,
//                    FileName = e.FileName,
//                    ExtraInfo = $"Offset={e.Offset} Size={e.IoSize}B"
//                });
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
//            public int Pid { get; set; }
//            public string ProcessName { get; set; } = "";
//            public string ExtraInfo { get; set; } = "";
//        }
//    }
//}
