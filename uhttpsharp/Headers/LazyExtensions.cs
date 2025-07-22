using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace uhttpsharp.Headers
{
    static class LazyExtensions
    {
        private class ExclusiveSynchronizationContext : SynchronizationContext
        {
            private readonly AutoResetEvent workItemsWaiting = new AutoResetEvent(false);
            private readonly ConcurrentQueue<(SendOrPostCallback, object)> items =
                new ConcurrentQueue<(SendOrPostCallback, object)>();
            private int done;
            private Task runningTask;

            public override void Send(SendOrPostCallback d, object state)
            {
                throw new NotSupportedException("We cannot send to our same thread");
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                items.Enqueue((d, state));
                workItemsWaiting.Set();
            }

            private void StartWithHelper<T>(object state)
            {
                Task<T> task = (Task<T>)state;
                runningTask = RunTaskWorker(task);
            }

            private async Task<T> RunTaskWorker<T>(Task<T> task)
            {
                try
                {
                    return await task;
                }
                finally
                {
                    EndMessageLoop();
                }
            }

            public void StartWith<T>(Task<T> task)
            {
                Post(StartWithHelper<T>, task);
            }

            public void EndMessageLoop()
            {
                if (Interlocked.CompareExchange(ref done, 1, 0) == 0)
                    workItemsWaiting.Set();
            }

            public void BeginMessageLoop()
            {
                while (done == 0)
                {
                    if (items.TryDequeue(out var work))
                    {
                        work.Item1(work.Item2);
                        ThrowIfExceptional();
                    }
                    else
                    {
                        workItemsWaiting.WaitOne();
                    }
                }
            }

            public T GetResult<T>()
            {
                return ((Task<T>)runningTask).Result;
            }

            internal void ThrowIfExceptional()
            {
                Exception ex = null;
                if (runningTask.IsCanceled)
                    ex = new TaskCanceledException(runningTask);
                else if (runningTask.IsFaulted)
                    ex = runningTask.Exception;

                if (ex != null)
                    throw ex;
            }

            public override SynchronizationContext CreateCopy()
            {
                return this;
            }
        }

        //see: https://devblogs.microsoft.com/dotnet/await-and-ui-and-deadlocks-oh-my/
        //https://blog.stephencleary.com/2012/07/dont-block-on-async-code.html
        //https://stackoverflow.com/a/5097066/3518520
        private static T RunSync<T>(Task<T> task)
        {
            if (task.IsCompleted)
                return task.Result;

            var oldContext = SynchronizationContext.Current;
            var syncContext = new ExclusiveSynchronizationContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(syncContext);
                syncContext.StartWith(task);
                syncContext.BeginMessageLoop();
                return syncContext.GetResult<T>();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(oldContext);
            }
        }

        //see: https://devblogs.microsoft.com/dotnet/asynclazyt/
        internal static Lazy<Task<T>> ToAsyncLazy<T>(this Lazy<T> lazy)
        {
            return new Lazy<Task<T>>(() => lazy.IsValueCreated ? Task.FromResult(lazy.Value) : Task.Run(() => lazy.Value));
        }

        internal static Lazy<T> FromAsyncLazy<T>(this Lazy<Task<T>> asyncLazy)
        {
            return new Lazy<T>(() => RunSync(asyncLazy.Value));
        }
    }
}
