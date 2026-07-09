using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace CADImporter
{
    /// <summary>
    /// Fans independent per-part CPU work (parsing, welding, normal generation, decimation)
    /// across the thread pool while the calling thread reports progress. Bodies must not
    /// touch the Unity object API — they run on worker threads. Pure math types
    /// (Vector3, Quaternion, Mathf) and Debug logging are safe.
    /// </summary>
    public static class CadParallel
    {
        /// <summary>
        /// Runs <paramref name="body"/> once per item, in parallel when there is more than one
        /// item and more than one core. <paramref name="onProgress"/>, when supplied, is invoked
        /// on the calling thread with the 0..1 completed fraction, so it can safely drive editor
        /// progress UI. Items must be independent: a body must only touch its own item's data.
        /// The first exception thrown by any body is rethrown on the calling thread.
        /// </summary>
        public static void ForEach<T>(IReadOnlyList<T> items, Action<T> body, Action<float> onProgress = null)
        {
            if (items == null || items.Count == 0) return;

            if (items.Count == 1 || Environment.ProcessorCount <= 1)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    body(items[i]);
                    onProgress?.Invoke((float)(i + 1) / items.Count);
                }
                return;
            }

            int done = 0;
            var task = Task.Run(() => Parallel.ForEach(items, item =>
            {
                body(item);
                Interlocked.Increment(ref done);
            }));

            if (onProgress != null)
            {
                // Poll instead of blocking so progress keeps updating from this thread.
                int last = -1;
                while (!task.IsCompleted)
                {
                    int d = Volatile.Read(ref done);
                    if (d != last)
                    {
                        last = d;
                        onProgress((float)d / items.Count);
                    }
                    Thread.Sleep(10);
                }
            }

            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (AggregateException ae)
            {
                // Surface the real failure (a corrupt part file, a degenerate mesh) instead of
                // a wrapper whose message is just "One or more errors occurred".
                ExceptionDispatchInfo.Capture(ae.Flatten().InnerExceptions[0]).Throw();
            }

            onProgress?.Invoke(1f);
        }
    }
}
