// several test classes toggle the process-global DFLogBuffer.UseNativeScan
// flag around their runs; parallel collection execution races on it - see
// xunit.runner.json (parallelizeTestCollections: false)
