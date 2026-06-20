using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ProcessFileMonitor.Logging
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        FileEvent = 10  // Special level for file I/O audit events
    }

    public class FileAuditEvent
    {
        public DateTime Timestamp { get; set; }
        public string Operation { get; set; } = "";
        public int Pid { get; set; }
        public string ProcessName { get; set; } = "";
        public string FileName { get; set; } = "";
        public string ExtraInfo { get; set; } = "";
    }

    public sealed class AuditLogger : IDisposable
    {
        private readonly StreamWriter _fileWriter;
        private readonly object _lock = new();
        private LogLevel _minLevel = LogLevel.Debug;

        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorInfo = (ConsoleColor.Cyan, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorWarning = (ConsoleColor.Yellow, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorError = (ConsoleColor.Red, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorDebug = (ConsoleColor.DarkGray, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorCreate = (ConsoleColor.Green, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorDelete = (ConsoleColor.Red, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorWrite = (ConsoleColor.Magenta, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorRead = (ConsoleColor.White, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorClose = (ConsoleColor.DarkCyan, ConsoleColor.Black);
        private static readonly (ConsoleColor fg, ConsoleColor bg) ColorDefault = (ConsoleColor.Gray, ConsoleColor.Black);

        public AuditLogger(string appName)
        {
            string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, $"{appName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            _fileWriter = new StreamWriter(logFile, append: false, Encoding.UTF8)
            {
                AutoFlush = false
            };

            // Write log header
            string header = $"""
            ================================================================================
              {appName} - Audit Log
              Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}
              Machine: {Environment.MachineName}
              User: {Environment.UserName}
              OS: {Environment.OSVersion}
            ================================================================================

            """;
            _fileWriter.Write(header);
            _fileWriter.Flush();
            Console.OutputEncoding = Encoding.UTF8;
        }

        public void LogDebug(string message) => Log(LogLevel.Debug, message);
        public void LogInfo(string message) => Log(LogLevel.Info, message);
        public void LogWarning(string message) => Log(LogLevel.Warning, message);
        public void LogError(string message) => Log(LogLevel.Error, message);

        private void Log(LogLevel level, string message)
        {
            if (level < _minLevel) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line = $"[{timestamp}] [{LevelTag(level)}] {message}";

            lock (_lock)
            {
                // Console
                var (fg, bg) = level switch
                {
                    LogLevel.Debug => ColorDebug,
                    LogLevel.Info => ColorInfo,
                    LogLevel.Warning => ColorWarning,
                    LogLevel.Error => ColorError,
                    _ => ColorDefault
                };
                WriteConsole(line, fg, bg);

                // File
                _fileWriter.WriteLine(line);
            }
        }

        public void LogFileEvent(FileAuditEvent ev)
        {
            string timestamp = ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string extra = string.IsNullOrEmpty(ev.ExtraInfo) ? "" : $" | {ev.ExtraInfo}";

            string tag = $"[FILE:{ev.Operation,-12}]";
            string line = $"[{timestamp}] {tag} PID={ev.Pid,-6} ({ev.ProcessName,-20}) {ev.FileName}{extra}";

            lock (_lock)
            {
                var (fg, bg) = ev.Operation switch
                {
                    "CREATE/OPEN" => ColorCreate,
                    "FILE_CREATED" => ColorCreate,
                    "WRITE" => ColorWrite,
                    "READ" => ColorRead,
                    "DELETE" => ColorDelete,
                    "FILE_DELETED" => ColorDelete,
                    "RENAME" => ColorWarning,
                    "CLOSE" => ColorClose,
                    _ => ColorDefault
                };

                WriteConsole(line, fg, bg);
                _fileWriter.WriteLine(line);
            }
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.FileEvent => "EVT",
            _ => "???"
        };

        private static void WriteConsole(string line, ConsoleColor fg, ConsoleColor bg)
        {
            var prevFg = Console.ForegroundColor;
            var prevBg = Console.BackgroundColor;
            Console.ForegroundColor = fg;
            Console.BackgroundColor = bg;
            Console.WriteLine(line);
            Console.ForegroundColor = prevFg;
            Console.BackgroundColor = prevBg;
        }

        public void Flush()
        {
            lock (_lock)
            {
                _fileWriter.Flush();
            }
        }

        public void Dispose()
        {
            Flush();
            _fileWriter.Dispose();
        }
    }
}
