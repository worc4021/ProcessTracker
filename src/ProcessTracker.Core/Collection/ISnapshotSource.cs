using ProcessTracker.Core.Model;

namespace ProcessTracker.Core.Collection;

public interface ISnapshotSource
{
    Snapshot Take();
}
