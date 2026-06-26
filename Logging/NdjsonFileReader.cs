using System.Text;
using System.Text.Json;

namespace ProcessFileMonitor.Logging;

/// <summary>
/// Read NDJSON — lazy streaming, không load cả file vào RAM.
/// Dùng ở nơi khác trong app, hoàn toàn độc lập với NdjsonFileWriter.
/// </summary>
public static class NdjsonFileReader
{
    /// <summary>
    /// Đọc và deserialize từng dòng thành T.
    /// Bỏ qua dòng trắng và dòng lỗi parse.
    /// </summary>
    public static IEnumerable<T> ReadAll<T>(
        string filePath,
        JsonSerializerOptions? options = null)
    {
        if (!File.Exists(filePath)) yield break;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            T? record = default;
            try { record = JsonSerializer.Deserialize<T>(line, options); }
            catch { /* dòng lỗi / bị cắt → bỏ qua */ }

            if (record is not null) yield return record;
        }
    }

    /// <summary>Đọc N dòng cuối cùng — hiệu quả với file lớn.</summary>
    public static IEnumerable<T> ReadLast<T>(
        string filePath,
        int count,
        JsonSerializerOptions? options = null)
        => ReadAll<T>(filePath, options).TakeLast(count);
}

/*
// ── Đọc (nơi khác trong app) ─────────────────────────────────────────────────
// Đọc toàn bộ
foreach (var ev in NdjsonFileReader.ReadAll<YourEtwClass>("events.ndjson"))
{
    Console.WriteLine(ev.Timestamp);
}

// Đọc 200 record cuối (ví dụ: UI hiển thị log gần nhất)
var recent = NdjsonFileReader.ReadLast<YourEtwClass>("events.ndjson", 200).ToList();
*/