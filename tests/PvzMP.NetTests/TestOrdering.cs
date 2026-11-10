using Xunit;

/// <summary>
/// TestLog is one global list because the mod's Log entry point is one global static: a test that
/// clears it erases the lines another test is still waiting for, and a test that reads it sees the
/// other test's lines. That made the suite fail at random - one run lost a transport log assertion,
/// the next lost an arbiter one - which is the last thing a room full of timing bugs needs. Running
/// the classes one after another is what makes a red run mean a real defect.
/// </summary>
[assembly: CollectionBehavior(DisableTestParallelization = true)]
