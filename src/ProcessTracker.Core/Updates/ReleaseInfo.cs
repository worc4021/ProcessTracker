namespace ProcessTracker.Core.Updates;

/// <summary>A published GitHub release: its parsed version, the original tag and its downloadable assets.</summary>
/// <param name="Version">Version parsed from a <c>vX.Y.Z</c> tag.</param>
/// <param name="Tag">The release tag exactly as published.</param>
/// <param name="Assets">Asset file name to <c>browser_download_url</c>.</param>
public sealed record ReleaseInfo(Version Version, string Tag, IReadOnlyDictionary<string, Uri> Assets);
