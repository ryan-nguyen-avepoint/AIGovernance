//using System;
//using System.Collections.Generic;
//using System.Threading;
//using System.Threading.Channels;
//using System.Threading.Tasks;

//// ══════════════════════════════════════════════════════════════════════════════
////  Pipeline:
////
////  Producer(s) ──► EventChannel ──► [Batcher Task] ──► BatchChannel ──► [Processor Task]
////
////  EventChannel  : bounded, DropOldest  → tự xóa event cũ khi đầy
////  Batcher       : gom tối đa 2s HOẶC đủ 1000 event thì push batch ngay
////  BatchChannel  : bounded, Wait        → back-pressure: block Batcher khi Processor chậm
////  Processor     : ReadAllAsync tuần tự → không bao giờ 2 batch song song
//// ══════════════════════════════════════════════════════════════════════════════

//public sealed class ChannelBatchProcessor<T> : IAsyncDisposable
//{
//    // ── Cấu hình ─────────────────────────────────────────────────────────────

//    private readonly int _maxBatchSize;        // đủ N event → flush ngay
//    private readonly TimeSpan _batchWindow;       // chưa đủ N → flush sau window này
//    private readonly Func<IReadOnlyList<T>, Task> _processBatch;

//    // ── Channels ──────────────────────────────────────────────────────────────

//    /// <summary>
//    /// Event đầu vào. DropOldest: khi đầy, event cũ nhất bị bỏ tự động,
//    /// producer không bao giờ bị block.
//    /// </summary>
//    private readonly Channel<T> _eventChannel;

//    /// <summary>
//    /// Batch đầu ra. Wait: khi Processor xử lý chậm và channel đầy,
//    /// Batcher bị block tại WriteAsync → back-pressure tự nhiên.
//    /// </summary>
//    private readonly Channel<List<T>> _batchChannel;

//    // ── Internal ──────────────────────────────────────────────────────────────

//    private readonly CancellationTokenSource _cts = new();
//    private Task? _batcherTask;
//    private Task? _processorTask;

//    // ── Constructor ───────────────────────────────────────────────────────────

//    /// <param name="eventChannelCapacity">
//    ///     Số event tối đa trong channel đầu vào.
//    ///     Khi đầy, event cũ nhất tự động bị drop (DropOldest).
//    /// </param>
//    /// <param name="maxBatchSize">
//    ///     Flush batch ngay khi đủ số event này (mặc định 1000).
//    /// </param>
//    /// <param name="batchWindowMs">
//    ///     Flush batch sau tối đa N ms nếu chưa đủ maxBatchSize (mặc định 2000ms).
//    /// </param>
//    /// <param name="maxPendingBatches">
//    ///     Số batch tối đa đang chờ Processor. Khi đầy, Batcher bị block (back-pressure).
//    /// </param>
//    public ChannelBatchProcessor(
//        Func<IReadOnlyList<T>, Task> processBatch,
//        int eventChannelCapacity = 10_000,
//        int maxBatchSize = 1_000,
//        int batchWindowMs = 2_000,
//        int maxPendingBatches = 5)
//    {
//        _processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));
//        _maxBatchSize = maxBatchSize;
//        _batchWindow = TimeSpan.FromMilliseconds(batchWindowMs);

//        _eventChannel = Channel.CreateBounded<T>(new BoundedChannelOptions(eventChannelCapacity)
//        {
//            FullMode = BoundedChannelFullMode.DropOldest, // drop event cũ, ưu tiên mới
//            SingleReader = true,   // chỉ Batcher đọc
//            SingleWriter = false,  // nhiều producer có thể ghi đồng thời
//        });

//        _batchChannel = Channel.CreateBounded<List<T>>(new BoundedChannelOptions(maxPendingBatches)
//        {
//            FullMode = BoundedChannelFullMode.Wait, // back-pressure: block Batcher
//            SingleReader = true,  // chỉ Processor đọc
//            SingleWriter = true,  // chỉ Batcher ghi
//        });
//    }

//    // ── Public API ────────────────────────────────────────────────────────────

//    public void Start()
//    {
//        if (_batcherTask is not null) throw new InvalidOperationException("Đã start rồi.");
//        _batcherTask = Task.Run(() => BatcherLoopAsync(_cts.Token));
//        _processorTask = Task.Run(() => ProcessorLoopAsync(_cts.Token));
//    }

//    /// <summary>
//    /// Thêm event từ producer. Không bao giờ block — nếu channel đầy thì event
//    /// cũ nhất bị drop tự động (DropOldest).
//    /// </summary>
//    public bool TryWrite(T item) => _eventChannel.Writer.TryWrite(item);

//    /// <summary>Dừng graceful: chờ batch đang xử lý hoàn thành.</summary>
//    public async ValueTask DisposeAsync()
//    {
//        // 1. Báo Batcher dừng
//        _cts.Cancel();
//        _eventChannel.Writer.TryComplete();

//        // 2. Chờ Batcher xong (nó sẽ complete _batchChannel)
//        if (_batcherTask is not null)
//            await _batcherTask.ConfigureAwait(false);

//        // 3. Chờ Processor xử lý nốt các batch còn trong channel
//        if (_processorTask is not null)
//            await _processorTask.ConfigureAwait(false);

//        _cts.Dispose();
//    }

//    // ── Stage 1: Batcher ─────────────────────────────────────────────────────

//    private async Task BatcherLoopAsync(CancellationToken ct)
//    {
//        var reader = _eventChannel.Reader;
//        var writer = _batchChannel.Writer;

//        try
//        {
//            while (!ct.IsCancellationRequested || reader.Count > 0)
//            {
//                // Chờ có ít nhất 1 event
//                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
//                    break; // channel đã complete

//                var batch = new List<T>(_maxBatchSize);
//                var deadline = DateTime.UtcNow.Add(_batchWindow);

//                // Gom event cho đến khi: đủ maxBatchSize HOẶC hết batchWindow
//                while (batch.Count < _maxBatchSize)
//                {
//                    if (deadline - DateTime.UtcNow <= TimeSpan.Zero) break;
//                    if (reader.TryRead(out var item))
//                    {
//                        batch.Add(item);
//                        continue;
//                    }

//                    // Hết event tạm thời → kiểm tra deadline
//                    var remaining = deadline - DateTime.UtcNow;
//                    if (remaining <= TimeSpan.Zero)
//                        break; // hết 2s → flush dù chưa đủ 1000

//                    // Chờ thêm event mới, timeout = thời gian còn lại trong window
//                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
//                    timeoutCts.CancelAfter(remaining);

//                    try
//                    {
//                        if (!await reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
//                            break; // channel complete
//                    }
//                    catch (OperationCanceledException)
//                    {
//                        // Timeout hết 2s → flush, hoặc ct bị cancel → thoát
//                        break;
//                    }
//                }

//                if (batch.Count == 0)
//                    continue;

//                // Push batch vào channel — block nếu Processor chậm (back-pressure)
//                await writer.WriteAsync(batch, ct).ConfigureAwait(false);
//            }
//        }
//        catch (OperationCanceledException) { /* shutdown bình thường */ }
//        finally
//        {
//            writer.TryComplete();
//        }
//    }

//    // ── Stage 2: Processor ───────────────────────────────────────────────────

//    private async Task ProcessorLoopAsync(CancellationToken ct)
//    {
//        // ReadAllAsync tự thoát khi BatchChannel complete + rỗng
//        await foreach (var batch in _batchChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
//        {
//            try
//            {
//                await _processBatch(batch).ConfigureAwait(false);
//            }
//            catch (Exception ex)
//            {
//                Console.Error.WriteLine($"[Processor] Lỗi batch {batch.Count} event: {ex.Message}");
//            }
//        }
//    }
//}

//// ══════════════════════════════════════════════════════════════════════════════
////  Demo
//// ══════════════════════════════════════════════════════════════════════════════

//internal static class Program
//{
//    private static async Task Main()
//    {
//        var processor = new ChannelBatchProcessor<string>(
//            processBatch: async batch =>
//            {
//                Console.WriteLine($"[Processor] ▶ batch {batch.Count} event  [{batch[0]} … {batch[^1]}]");
//                await Task.Delay(1_500); // giả lập xử lý nặng
//                Console.WriteLine($"[Processor] ✓ xong\n");
//            },
//            eventChannelCapacity: 200,   // giữ tối đa 200 event, drop-oldest khi đầy
//            maxBatchSize: 10,    // demo dùng 10 thay vì 1000
//            batchWindowMs: 2_000,
//            maxPendingBatches: 3
//        );

//        processor.Start();

//        // Producer: 1 luồng thêm event đều đặn
//        var cts = new CancellationTokenSource();
//        _ = Task.Run(async () =>
//        {
//            int id = 0;
//            while (!cts.Token.IsCancellationRequested)
//            {
//                bool ok = processor.TryWrite($"ev{++id}");
//                Console.WriteLine($"[Producer]  +ev{id}  {(ok ? "ok" : "DROPPED")}");
//                await Task.Delay(150, cts.Token).ConfigureAwait(false);
//            }
//        }, cts.Token);

//        // Burst: thỉnh thoảng nhồi event nhanh để kích hoạt flush sớm (đủ 10 event)
//        _ = Task.Run(async () =>
//        {
//            await Task.Delay(3_000);
//            Console.WriteLine("\n[Burst] Nhồi 15 event cùng lúc...");
//            for (int i = 0; i < 15; i++)
//                processor.TryWrite($"burst{i}");
//        });

//        await Task.Delay(14_000);
//        cts.Cancel();
//        await processor.DisposeAsync();
//        Console.WriteLine("[Demo] Dừng.");
//    }
//}