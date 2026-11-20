namespace ProcessTracker.Core.Model;

/// <summary>Per-process sample. CpuCores is the average number of cores used (2.5 = two and a half cores).</summary>
public readonly record struct ProcessSample(
    ProcessKey Key, DateTime TimestampUtc, float CpuCores,
    long PrivateBytes, long WorkingSet,
    long ReadOpsDelta, long WriteOpsDelta, long OtherOpsDelta,
    long ReadBytesDelta, long WriteBytesDelta, long OtherBytesDelta,
    long ContextSwitchesDelta, int HandleCount, int ThreadCount);
