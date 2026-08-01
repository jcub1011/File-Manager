using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FileManager.MemoryProbe;

/// <summary>One memory reading of the service process, in bytes.</summary>
internal readonly record struct MemorySample(long PrivateBytes, long WorkingSetBytes, long PeakWorkingSetBytes)
{
    public static MemorySample Empty => new(0, 0, 0);
}

/// <summary>Polls the service process's memory on a background thread.
///
/// <para>Windows maintains a peak counter for the working set (<c>PeakWorkingSet64</c>, free and
/// race-free) but NOT for private commit — which is the number a user actually reports from Task
/// Manager. Sampling is the only way to get a high-water mark for that, and it is precisely why this
/// class exists rather than two reads either side of the run.</para>
///
/// <para>100 ms is fast enough that a multi-second dry run's peak is captured within a few percent,
/// and slow enough that the polling itself does not perturb the process being measured.</para></summary>
internal sealed class MemorySampler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly Process _process;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();

    private long _peakPrivate;
    private long _peakWorkingSet;

    public MemorySampler(Process process)
    {
        _process = process;
        _thread = new Thread(Loop) { IsBackground = true, Name = "memprobe-sampler" };
        _thread.Start();
    }

    /// <summary>The high-water private commit and working set observed so far.</summary>
    public (long Private, long WorkingSet) Peak
    {
        get
        {
            lock (_gate)
                return (_peakPrivate, _peakWorkingSet);
        }
    }

    /// <summary>A reading taken right now. Also folds into the peak, so an explicit sample can never
    /// report a value the peak does not cover.</summary>
    public MemorySample Sample()
    {
        try
        {
            _process.Refresh();
            if (_process.HasExited)
                return MemorySample.Empty;
            MemorySample sample = new(_process.PrivateMemorySize64, _process.WorkingSet64, _process.PeakWorkingSet64);
            Record(sample);
            return sample;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between the HasExited check and the read, or access was refused.
            // A missing sample must not take down the harness mid-measurement.
            return MemorySample.Empty;
        }
    }

    private void Loop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Sample();
                if (_stop.Token.WaitHandle.WaitOne(Interval))
                    return;
            }
        }
        catch (Exception ex)
        {
            // Last resort: this thread is unobserved, and a dead sampler that silently reported 0 MB
            // would look like a spectacular memory win rather than a broken harness.
            Console.Error.WriteLine($"memory sampler stopped unexpectedly: {ex}");
        }
    }

    private void Record(MemorySample sample)
    {
        lock (_gate)
        {
            if (sample.PrivateBytes > _peakPrivate)
                _peakPrivate = sample.PrivateBytes;
            if (sample.WorkingSetBytes > _peakWorkingSet)
                _peakWorkingSet = sample.WorkingSetBytes;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}

/// <summary>One reported row of the measurement.</summary>
internal sealed record PhaseReading(string Phase, long PrivateBytes, long WorkingSetBytes)
{
    public static IReadOnlyList<string> CsvHeader =>
        ["timestamp", "label", "phase", "private_mb", "working_set_mb"];

    public string ToCsvRow(string timestamp, string label) =>
        $"{timestamp},{label},{Phase},{PrivateBytes >> 20},{WorkingSetBytes >> 20}";
}
