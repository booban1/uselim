using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Uselim
{
    // One thread for the relay's whole state, the way the Mac's relay keeps to its main queue: sockets, timers and
    // background work hand their results in through Post, and every change to the relay happens here, one at a time.

    public sealed class Loop
    {
        private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        private readonly Thread thread;

        public Loop(string name)
        {
            thread = new Thread(Run) { IsBackground = true, Name = name };
            thread.Start();
        }

        public bool IsCurrent => Thread.CurrentThread == thread;

        public void Post(Action action)
        {
            try { work.Add(action); } catch (InvalidOperationException) { }
        }

        // the action after the seconds given, on this loop; Cancel() keeps it from running
        public Later After(double seconds, Action action)
        {
            var later = new Later();
            later.Timer = new Timer(_ =>
            {
                later.Timer?.Dispose();
                if (!later.IsCancelled) Post(() => { if (!later.IsCancelled) action(); });
            }, null, (long)Math.Max(0, seconds * 1000), Timeout.Infinite);
            return later;
        }

        // a value from the loop, for a caller on another thread that needs it now; the loop never waits on that caller
        public T Invoke<T>(Func<T> read)
        {
            if (IsCurrent) return read();
            var result = default(T);
            Exception failure = null;
            using (var done = new ManualResetEventSlim(false))
            {
                Post(() =>
                {
                    try { result = read(); }
                    catch (Exception e) { failure = e; }
                    finally { done.Set(); }
                });
                if (!done.Wait(5000)) throw new TimeoutException("the relay's loop stayed busy");
            }
            if (failure != null) throw failure;
            return result;
        }

        public void Stop() => work.CompleteAdding();

        private void Run()
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception e) { Log.Note("relay: " + e.GetType().Name + ", " + e.Message); }
            }
        }

        public sealed class Later
        {
            internal Timer Timer;
            internal volatile bool IsCancelled;

            public void Cancel()
            {
                IsCancelled = true;
                Timer?.Dispose();
            }
        }
    }
}
