using System.Diagnostics.Metrics;

namespace Chorus.Engine.Tests;

/// <summary>
/// Collects every measurement of one meter: values per instrument name, whether any sample
/// ever arrived carrying a label, and a frozen snapshot so assertions on separate instruments
/// cannot race a recording between two reads. Metric tests pass a private meter name so
/// parallel suites cannot bleed their samples in.
/// </summary>
internal sealed class MeterSamples : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, List<double>> _values = [];
    private int _tagged;

    public MeterSamples(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, _, tags) =>
        {
            lock (_gate)
            {
                if (tags is Array tagged && tagged.Length > 0) _tagged++;
                Values(instrument.Name).Add(value);
            }
        });
        _listener.Start();
    }

    public List<double> Values(string instrument)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(instrument, out var list))
                _values[instrument] = list = [];
            return list;
        }
    }

    public int Count(string instrument)
    {
        lock (_gate) return Values(instrument).Count;
    }

    /// <summary>Freezes every instrument's samples at one instant, so assertions on
    /// separate instruments cannot race a recording between two reads.</summary>
    public Dictionary<string, double[]> Snapshot()
    {
        lock (_gate) return _values.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    public int TaggedMeasurements { get { lock (_gate) return _tagged; } }

    public void Dispose() => _listener.Dispose();
}
