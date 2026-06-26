using System;
using System.IO;
using System.Text;

namespace ProcessFileMonitor.Logging;
/// <summary>
/// Ghi từng dòng JSON string vào file NDJSON.
/// Khi file vượt quá MaxFileSizeBytes, tự động xóa TrimFraction đầu file.
/// </summary>
public sealed class NdjsonFileWriter : IDisposable
{
    // ── Cấu hình ────────────────────────────────────────────────────────────
    private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB
    private const double TrimFraction = 0.4;              // xóa 40% đầu file
    // ────────────────────────────────────────────────────────────────────────

    private readonly string _filePath;
    private FileStream _fs;
    private StreamWriter _writer;
    private bool _disposed;

    public NdjsonFileWriter(string filePath)
    {
        _filePath = filePath;
        OpenAppend();
    }

    /// <summary>Ghi 1 JSON string thành 1 dòng. Thread-safe không cần thiết — single process.</summary>
    public void Write(string jsonLine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Kiểm tra size trước khi ghi
        if (_fs.Length >= MaxFileSizeBytes)
            TrimOldRecords();

        _writer.WriteLine(jsonLine);
    }

    // ── Trim ────────────────────────────────────────────────────────────────

    private void TrimOldRecords()
    {
        // Flush + đóng file hiện tại trước khi đọc lại
        _writer.Flush();
        _writer.Dispose();
        _fs.Dispose();

        // Xác định vị trí byte bắt đầu giữ lại
        var fileBytes = new FileInfo(_filePath).Length;
        var skipBytes = (long)(fileBytes * TrimFraction);

        // Đọc từ skipBytes, tìm đầu dòng tiếp theo để không cắt giữa record
        byte[] kept;
        using (var readFs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            readFs.Seek(skipBytes, SeekOrigin.Begin);

            // Bỏ qua phần dòng dở (tìm '\n' tiếp theo)
            int b;
            while ((b = readFs.ReadByte()) != -1 && b != '\n') { }

            // Đọc phần còn lại
            var remaining = (int)(readFs.Length - readFs.Position);
            kept = new byte[remaining];
            readFs.Read(kept, 0, remaining);
        }

        // Ghi đè file với phần data giữ lại
        File.WriteAllBytes(_filePath, kept);

        // Mở lại ở append mode
        OpenAppend();
    }

    private void OpenAppend()
    {
        _fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_fs, Encoding.UTF8) { AutoFlush = true };
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _writer.Flush();
        _writer.Dispose();
        _fs.Dispose();
        _disposed = true;
    }
}