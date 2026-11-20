using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Collection;

public interface IProcessInspector
{
    InspectResult Inspect(ProcessKey key);
}
