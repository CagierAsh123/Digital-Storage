using System;
using System.Collections.Concurrent;
using Verse;

namespace DigitalStorage.Threading
{
    public static class MainThreadInvoker
    {
        private static readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();

        public static void Enqueue(Action action)
        {
            if (action == null)
            {
                return;
            }

            queue.Enqueue(action);
        }

        internal static void FlushOnce()
        {
            int maxExecute = 512;
            
            while (maxExecute-- > 0 && queue.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error($"[数字存储] Exception in main-thread action: {ex}");
                }
            }
        }

        public static int PendingCount => queue.Count;
    }
}

