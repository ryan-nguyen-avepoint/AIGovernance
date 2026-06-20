//using System;
//using System.Collections.Concurrent;
//using System.Collections.Generic;
//using System.Threading;
//using System.Threading.Tasks;

///// <summary>
///// Xử lý event theo batch:
/////   - Nếu queue trống → chờ 1s rồi check lại
/////   - Nếu có event     → chờ thêm 2s để gom thêm event → drain toàn bộ → xử lý 1 lần đồng bộ
///// </summary>
//public class BatchQueueProcessor<T> : IDisposable
//{
//    private readonly ConcurrentQueue<T> _queue;
//    private readonly Func<IReadOnlyList<T>, Task> _processBatch;
//    private readonly TimeSpan _batchWindow;
//    private readonly TimeSpan _emptyQueuePollInterval;

//    private readonly CancellationTokenSource _cts = new();
//    private Task? _workerTask;

//    /// <param name="queue">Queue dùng chung với producer</param>
//    /// <param name="processBatch">Hàm xử lý 1 batch (đồng bộ từ phía caller)</param>
//    /// <param name="batchWindowMs">Thời gian gom event sau khi phát hiện queue có data (mặc định 2000ms)</param>
//    /// <param name="emptyPollMs">Thời gian chờ khi queue trống (mặc định 1000ms)</param>
//    public BatchQueueProcessor(
//        ConcurrentQueue<T> queue,
//        Func<IReadOnlyList<T>, Task> processBatch,
//        int batchWindowMs = 2000,
//        int emptyPollMs = 1000)
//    {
//        _queue = queue;
//        _processBatch = processBatch;
//        _batchWindow = TimeSpan.FromMilliseconds(batchWindowMs);
//        _emptyQueuePollInterval = TimeSpan.FromMilliseconds(emptyPollMs);
//    }

//    /// <summary>Khởi động worker thread nền.</summary>
//    public void Start()
//    {
//        if (_workerTask is not null)
//            throw new InvalidOperationException("Processor đã được khởi động.");

//        _workerTask = Task.Run(() => RunLoopAsync(_cts.Token));
//    }

//    private async Task RunLoopAsync(CancellationToken ct)
//    {
//        while (!ct.IsCancellationRequested)
//        {
//            // --- Bước 1: Kiểm tra queue có event chưa ---
//            if (_queue.IsEmpty)
//            {
//                try
//                {
//                    await Task.Delay(_emptyQueuePollInterval, ct);
//                }
//                catch (OperationCanceledException) { break; }

//                continue;
//            }

//            // --- Bước 2: Queue có event → chờ 2s để gom thêm ---
//            try
//            {
//                await Task.Delay(_batchWindow, ct);
//            }
//            catch (OperationCanceledException)
//            {
//                // Bị huỷ trong lúc chờ → vẫn drain & xử lý những gì còn trong queue
//                DrainAndProcess(Array.Empty<T>()).GetAwaiter().GetResult();
//                break;
//            }

//            // --- Bước 3: Drain toàn bộ queue vào batch ---
//            var batch = new List<T>();
//            while (_queue.TryDequeue(out var item))
//                batch.Add(item);

//            if (batch.Count == 0)
//                continue; // Hiếm gặp: producer drain trước mình, tiếp tục vòng lặp

//            // --- Bước 4: Xử lý đồng bộ — không song song ---
//            try
//            {
//                await _processBatch(batch);
//            }
//            catch (Exception ex)
//            {
//                // Tuỳ nghiệp vụ: log lỗi, retry, dead-letter, v.v.
//                Console.Error.WriteLine($"[BatchProcessor] Lỗi khi xử lý batch {batch.Count} event: {ex}");
//            }
//        }
//    }

//    private async Task DrainAndProcess(IReadOnlyList<T> prefix)
//    {
//        var batch = new List<T>(prefix);
//        while (_queue.TryDequeue(out var item))
//            batch.Add(item);

//        if (batch.Count > 0)
//            await _processBatch(batch);
//    }

//    /// <summary>Dừng gracefully: chờ worker xử lý xong batch hiện tại.</summary>
//    public async Task StopAsync()
//    {
//        _cts.Cancel();
//        if (_workerTask is not null)
//            await _workerTask.ConfigureAwait(false);
//    }

//    public void Dispose()
//    {
//        _cts.Cancel();
//        _cts.Dispose();
//    }
//}

//// ──────────────────────────────────────────────
//// Demo sử dụng
//// ──────────────────────────────────────────────
//internal static class Program
//{
//    private static async Task Main()
//    {
//        var queue = new ConcurrentQueue<string>();

//        // Producer: add event liên tục từ thread riêng
//        var producerCts = new CancellationTokenSource();
//        var producer = Task.Run(async () =>
//        {
//            int id = 0;
//            while (!producerCts.Token.IsCancellationRequested)
//            {
//                queue.Enqueue($"event-{++id}");
//                Console.WriteLine($"  [Producer] Enqueued event-{id}  (queue size ≈ {queue.Count})");
//                await Task.Delay(300, producerCts.Token).ConfigureAwait(false);
//            }
//        }, producerCts.Token);

//        // Consumer: batch processor
//        var processor = new BatchQueueProcessor<string>(
//            queue,
//            async batch =>
//            {
//                Console.WriteLine($"\n>>> [Processor] Bắt đầu xử lý batch {batch.Count} event: [{string.Join(", ", batch)}]");
//                await Task.Delay(500); // Giả lập xử lý mất 500ms
//                Console.WriteLine($"<<< [Processor] Xong batch\n");
//            },
//            batchWindowMs: 2000,
//            emptyPollMs: 1000
//        );

//        processor.Start();

//        // Chạy thử 12 giây
//        await Task.Delay(12_000);

//        producerCts.Cancel();
//        await processor.StopAsync();
//        Console.WriteLine("=== Processor đã dừng ===");
//    }
//}