using MissionPlanner.Utilities;
using Xunit;

// several test classes toggle the process-global DFLogBuffer.UseNativeScan
// flag around their runs; parallel collection execution races on it - see
// xunit.runner.json (parallelizeTestCollections: false)

[assembly: AssemblyFixture(typeof(MissionPlanner.Utilities.Tests.NativeFlagDefault))]

namespace MissionPlanner.Utilities.Tests
{
    /// <summary>
    /// Pins UseNativeScan to an explicit value before any test runs, so the
    /// lazy default resolver (which consults the machine-global Settings
    /// store) never executes inside the test process.
    /// </summary>
    public sealed class NativeFlagDefault
    {
        public NativeFlagDefault()
        {
            DFLogBuffer.UseNativeScan = false;
        }
    }
}
