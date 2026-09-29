using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class CollectionLocationTests
{
    private static readonly CollectionLocation[] Locations =
    [
        new(25, 1, true, "White", "Kyle", 2, 4, "Sparky"),
        new(1, 0, false, "Bank", "Local", 0, 3),
        new(25, 0, false, "White", "Kyle", -1, 1),
        new(25, 0, true, "Bank", "Local", 1, 2),
        new(25, 0, true, "Bank", "Local", 1, 2),
        new(25, 1, false, "Bank", "Archive", 4, 5),
        new(25, 1, false, "Black", "Nate", 0, 0),
    ];

    [Fact]
    public void FindFiltersSpeciesAndOrdersStorageLocations()
    {
        var matches = CollectionLocations.Find(Locations, 25);

        Assert.Equal(6, matches.Count);
        Assert.Equal(
        [
            ("Bank", "Archive", 4, 5),
            ("Bank", "Local", 1, 2),
            ("Bank", "Local", 1, 2),
            ("Black", "Nate", 0, 0),
            ("White", "Kyle", -1, 1),
            ("White", "Kyle", 2, 4),
        ], matches.Select(location => (location.SourceLabel, location.SourceDetail, location.Box, location.Slot)));
    }

    [Fact]
    public void FindTreatsBaseFormAsAnExactFilter()
    {
        var matches = CollectionLocations.Find(Locations, 25, form: 0);

        Assert.Equal(3, matches.Count);
        Assert.All(matches, location => Assert.Equal(0, location.Form));
    }

    [Fact]
    public void FindCanKeepOnlyShinyCopiesWithoutCollapsingDuplicates()
    {
        var matches = CollectionLocations.Find(Locations, 25, shinyOnly: true);

        Assert.Equal(3, matches.Count);
        Assert.All(matches, location => Assert.True(location.Shiny));
        Assert.Equal(2, matches.Count(location => location.SourceLabel == "Bank" && location.Box == 1 && location.Slot == 2));
    }

    [Theory]
    [InlineData(-1, 0, "Party / slot 1")]
    [InlineData(0, 0, "Box 1 / slot 1")]
    [InlineData(2, 4, "Box 3 / slot 5")]
    public void PositionLabelUsesOneBasedDisplayIndexes(int box, int slot, string expected)
    {
        var location = new CollectionLocation(25, 0, false, "White", "Kyle", box, slot);

        Assert.Equal(expected, location.PositionLabel);
    }
}
