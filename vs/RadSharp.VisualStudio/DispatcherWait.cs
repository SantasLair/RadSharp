using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace RadSharp.VisualStudio
{
    /// <summary>
    /// Waits for a task on the UI thread while pumping the WPF dispatcher, for synchronous COM calls (IPersistFileFormat.Save)
    /// whose work completes through Dispatcher.BeginInvoke (the design host connection delivers answers that way).
    /// </summary>
    internal static class DispatcherWait
    {
        public static void Wait(Task task, TimeSpan timeout)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = timeout };
                timer.Tick += (s, e) => frame.Continue = false;
                _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();
            }

            if (!task.IsCompleted)
                throw new TimeoutException("The RadSharp design host did not answer within " + timeout.TotalSeconds + " s.");
            // The task has completed: this only rethrows its exception, it never blocks.
#pragma warning disable VSTHRD002
            task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
    }
}
