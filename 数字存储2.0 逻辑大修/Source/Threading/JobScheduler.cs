using System;
using System.Collections.Concurrent;
using System.Threading;
using Verse;

namespace DigitalStorage.Threading
{
    public sealed class JobScheduler
    {
        public static readonly JobScheduler Instance = new JobScheduler();

        private readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly Thread[] workers;
        private volatile bool running = true;
        private int pending;

        private const int MaxQueue = 200000;

        private JobScheduler()
        {
            int workerCount = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));
            this.workers = new Thread[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                this.workers[i] = new Thread(WorkerLoop)
                {
                    Name = $"DigitalStorage-Worker-{i}",
                    IsBackground = true
                };
                this.workers[i].Start();
            }
        }

        private void WorkerLoop()
        {
            while (this.running)
            {
                if (this.queue.TryDequeue(out Action action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[数字存储] Worker exception: {ex}");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref this.pending);
                    }
                }
                else
                {
                    this.signal.WaitOne(5);
                }
            }
        }

        public bool TryEnqueue(Action job)
        {
            if (job == null)
            {
                return false;
            }

            if (Interlocked.Increment(ref this.pending) > MaxQueue)
            {
                Interlocked.Decrement(ref this.pending);
                return false;
            }

            this.queue.Enqueue(job);
            this.signal.Set();
            return true;
        }

        public int Pending => this.pending;

        public void Stop()
        {
            this.running = false;
            for (int i = 0; i < this.workers.Length; i++)
            {
                this.signal.Set();
            }

            foreach (Thread worker in this.workers)
            {
                try
                {
                    worker.Join(200);
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }
}

