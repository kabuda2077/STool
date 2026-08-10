namespace STool.Modules.LanTransfer;

internal sealed class DownloadProgressTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DownloadState> _states = new(StringComparer.Ordinal);

    public string Begin(string fileId, string remoteAddress, long totalLength, long rangeStart)
    {
        var key = $"{fileId}|{remoteAddress}";
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state) ||
                state.TotalLength != totalLength ||
                (state.ActiveRequests == 0 && rangeStart == 0))
            {
                state = new DownloadState(totalLength);
                _states[key] = state;
            }

            state.ActiveRequests++;
        }
        return key;
    }

    public long Record(string key, long start, long length)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state) || length <= 0 || start >= state.TotalLength)
                return 0;

            start = Math.Max(0, start);
            var end = length >= state.TotalLength - start ? state.TotalLength : start + length;
            if (end <= start)
                return state.CoveredBytes;

            var mergedStart = start;
            var mergedEnd = end;
            var insertAt = 0;
            while (insertAt < state.Ranges.Count && state.Ranges[insertAt].End < mergedStart)
                insertAt++;

            while (insertAt < state.Ranges.Count && state.Ranges[insertAt].Start <= mergedEnd)
            {
                var existing = state.Ranges[insertAt];
                mergedStart = Math.Min(mergedStart, existing.Start);
                mergedEnd = Math.Max(mergedEnd, existing.End);
                state.CoveredBytes -= existing.End - existing.Start;
                state.Ranges.RemoveAt(insertAt);
            }

            state.Ranges.Insert(insertAt, new ByteRange(mergedStart, mergedEnd));
            state.CoveredBytes += mergedEnd - mergedStart;
            return state.CoveredBytes;
        }
    }

    public void End(string key)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var state) && state.ActiveRequests > 0)
                state.ActiveRequests--;
        }
    }

    public void Remove(string fileId)
    {
        var prefix = fileId + "|";
        lock (_gate)
        {
            foreach (var key in _states.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                _states.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _states.Clear();
    }

    private sealed class DownloadState(long totalLength)
    {
        public long TotalLength { get; } = totalLength;
        public List<ByteRange> Ranges { get; } = [];
        public long CoveredBytes { get; set; }
        public int ActiveRequests { get; set; }
    }

    private readonly record struct ByteRange(long Start, long End);
}
