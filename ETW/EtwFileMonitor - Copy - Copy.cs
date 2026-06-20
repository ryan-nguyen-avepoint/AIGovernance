//using System;
//using System.Collections.Concurrent;
//using System.Collections.Generic;
//using System.Threading;
//using System.Threading.Channels;
//using System.Threading.Tasks;

//// ══════════════════════════════════════════════════════════════════════════════
//// SHARED: Bounded event queue — tự drop event CŨ nhất khi đầy, ưu tiên event mới
//// ══════════════════════════════════════════════════════════════════════════════

///// <summary>
///// Queue có giới hạn dung lượng. Khi đầy, tự động bỏ event cũ nhất để nhường
///// chỗ cho event mới (drop-oldest policy). Thread-safe.
///// </summary>
//public sealed class BoundedEventQueue<T>
//{
//    private readonly ConcurrentQueue<T> _inner = new();
//    private readonly int _capacity;
//    private int _count; // dùng Interlocked để đếm chính xác

//    public int Count => _count;
//    public bool IsEmpty => _count == 0;

//    public BoundedEventQueue(int capacity)
//    {
//        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
//        _capacity = capacity;
//    }

//    /// <summary>
//    /// Thêm event mới. Nếu queue đã đầy, drop event cũ nhất trước rồi mới thêm.
//    /// Trả về true nếu phải drop event cũ.
//    /// </summary>
//    public bool Enqueue(T item)
//    {
//        bool dropped = false;

//        while (true)
//        {
//            int current = _count;
//            if (current < _capacity)
//            {
//                // Còn chỗ — thử tăng counter
//                if (Interlocked.CompareExchange(ref _count, current + 1, current) == current)
//                {
//                    _inner.Enqueue(item);
//                    return dropped;
//                }
//                // CAS thất bại (race condition) → retry
//            }
//            else
//            {
//                // Đầy → drop event cũ nhất để nhường chỗ
//                if (_inner.TryDequeue(out _))
//                {
//                    Interlocked.Decrement(ref _count);
//                    dropped = true;
//                }
//                // Sau khi drop (hoặc nếu TryDequeue thất bại do race), retry từ đầu
//            }
//        }
//    }

//    public bool TryDequeue(out T item)
//    {
//        if (_inner.TryDequeue(out item!))
//        {
//            Interlocked.Decrement(ref _count);
//            return true;
//        }
//        return false;
//    }

//    /// <summary>Lấy tất cả item hiện có ra cùng lúc (drain).</summary>
//    public List<T> DrainAll()
//    {
//        var result = new List<T>();
//        while (TryDequeue(out var item))
//            result.Add(item);
//        return result;
//    }
//}

//// ══════════════════════════════════════════════════════════════════════════════
//// APPROACH 1: Single-loop với fire-and-forget có kiểm soát
//// ══════════════════════════════════════════════════════════════════════════════

///// <summary>
///// Approach 1 — Single worker loop:
/////
/////   [EventQueue] ──► [Worker loop: gom + dispatch] ──► ProcessBatch()
/////
/////   • Queue trống                → chờ emptyPollMs
/////   • Queue có event             → chờ batchWindowMs để gom thêm
/////   • Gom xong → await batch trước xong → dispatch batch mới (không block việc gom)
/////   • pendingBatchCount >= max   → tạm dừng gom, chờ processor tiêu thụ bớt
/////   • Không bao giờ 2 batch xử lý song song
///// </summary>
//public sealed class SingleLoopBatchProcessor<T> : IAsyncDisposable
//{
//    private readonly BoundedEventQueue<T> _eventQueue;
//    private readonly Func<IReadOnlyList<T>, Task> _processBatch;
//    private readonly TimeSpan _batchWindow;
//    private readonly TimeSpan _emptyPollInterval;
//    private readonly TimeSpan _backPressurePollInterval;
//    private readonly int _maxPendingBatches;

//    private readonly CancellationTokenSource _cts = new();
//    private Task? _workerTask;

//    private int _pendingBatchCount;

//    public int PendingBatchCount => _pendingBatchCount;

//    public SingleLoopBatchProcessor(
//        BoundedEventQueue<T> eventQueue,
//        Func<IReadOnlyList<T>, Task> processBatch,
//        int maxPendingBatches = 5,
//        int batchWindowMs = 2000,
//        int emptyPollMs = 1000,
//        int backPressurePollMs = 200)
//    {
//        _eventQueue = eventQueue;
//        _processBatch = processBatch;
//        _maxPendingBatches = maxPendingBatches;
//        _batchWindow = TimeSpan.FromMilliseconds(batchWindowMs);
//        _emptyPollInterval = TimeSpan.FromMilliseconds(emptyPollMs);
//        _backPressurePollInterval = TimeSpan.FromMilliseconds(backPressurePollMs);
//    }

//    public void Start()
//    {
//        if (_workerTask is not null) throw new InvalidOperationException("Đã start rồi.");
//        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
//    }

//    private async Task WorkerLoopAsync(CancellationToken ct)
//    {
//        // lastProcessingTask giữ reference đến batch đang xử lý
//        // để đảm bảo không có 2 batch chạy song song
//        Task lastProcessingTask = Task.CompletedTask;

//        while (!ct.IsCancellationRequested)
//        {
//            // ── Back-pressure: quá nhiều batch pending → tạm dừng gom ────────
//            while (_pendingBatchCount >= _maxPendingBatches && !ct.IsCancellationRequested)
//            {
//                await Task.Delay(_backPressurePollInterval, ct).ConfigureAwait(false);
//            }
//            if (ct.IsCancellationRequested) break;

//            // ── Queue trống → poll ────────────────────────────────────────────
//            if (_eventQueue.IsEmpty)
//            {
//                await Task.Delay(_emptyPollInterval, ct).ConfigureAwait(false);
//                continue;
//            }

//            // ── Queue có event → chờ batchWindow để gom thêm ─────────────────
//            await Task.Delay(_batchWindow, ct).ConfigureAwait(false);

//            var batch = _eventQueue.DrainAll();
//            if (batch.Count == 0) continue;

//            // ── Chờ batch trước xong (đảm bảo không song song) ───────────────
//            // Worker không bị block trong lúc gom ở bước trên,
//            // chỉ await tại đây ngay trước khi cần dispatch batch mới.
//            await lastProcessingTask.ConfigureAwait(false);

//            Interlocked.Increment(ref _pendingBatchCount);
//            lastProcessingTask = ProcessAndDecrementAsync(batch);
//            // KHÔNG await ở đây → worker tiếp tục vòng lặp gom event ngay
//        }

//        // Graceful shutdown: chờ batch cuối cùng xử lý xong
//        await lastProcessingTask.ConfigureAwait(false);
//    }

//    private async Task ProcessAndDecrementAsync(List<T> batch)
//    {
//        try
//        {
//            await _processBatch(batch).ConfigureAwait(false);
//        }
//        catch (Exception ex)
//        {
//            Console.Error.WriteLine($"[Approach1] Lỗi xử lý batch {batch.Count} event: {ex.Message}");
//        }
//        finally
//        {
//            Interlocked.Decrement(ref _pendingBatchCount);
//        }
//    }

//    public async ValueTask DisposeAsync()
//    {
//        _cts.Cancel();
//        if (_workerTask is not null)
//            await _workerTask.ConfigureAwait(false);
//        _cts.Dispose();
//    }
//}

//// ══════════════════════════════════════════════════════════════════════════════
//// APPROACH 2: Channel pipeline — Batcher task + Processor task tách biệt
//// ══════════════════════════════════════════════════════════════════════════════

///// <summary>
///// Approach 2 — Channel pipeline:
/////
/////   [EventQueue] ──► [Batcher Task] ──► Channel&lt;Batch&gt; ──► [Processor Task]
/////
/////   • Batcher: gom event mỗi batchWindowMs, WriteAsync vào channel
/////   • Channel bounded (maxPendingBatches, FullMode = Wait):
/////       khi channel đầy, WriteAsync block Batcher tự nhiên → back-pressure
/////   • Processor: ReadAllAsync tuần tự, không bao giờ 2 batch song song
/////   • Hai stage hoàn toàn độc lập về thời gian — Batcher không chờ Processor
///// </summary>
//public sealed class ChannelPipelineBatchProcessor<T> : IAsyncDisposable
//{
//    private readonly BoundedEventQueue<T> _eventQueue;
//    private readonly Func<IReadOnlyList<T>, Task> _processBatch;
//    private readonly Channel<List<T>> _batchChannel;
//    private readonly TimeSpan _batchWindow;
//    private readonly TimeSpan _emptyPollInterval;
//    private readonly TimeSpan _emptyPollInterval;

//    private readonly CancellationTokenSource _cts = new();
//    private Task? _batcherTask;
//    private Task? _processorTask;

//    public ChannelPipelineBatchProcessor(
//        BoundedEventQueue<T> eventQueue,
//        Func<IReadOnlyList<T>, Task> processBatch,
//        int maxPendingBatches = 5,
//        int batchWindowMs = 2000,
//        int emptyPollMs = 1000)
//    {
//        _eventQueue = eventQueue;
//        _processBatch = processBatch;
//        _batchWindow = TimeSpan.FromMilliseconds(batchWindowMs);
//        _emptyPollInterval = TimeSpan.FromMilliseconds(emptyPollMs);

//        // FullMode.Wait: khi channel đầy, WriteAsync tự block Batcher
//        // → back-pressure tự nhiên, không cần polling thủ công
//        _batchChannel = Channel.CreateBounded<List<T>>(new BoundedChannelOptions(maxPendingBatches)
//        {
//            FullMode = BoundedChannelFullMode.Wait,
//            SingleReader = true,
//            SingleWriter = true,
//        });
//    }

//    public void Start()
//    {
//        if (_batcherTask is not null) throw new InvalidOperationException("Đã start rồi.");
//        _batcherTask = Task.Run(() => BatcherLoopAsync(_cts.Token));
//        _processorTask = Task.Run(() => ProcessorLoopAsync(_cts.Token));
//    }

//    // ── Stage 1: Batcher ─────────────────────────────────────────────────────
//    private async Task BatcherLoopAsync(CancellationToken ct)
//    {
//        var writer = _batchChannel.Writer;
//        try
//        {
//            while (!ct.IsCancellationRequested)
//            {
//                // Queue trống → poll
//                if (_eventQueue.IsEmpty)
//                {
//                    await Task.Delay(_emptyPollInterval, ct).ConfigureAwait(false);
//                    continue;
//                }

//                // Gom event trong batchWindow
//                await Task.Delay(_batchWindow, ct).ConfigureAwait(false);

//                var batch = _eventQueue.DrainAll();
//                if (batch.Count == 0) continue;

//                // WriteAsync block tự nhiên nếu channel đầy → back-pressure
//                await writer.WriteAsync(batch, ct).ConfigureAwait(false);
//            }
//        }
//        catch (OperationCanceledException) { /* shutdown bình thường */ }
//        finally
//        {
//            // Báo cho Processor biết không còn batch nào nữa
//            writer.TryComplete();
//        }
//    }

//    // ── Stage 2: Processor ───────────────────────────────────────────────────
//    private async Task ProcessorLoopAsync(CancellationToken ct)
//    {
//        // ReadAllAsync tự thoát khi channel complete + rỗng
//        await foreach (var batch in _batchChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
//        {
//            try
//            {
//                await _processBatch(batch).ConfigureAwait(false);
//            }
//            catch (Exception ex)
//            {
//                Console.Error.WriteLine($"[Approach2] Lỗi xử lý batch {batch.Count} event: {ex.Message}");
//            }
//        }
//    }

//    public async ValueTask DisposeAsync()
//    {
//        _cts.Cancel();
//        if (_batcherTask is not null) await _batcherTask.ConfigureAwait(false);
//        if (_processorTask is not null) await _processorTask.ConfigureAwait(false);
//        _cts.Dispose();
//    }
//}

//// ══════════════════════════════════════════════════════════════════════════════
//// DEMO
//// ══════════════════════════════════════════════════════════════════════════════

//internal static class Program
//{
//    private static async Task Main()
//    {
//        Console.WriteLine("════════════════════════════════════════");
//        Console.WriteLine("  APPROACH 1: Single-loop");
//        Console.WriteLine("════════════════════════════════════════\n");
//        await RunDemoAsync(usePipeline: false);

//        Console.WriteLine("\n════════════════════════════════════════");
//        Console.WriteLine("  APPROACH 2: Channel pipeline");
//        Console.WriteLine("════════════════════════════════════════\n");
//        await RunDemoAsync(usePipeline: true);
//    }

//    private static async Task RunDemoAsync(bool usePipeline)
//    {
//        string tag = usePipeline ? "Pipeline" : "SingleLoop";

//        // EventQueue tối đa 20 event — khi đầy tự drop event cũ
//        var eventQueue = new BoundedEventQueue<string>(capacity: 20);

//        async Task ProcessBatch(IReadOnlyList<string> batch)
//        {
//            Console.WriteLine($"  [{tag}] ▶ Bắt đầu batch ({batch.Count} event): {string.Join(" ", batch)}");
//            await Task.Delay(1500); // Giả lập xử lý nặng
//            Console.WriteLine($"  [{tag}] ✓ Xong batch\n");
//        }

//        IAsyncDisposable processor;
//        if (usePipeline)
//        {
//            var p = new ChannelPipelineBatchProcessor<string>(
//                eventQueue, ProcessBatch,
//                maxPendingBatches: 3, batchWindowMs: 2000, emptyPollMs: 500);
//            p.Start();
//            processor = p;
//        }
//        else
//        {
//            var p = new SingleLoopBatchProcessor<string>(
//                eventQueue, ProcessBatch,
//                maxPendingBatches: 3, batchWindowMs: 2000, emptyPollMs: 500, backPressurePollMs: 200);
//            p.Start();
//            processor = p;
//        }

//        // Producer: thêm event nhanh, thỉnh thoảng burst để kích hoạt drop-oldest
//        var producerCts = new CancellationTokenSource();
//        _ = Task.Run(async () =>
//        {
//            int id = 0;
//            while (!producerCts.Token.IsCancellationRequested)
//            {
//                bool dropped = eventQueue.Enqueue($"ev{++id}");
//                string warn = dropped ? " ⚠ drop oldest" : "";
//                Console.WriteLine($"  [Producer] +ev{id}  queue={eventQueue.Count}{warn}");
//                await Task.Delay(250, producerCts.Token).ConfigureAwait(false);
//            }
//        }, producerCts.Token);

//        await Task.Delay(14_000);

//        producerCts.Cancel();
//        await processor.DisposeAsync();

//        Console.WriteLine($"  [Demo] Dừng. Event còn lại trong queue: {eventQueue.Count}");
//    }
//}