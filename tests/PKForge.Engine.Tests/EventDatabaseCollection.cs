using Xunit;

namespace PKForge.Engine.Tests;

// These tests replace PKHeX's process-wide event tables and the archive index.
// Exclude other collections too: legality and gift readers consume those tables.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EventDatabaseCollection
{
    public const string Name = "Mutable event database";
}
