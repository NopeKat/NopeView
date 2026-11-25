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

    // Samples are buffered and written in one go, because the writer itself
    // runs on the UI thread. Eight samples or two seconds, whichever comes
    // first, keeps the file readable without turning timing into logging work.
    private static readonly object Gate = new();
    private static readonly List<string> Samples = new();
    private static readonly Stopwatch SinceFlush = Stopwatch.StartNew();

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
                WriteSamples();
            }
        }
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

    internal static void Flush()
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            WriteSamples();
        }
    }

    private static void WriteSamples()
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

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FolderName);
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, FileName), builder.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Timing must never change what the app does.
            Debug.WriteLine($"UiPerf: could not write the samples: {ex.Message}");
        }
    }
}
