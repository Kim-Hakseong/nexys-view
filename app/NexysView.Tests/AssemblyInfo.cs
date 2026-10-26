using Xunit;

// Gate tests measure time and memory: run test classes sequentially so they do not disturb each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
