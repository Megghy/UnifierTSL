using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace UnifierTSL.Logging.LogWriters
{
    internal sealed class AsyncDurableLogWriter : LogWriter, ILogHistorySink, IDisposable
    {
        public const int BatchSize = 128;
        public const int BacklogWarnThreshold = 20_000;
        public const int BacklogWarnIntervalMs = 5_000;

        private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(3);

        private readonly IDurableLogSink sink;
        private readonly Channel<QueuedDurableLogRecord> queue;
        private readonly Task consumerTask;
        private readonly Lock disposeGate = new();
        private readonly Lock sinkGate = new();

        private long enqueuedCount;
        private long dequeuedCount;
        private long lastBacklogWarnTick;
        private int sinkFailed;
        private int sinkFailureReported;
        private int disposed;
        private bool sinkDisposed;

        public AsyncDurableLogWriter(IDurableLogSink sink) {
            this.sink = sink;
            queue = Channel.CreateUnbounded<QueuedDurableLogRecord>(new UnboundedChannelOptions {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            consumerTask = Task.Run(ConsumeLoop);
        }

        public override void Write(scoped in LogEntry log) {
            if (Volatile.Read(ref sinkFailed) != 0 || Volatile.Read(ref disposed) != 0) {
                return;
            }

            Enqueue(QueuedDurableLogRecord.FromLogEntry(in log));
        }

        public void Write(scoped in LogRecordView record) {
            if (Volatile.Read(ref sinkFailed) != 0 || Volatile.Read(ref disposed) != 0) {
                return;
            }

            Enqueue(QueuedDurableLogRecord.FromLogRecordView(in record));
        }

        private void Enqueue(QueuedDurableLogRecord record) {
            if (!queue.Writer.TryWrite(record)) {
                record.ReturnPooledResources();
                return;
            }

            long enqueued = Interlocked.Increment(ref enqueuedCount);
            long backlog = enqueued - Volatile.Read(ref dequeuedCount);
            TryWarnBacklog(backlog);
        }

        private static void ReturnBatchResources(List<QueuedDurableLogRecord> batch) {
            int count = batch.Count;
            for (int i = 0; i < count; i++) {
                batch[i].ReturnPooledResources();
            }

            batch.Clear();
        }

        private async Task ConsumeLoop() {
            try {
                while (await queue.Reader.WaitToReadAsync()) {
                    FlushSync();
                }
                FlushSync();
            }
            finally {
                // 超时只结束调用方的等待；sink 必须等最后一批写入完成后释放。
                lock (sinkGate) {
                    sinkDisposed = true;
                    try {
                        sink.Dispose();
                    }
                    catch (Exception ex) {
                        MarkSinkFailure(ex);
                    }
                }
            }
        }

        private void DrainPendingRecords(List<QueuedDurableLogRecord> batch) {
            while (queue.Reader.TryRead(out QueuedDurableLogRecord record)) {
                batch.Add(record);
                Interlocked.Increment(ref dequeuedCount);
                if (batch.Count >= BatchSize) {
                    FlushBatch(batch);
                }
            }
        }

        private void FlushBatch(List<QueuedDurableLogRecord> batch) {
            if (batch.Count <= 0) {
                return;
            }

            try {
                if (Volatile.Read(ref sinkFailed) == 0) {
                    sink.WriteBatch(CollectionsMarshal.AsSpan(batch));
                    sink.Flush();
                }
            }
            catch (Exception ex) {
                MarkSinkFailure(ex);
            }
            finally {
                ReturnBatchResources(batch);
            }
        }

        private void FlushSink() {
            if (Volatile.Read(ref sinkFailed) != 0) {
                return;
            }

            try {
                sink.Flush();
            }
            catch (Exception ex) {
                MarkSinkFailure(ex);
            }
        }

        private void TryWarnBacklog(long backlog) {
            if (backlog < BacklogWarnThreshold) {
                return;
            }

            long now = Environment.TickCount64;
            long previous = Volatile.Read(ref lastBacklogWarnTick);
            if (now - previous < BacklogWarnIntervalMs) {
                return;
            }

            if (Interlocked.CompareExchange(ref lastBacklogWarnTick, now, previous) != previous) {
                return;
            }

            Console.Error.WriteLine(
                GetParticularString("{0} is durable backlog count, {1} is durable backlog warning threshold", $"[Warning][LogCore|DurableQueue] Durable backlog is {backlog} entries (threshold: {BacklogWarnThreshold})."));
        }

        private void MarkSinkFailure(Exception ex) {
            Volatile.Write(ref sinkFailed, 1);
            if (Interlocked.CompareExchange(ref sinkFailureReported, 1, 0) != 0) {
                return;
            }

            Console.Error.WriteLine(
                GetParticularString("{0} is exception text", $"[Error][LogCore|DurableQueue] Durable sink disabled after failure: {ex}"));
        }

        public void FlushSync() {
            // 锁必须覆盖出队到落盘，防止同步刷新漏掉后台已出队但尚未写入的日志。
            lock (sinkGate) {
                if (sinkDisposed) {
                    return;
                }
                List<QueuedDurableLogRecord> batch = new(BatchSize);
                try {
                    DrainPendingRecords(batch);
                    FlushBatch(batch);
                    FlushSink();
                }
                finally {
                    ReturnBatchResources(batch);
                }
            }
        }

        public bool TryFlushAndStop(TimeSpan timeout) {
            lock (disposeGate) {
                if (Interlocked.Exchange(ref disposed, 1) == 0) {
                    queue.Writer.TryComplete();
                }
            }
            return WaitForConsumer(timeout);
        }

        private bool WaitForConsumer(TimeSpan timeout) {
            try {
                return consumerTask.Wait(timeout);
            }
            catch (AggregateException ex) {
                Exception root = ex.GetBaseException();
                MarkSinkFailure(root);
                return true;
            }
            catch (Exception ex) {
                MarkSinkFailure(ex);
                return true;
            }
        }

        public void Dispose() {
            _ = TryFlushAndStop(DefaultShutdownTimeout);
        }
    }
}
