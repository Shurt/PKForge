using System.Text.Json;
using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class BankPlanWonderCardTests
{
    private const string RandomDiancie = "Wondercards/ENG/0525 XY - OCT2014 Diancie (ENG) (Random IVs).wc6";
    private const string SetDiancie = "Wondercards/ENG/0525 XY - OCT2014 Diancie (ENG) (3 Set IVs).wc6";

    [Fact]
    public void BankPlanMatchesTheExactReferencedVariantBeforeCollapsing()
    {
        var profile = new EventGiftSaveProfile(6, 2, "Pokemon X");
        var entries = Entries(profile,
            Gift(0, RandomDiancie, 525, 719, 6, 2),
            Gift(1, SetDiancie, 525, 719, 6, 2));
        var plan = Plan([
            new PokedexWonderCardReference(
                "Released\\Gen 6\\Wondercards\\ENG\\0525 XY - OCT2014 Diancie (ENG) (3 Set IVs).wc6",
                "Pokémon X / Pokémon Y",
                "0525"),
        ]);

        var page = WonderCardAlbum.Query(entries, new WonderCardQuery { BankPlanOnly = true }, profile, plan);

        var selected = Assert.Single(page.Items);
        Assert.Equal(1, selected.Gift.Id);
        Assert.Equal(1, Assert.Single(selected.Variants).Gift.Id);
    }

    [Fact]
    public void BankPlanReferenceRequiresBothTheSourceAndNumericCardId()
    {
        var profile = new EventGiftSaveProfile(6, 2, "X");
        var entries = Entries(profile,
            Gift(0, RandomDiancie, 526, 719, 6, 2),
            Gift(1, RandomDiancie.Replace("Random IVs", "Wrong release"), 525, 719, 6, 2),
            Gift(2, RandomDiancie, 525, 719, 6, 2));
        var plan = Plan([new PokedexWonderCardReference(
            $"Released/Gen 6/{RandomDiancie}", "X / Y", "525")]);

        var page = WonderCardAlbum.Query(entries, new WonderCardQuery { BankPlanOnly = true }, profile, plan);

        Assert.Equal(2, Assert.Single(page.Items).Gift.Id);
    }

    [Fact]
    public void ItemPrerequisiteIsIncludedEvenWhenTheTargetNamesAPokemon()
    {
        const string memberCard = "Wondercards/ENG/018 Pt - Item Member Card (ENG).wc4";
        var profile = new EventGiftSaveProfile(4, 2, "Pokémon Platinum");
        var entries = Entries(profile, Gift(0, memberCard, 18, kind: EventGiftKind.Item, generation: 4, language: 2));
        var plan = Plan(
            [new PokedexWonderCardReference($"Released/Gen 4/{memberCard}", "Platinum", "018")],
            title: "Pokémon Darkrai");

        var page = WonderCardAlbum.Query(entries, new WonderCardQuery { BankPlanOnly = true }, profile, plan);

        Assert.Equal(EventGiftKind.Item, Assert.Single(page.Items).Gift.Kind);
    }

    [Theory]
    [InlineData("Diamond / Pearl / Platinum, then trade to HGSS", "Pokemon Pearl", true)]
    [InlineData("Diamond / Pearl / Platinum, then trade to HGSS", "Pokémon HeartGold", false)]
    [InlineData("Diamond / Pearl / Platinum", "Brilliant Pearl", false)]
    [InlineData("Pokémon X / Pokémon Y", "Omega Ruby", false)]
    [InlineData("Pokémon Black / Pokémon White", "Pokemon Black 2", false)]
    public void ReceivingGameUsesExactSlashSeparatedNamesBeforeTheComma(
        string games, string gameLabel, bool expected)
    {
        const string source = "Wondercards/ENG/0001 TEST - Target (ENG).wc6";
        var generation = games.Contains("Black", StringComparison.Ordinal) ? 5
            : games.Contains('X', StringComparison.Ordinal) ? 6 : 4;
        var profile = new EventGiftSaveProfile(generation, 2, gameLabel);
        var entries = Entries(profile, Gift(0, source, 1, generation: generation, language: 2));
        var plan = Plan([new PokedexWonderCardReference($"Released/Gen {generation}/{source}", games, "1")]);

        var page = WonderCardAlbum.Query(entries, new WonderCardQuery { BankPlanOnly = true }, profile, plan);

        Assert.Equal(expected, page.Items.Count == 1);
    }

    [Fact]
    public void BankPlanFilterAlwaysAppliesSaveCompatibility()
    {
        const string english = "Wondercards/ENG/0001 XY - Target (ENG).wc6";
        const string japanese = "Wondercards/JPN/0002 XY - Target (JPN).wc6";
        const string oldGeneration = "Wondercards/ENG/0003 XY - Target (ENG).wc6";
        var profile = new EventGiftSaveProfile(6, 2, "X");
        var entries = Entries(profile,
            Gift(0, english, 1, generation: 6, language: 2),
            Gift(1, japanese, 2, generation: 6, language: 1),
            Gift(2, oldGeneration, 3, generation: 5, language: 2));
        var plan = Plan([
            Reference(6, english, "1", "X / Y"),
            Reference(6, japanese, "2", "X / Y"),
            Reference(6, oldGeneration, "3", "X / Y"),
        ]);

        var page = WonderCardAlbum.Query(entries, new WonderCardQuery { BankPlanOnly = true }, profile, plan);

        Assert.Equal(0, Assert.Single(page.Items).Gift.Id);
    }

    [Fact]
    public void AcquiredProgressDoesNotRemoveAReferencedCard()
    {
        var profile = new EventGiftSaveProfile(6, 2, "X");
        var entries = Entries(profile, Gift(0, RandomDiancie, 525, 719, 6, 2));
        var plan = Plan(
            [Reference(6, RandomDiancie, "525", "X / Y")],
            acquired: true);

        var page = WonderCardAlbum.Query(entries, new WonderCardQuery { BankPlanOnly = true }, profile, plan);

        Assert.Single(page.Items);
    }

    [Fact]
    public void EnabledFilterNeedsAProfilePlanAndWonderCardReferences()
    {
        var profile = new EventGiftSaveProfile(6, 2, "X");
        var entries = Entries(profile, Gift(0, RandomDiancie, 525, 719, 6, 2));
        var query = new WonderCardQuery { BankPlanOnly = true };
        var oldPlan = Plan(null);

        Assert.Empty(WonderCardAlbum.Query(entries, query, null, Plan([Reference(6, RandomDiancie, "525", "X")])).Items);
        Assert.Empty(WonderCardAlbum.Query(entries, query, profile, null).Items);
        Assert.Empty(WonderCardAlbum.Query(entries, query, profile, oldPlan).Items);
        Assert.Equal(1, query.ActiveFilterCount);
    }

    [Fact]
    public void DisabledFilterDoesNotNeedBankPlanData()
    {
        var entries = Entries(null,
            Gift(0, RandomDiancie, 525, 719, 6, 2),
            Gift(1, SetDiancie, 525, 719, 6, 2));

        var page = WonderCardAlbum.Query(
            entries,
            new WonderCardQuery { BankPlanOnly = false, OnePerEvent = false },
            null,
            null);

        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public void BankPlanFilterCombinesWithOrdinaryFiltersAndAClearQueryRemovesIt()
    {
        const string memberCard = "Wondercards/ENG/018 Pt - Item Member Card (ENG).wc4";
        const string shaymin = "Wondercards/ENG/019 Pt - Oak's Letter Shaymin (ENG).wc4";
        var profile = new EventGiftSaveProfile(4, 2, "Platinum");
        var entries = Entries(profile,
            Gift(0, memberCard, 18, kind: EventGiftKind.Item, generation: 4, language: 2),
            Gift(1, shaymin, 19, species: 492, generation: 4, language: 2));
        var plan = Plan([
            Reference(4, memberCard, "018", "Platinum"),
            Reference(4, shaymin, "019", "Platinum"),
        ]);
        var filtered = new WonderCardQuery
        {
            BankPlanOnly = true,
            Kind = WonderCardKindFilter.Pokemon,
            Grouping = WonderCardGrouping.Games,
        };

        var page = WonderCardAlbum.Query(entries, filtered, profile, plan);
        Assert.Equal(1, Assert.Single(page.Items).Gift.Id);
        Assert.Equal(2, filtered.ActiveFilterCount);

        var cleared = new WonderCardQuery
        {
            Sort = filtered.Sort,
            Grouping = filtered.Grouping,
            OnePerEvent = filtered.OnePerEvent,
        };
        Assert.False(cleared.BankPlanOnly);
        Assert.Equal(0, cleared.ActiveFilterCount);
        Assert.Equal(2, WonderCardAlbum.Query(entries, cleared, profile, null).Items.Count);
    }

    [Fact]
    public void MissingSourceMalformedCardIdAndWrongPathGenerationDoNotMatch()
    {
        var profile = new EventGiftSaveProfile(6, 2, "X");
        var withoutSource = Gift(0, SetDiancie, 525, 719, 6, 2) with
        {
            Details = new EventGiftDetails(),
        };
        var entries = Entries(profile,
            withoutSource,
            Gift(1, SetDiancie, 525, 719, 6, 2));
        var plan = Plan([
            new PokedexWonderCardReference($"Released/Gen 6/{SetDiancie}", "X / Y", "not-a-number"),
            new PokedexWonderCardReference($"Released/Gen 5/{SetDiancie}", "X / Y", "525"),
        ]);

        var page = WonderCardAlbum.Query(entries, new WonderCardQuery { BankPlanOnly = true }, profile, plan);

        Assert.Empty(page.Items);
    }

    [Fact]
    public void SyncCacheRoundTripsWonderCardReferences()
    {
        var reference = Reference(6, RandomDiancie, "0525", "Pokémon X / Pokémon Y");
        var response = new PokedexSyncResponse(
            PokedexConnectionProtocol.CurrentVersion,
            PokedexConnectionProtocol.AppliedStatus,
            DateTimeOffset.Parse("2026-10-02T12:00:00Z"),
            [],
            [],
            Plan([reference]));
        var state = new PokedexConnectionState { Cache = response };

        var json = JsonSerializer.Serialize(state, PokedexConnectionJson.Options);
        var restored = JsonSerializer.Deserialize<PokedexConnectionState>(json, PokedexConnectionJson.Options);

        var restoredReference = Assert.Single(Assert.Single(restored!.Cache!.BankPlan.Targets).WonderCards!);
        Assert.Equal(reference, restoredReference);
    }

    [Fact]
    public void OlderCachedTargetWithoutWonderCardsStillDeserializes()
    {
        const string json = """
            {
              "id": "diancie",
              "title": "Pokémon Diancie",
              "source": "X or Y",
              "gameProgress": "Ready",
              "progress": {
                "acquired": false,
                "archived": false,
                "inBank": false,
                "homeVerified": false,
                "notes": null
              }
            }
            """;

        var target = JsonSerializer.Deserialize<PokedexBankPlanTarget>(json, PokedexConnectionJson.Options);

        Assert.NotNull(target);
        Assert.Null(target.WonderCards);
    }

    private static PokedexWonderCardReference Reference(int generation, string source, string cardId, string games) =>
        new($"Released/Gen {generation}/{source}", games, cardId);

    private static PokedexBankPlan Plan(
        IReadOnlyList<PokedexWonderCardReference>? references,
        string title = "Pokémon Diancie",
        bool acquired = false) =>
        new([
            new PokedexBankPlanTarget(
                "diancie",
                title,
                "X or Y",
                "Ready",
                new PokedexBankPlanProgress(acquired, false, false, false, null))
            {
                WonderCards = references,
            },
        ]);

    private static IReadOnlyList<WonderCardEntry> Entries(EventGiftSaveProfile? profile, params EventGift[] gifts) =>
        WonderCardAlbum.CreateEntries(gifts, species => species == 719 ? "Diancie" : $"#{species}", profile, _ => false);

    private static EventGift Gift(
        int id,
        string source,
        int cardId,
        int species = 719,
        int generation = 6,
        int language = 2,
        EventGiftKind kind = EventGiftKind.Pokemon) =>
        new(
            id,
            kind == EventGiftKind.Item ? "Member Card" : "Diancie",
            $"Card #: {cardId:0000}",
            kind == EventGiftKind.Item ? 0 : species,
            50,
            false,
            cardId,
            generation,
            language,
            null,
            kind,
            new EventGiftDetails
            {
                SourceFile = source,
                Items = kind == EventGiftKind.Item ? [new EventGiftItem(0, "Member Card", 1)] : [],
            });
}
