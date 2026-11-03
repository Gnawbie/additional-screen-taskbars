using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace AST
{
    /// <summary>
    /// Logs what the bars' thread was doing if it stops responding. A frozen program that's ended from the Windows
    /// prompt leaves no error behind, so this is the only record of the cause.
    /// </summary>
    internal static class UiWatchdog
    {
        private const int FreezeMilliseconds = 8000, CheckMilliseconds = 250, MaxSuspendMilliseconds = 2000;

        public static void Start(Dispatcher dispatcher, Thread uiThread)
        {
            new Thread(() => Watch(dispatcher, uiThread)) { IsBackground = true, Name = "AST watchdog" }.Start();
        }

        private static void Watch(Dispatcher dispatcher, Thread uiThread)
        {
            while (!dispatcher.HasShutdownStarted)
            {
                var answered = new ManualResetEventSlim();
                dispatcher.BeginInvoke(new Action(answered.Set));
                var waited = Stopwatch.StartNew();
                if (!answered.Wait(FreezeMilliseconds))
                {
                    Log.Write("The bars have stopped responding. They were doing this:" + Environment.NewLine + StackOf(uiThread));
                    answered.Wait();
                    Log.Write("The bars responded again after " + (int)waited.Elapsed.TotalSeconds + " seconds");
                }
                Thread.Sleep(CheckMilliseconds);
            }
        }

        /// <summary>
        /// The thread has to be paused to read its stack, which can rarely deadlock, so a second thread makes sure it
        /// always carries on. This only runs when the thread is already frozen.
        /// </summary>
        private static string StackOf(Thread thread)
        {
#pragma warning disable 618
            var done = new ManualResetEventSlim();
            try
            {
                thread.Suspend();
                new Thread(() =>
                {
                    done.Wait(MaxSuspendMilliseconds);
                    try { thread.Resume(); } catch (ThreadStateException) { }
                }) { IsBackground = true }.Start();
                return new StackTrace(thread, false).ToString();
            }
            catch (Exception ex) { return "(the stack could not be read: " + ex.Message + ")"; }
            finally { done.Set(); }
#pragma warning restore 618
        }
    }
}
