// The tests drive real windows through UI Automation; running them in parallel would let
// fixtures interfere with each other.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
