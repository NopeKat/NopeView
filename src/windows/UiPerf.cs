using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NopeView.App;

/// <summary>
/// Opt-in timing record for work that runs on the UI thread. Sampling stays
/// off unless NOPEVIEW_UI_PERF is set to 1, so a normal run pays nothing; when
/// it is on, samples are appended to %LOCALAPPDATA%\NopeView\ui-perf.log.
/// </summary>
internal static class UiPerf
{
    private const string EnvironmentName = "NOPEVIEW_UI_PERF";
    private const string FolderName = "NopeView";
    private const string FileName = "ui-perf.log";

    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable(EnvironmentName) == "1";

    // Samples are buffered on the UI thread and handed to one writer that
    // drains them off the UI thread, because the recorder is called from input
    // handlers. Eight samples or two seconds, whichever comes first, keeps the
    // file readable without turning timing into logging work.
    private static readonly object Gate = new();
    private static readonly List<string> Samples = new();
    private static readonly Stopwatch SinceFlush = Stopwatch.StartNew();

    // Writes are chained so batches reach the file in the order they were
    // recorded. Guarded by Gate.
    private static Task WriteTail = Task.CompletedTask;

    internal static void Record(string name, TimeSpan elapsed, string detail)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            Samples.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1:0.000} ms {2}",
                name,
                elapsed.TotalMilliseconds,
                detail));

            if (Samples.Count >= 8 || SinceFlush.ElapsedMilliseconds >= 2000)
            {
                QueueWrite();
            }
        }
    }

    /// <summary>
    /// Formats a detail value the way the samples are read back, so a desktop
    /// whose decimal separator is a comma does not write a different log.
    /// </summary>
    internal static string Number(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Starts a measurement, or returns null when sampling is off so a handler
    /// on the input path pays nothing in a normal run.
    /// </summary>
    internal static Stopwatch? Start()
    {
        return Enabled ? Stopwatch.StartNew() : null;
    }

    internal static void Stop(Stopwatch? clock, string name, string detail)
    {
        if (clock is null)
        {
            return;
        }

        clock.Stop();
        Record(name, clock.Elapsed, detail);
    }

    /// <summary>
    /// Queues what is still buffered and waits briefly for the writer, so the
    /// last samples survive the exit. Only the close path calls this, where a
    /// short block is acceptable.
    /// </summary>
    internal static void Flush()
    {
        if (!Enabled)
        {
            return;
        }

        Task tail;
        lock (Gate)
        {
            QueueWrite();
            tail = WriteTail;
        }

        try
        {
            tail.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UiPerf: the writer did not finish: {ex.Message}");
        }
    }

    // Called with Gate held. The batch is built here, where the samples live,
    // and reaches the disk from a worker thread.
    private static void QueueWrite()
    {
        SinceFlush.Restart();
        if (Samples.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var sample in Samples)
        {
            builder.AppendLine(sample);
        }
        Samples.Clear();

        var text = builder.ToString();
        var previous = WriteTail;
        WriteTail = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // A failed batch must not stop the batches behind it.
            }

            Append(text);
        });
    }

    private static void Append(string text)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FolderName);
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, FileName), text, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Timing must never change what the app does.
            Debug.WriteLine($"UiPerf: could not write the samples: {ex.Message}");
        }
    }
}
