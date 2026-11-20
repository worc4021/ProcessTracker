namespace ProcessTracker.Core.Load;

/// <summary>Tunables for <see cref="LoadAssessor"/>.</summary>
public sealed record LoadOptions(double PerTaskGb = 2.0, int TopSessions = 5);
