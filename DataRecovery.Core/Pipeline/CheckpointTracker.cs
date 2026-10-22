namespace DataRecovery.Core.Pipeline;

/// <summary>
/// Because multiple carve-worker threads finish scanning chunks in a possibly different order than
/// they were produced, we can't just checkpoint "the reader's current position" - a chunk that's
/// still being scanned must not be skipped over on resume. This tracks exactly which prefix of
/// chunks (by sequence number) has been fully completed, so the checkpoint offset committed to disk
/// is always safe to resume from.
/// </summary>
internal sealed class CheckpointTracker
{
    private readonly object _lock = new();
    private readonly SortedSet<long> _completed = [];
    private readonly Dictionary<long, long> _endOffsets = [];
    private long _nextExpectedSequence;
    private long _safeOffset;

    public CheckpointTracker(long startingSequence, long startingOffset)
    {
        _nextExpectedSequence = startingSequence;
        _safeOffset = startingOffset;
    }

    public void MarkCompleted(long sequence, long endOffset)
    {
        lock (_lock)
        {
            _endOffsets[sequence] = endOffset;
            _completed.Add(sequence);

            while (_completed.Count > 0 && _completed.Min == _nextExpectedSequence)
            {
                _completed.Remove(_nextExpectedSequence);
                _safeOffset = _endOffsets[_nextExpectedSequence];
                _endOffsets.Remove(_nextExpectedSequence);
                _nextExpectedSequence++;
            }
        }
    }

    public long SafeOffset
    {
        get
        {
            lock (_lock)
            {
                return _safeOffset;
            }
        }
    }
}
