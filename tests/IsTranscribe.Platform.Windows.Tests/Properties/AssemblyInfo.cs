using Xunit;

// Windows capture/finalization tests share process-wide Media Foundation and COM resources.
// Serial execution keeps lifecycle timing assertions deterministic under the full suite.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
