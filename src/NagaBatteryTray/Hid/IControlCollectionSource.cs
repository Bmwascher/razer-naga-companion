using System.Collections.Generic;

namespace NagaBatteryTray.Hid;

/// <summary>Finds and opens control collections. The real implementation enumerates with HidSharp
/// and opens with CreateFile; tests substitute a fake so the selection logic runs with no hardware.</summary>
internal interface IControlCollectionSource
{
    /// <summary>Open each candidate collection exposing the 91-byte feature report, in the given PID
    /// order. Lazy: the caller stops at the first that verifies, so later candidates are never opened.
    /// A candidate that cannot be opened is skipped, not yielded.</summary>
    IEnumerable<IControlCollection> Open(int[] pids);
}
