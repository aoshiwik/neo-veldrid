using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace NeoVeldrid.Sdl2;

/// <summary>
/// Optional native-event boundary spans, on the same monotonic clock as the
/// caller's frame diagnostics. No clocks or payloads are read/built while
/// disabled. Counts describe delivered events, not elapsed CPU time.
/// </summary>
[EventSource(Name = "NeoVeldrid-SDL-Execution")]
public sealed class Sdl2EventPumpTrace : EventSource
{
    public static Sdl2EventPumpTrace Log { get; } = new();
    private Sdl2EventPumpTrace() { }

    [NonEvent]
    public Scope Measure(string stage) => IsEnabled() ? new Scope(stage) : default;

    [Event(1, Level = EventLevel.Informational)]
    public unsafe void Completed(string stage, long startQpc, long endQpc, int eventCount)
    {
        if (!IsEnabled()) return;
        ArgumentNullException.ThrowIfNull(stage);
        fixed (char* text = stage)
        {
            EventData* data = stackalloc EventData[4];
            data[0] = new() { DataPointer = (nint)text, Size = checked((stage.Length + 1) * sizeof(char)) };
            data[1] = new() { DataPointer = (nint)(&startQpc), Size = sizeof(long) };
            data[2] = new() { DataPointer = (nint)(&endQpc), Size = sizeof(long) };
            data[3] = new() { DataPointer = (nint)(&eventCount), Size = sizeof(int) };
            WriteEventCore(1, 4, data);
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly string stage;
        private readonly long start;
        internal Scope(string stage)
        {
            this.stage = stage;
            start = Stopwatch.GetTimestamp();
        }

        /// <summary>Completes a scope with a known count, instead of Dispose.</summary>
        public void Complete(int eventCount)
        {
            if (stage != null)
                Log.Completed(stage, start, Stopwatch.GetTimestamp(), eventCount);
        }

        public void Dispose() => Complete(-1);
    }
}
