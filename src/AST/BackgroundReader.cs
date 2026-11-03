using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Threading;

namespace AST
{
    /// <summary>
    /// Does the bars' slow reads one at a time on its own thread. Icons come from the shell, which can wait on a slow
    /// disk for many seconds, and doing that on the bars' thread froze them. It's a single-threaded apartment because
    /// some shell extensions need one.
    /// </summary>
    internal static class BackgroundReader
    {
        private static readonly BlockingCollection<Action> Work = StartWorker();

        private static BlockingCollection<Action> StartWorker()
        {
            var work = new BlockingCollection<Action>();
            var thread = new Thread(() =>
            {
                foreach (var action in work.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex) { Log.Write("A background read failed: " + ex.Message); }
                }
            }) { IsBackground = true, Name = "AST reader" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return work;
        }

        /// <summary>Runs read here, then passes its result to done on the calling thread.</summary>
        public static void Read<T>(Func<T> read, Action<T> done)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Work.Add(() =>
            {
                T result = read();
                dispatcher.BeginInvoke(new Action(() => done(result)));
            });
        }
    }
}
