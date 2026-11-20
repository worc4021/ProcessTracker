using ProcessTracker.Core.Model;

namespace ProcessTracker.Service;

/// <summary>Resolves which session (or app) made an HTTP request.</summary>
public interface ICallerResolver
{
    Attribution? Resolve(HttpContext ctx);
}

/// <summary>Placeholder until caller resolution exists: every caller is unknown.</summary>
public sealed class NullCallerResolver : ICallerResolver
{
    public Attribution? Resolve(HttpContext ctx) => null;
}
