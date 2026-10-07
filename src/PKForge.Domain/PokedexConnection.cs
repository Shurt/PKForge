using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PKForge.Domain;

public static class PokedexConnectionProtocol
{
    public const int CurrentVersion = 1;
    public const string AppliedStatus = "applied";
    public const string DuplicateStatus = "duplicate";
    public const string DefaultServerUrl = "https://pokedex.thren.dev/";
    public const int MaxObservationsPerRequest = 20_000;

    public static string NormalizeServerUrl(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) ||
            !Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("The Pokédex server URL must be an absolute HTTPS URL.", nameof(serverUrl));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The Pokédex server URL cannot contain credentials.", nameof(serverUrl));
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The Pokédex server URL cannot contain a query or fragment.", nameof(serverUrl));

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";
        return builder.Uri.AbsoluteUri;
    }

    public static void ValidateRequest(PokedexSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != CurrentVersion)
            throw new ArgumentException($"PKForge supports Pokédex protocol version {CurrentVersion}.", nameof(request));
        if (!Guid.TryParse(request.DeviceId, out _))
            throw new ArgumentException("The Pokédex device ID must be a GUID.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DeviceName))
            throw new ArgumentException("The Pokédex device name is required.", nameof(request));
        if (request.Sequence < 1)
            throw new ArgumentException("The Pokédex sync sequence must be positive.", nameof(request));
        if (request.ObservedAt == default)
            throw new ArgumentException("The Pokédex observation time is required.", nameof(request));
        if (request.ObservedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("The Pokédex observation time must use UTC.", nameof(request));
        if (request.Sources is null)
            throw new ArgumentException("The Pokédex source list is required.", nameof(request));
        if (request.DeviceName.Length > 100)
            throw new ArgumentException("The Pokédex device name cannot exceed 100 characters.", nameof(request));
        if (request.Sources.Count > 1_000)
            throw new ArgumentException("A Pokédex sync cannot contain more than 1,000 sources.", nameof(request));

        var observationCount = 0L;
        foreach (var source in request.Sources)
        {
            if (source?.Observations is null)
                continue;
            observationCount += source.Observations.Count;
            if (observationCount > MaxObservationsPerRequest)
            {
                throw new ArgumentException(
                    $"A Pokédex sync cannot contain more than {MaxObservationsPerRequest:N0} observations.",
                    nameof(request));
            }
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.SourceId) || source.SourceId.Length > 200 ||
                string.IsNullOrWhiteSpace(source.Label) || source.Label.Length > 200 ||
                string.IsNullOrWhiteSpace(source.Kind) || source.Kind.Length > 200 || source.Observations is null)
            {
                throw new ArgumentException("The Pokédex request contains an incomplete source.", nameof(request));
            }
            if (!sourceIds.Add(source.SourceId))
                throw new ArgumentException($"The Pokédex request contains duplicate source ID '{source.SourceId}'.", nameof(request));
            if (source.Observations.Count > 10_000)
                throw new ArgumentException("A Pokédex source cannot contain more than 10,000 observations.", nameof(request));

            var slotIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in source.Observations)
            {
                if (observation is null || string.IsNullOrWhiteSpace(observation.SlotId) || observation.SlotId.Length > 200 ||
                    string.IsNullOrWhiteSpace(observation.Format) || observation.Format.Length > 50 ||
                    observation.Nickname?.Length > 200)
                {
                    throw new ArgumentException("The Pokédex request contains an incomplete observation.", nameof(request));
                }
                if (!slotIds.Add(observation.SlotId))
                {
                    throw new ArgumentException(
                        $"Pokédex source '{source.SourceId}' contains duplicate slot ID '{observation.SlotId}'.",
                        nameof(request));
                }
            }
        }
    }

    public static void ValidateResponse(PokedexSyncResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.ProtocolVersion != CurrentVersion)
        {
            throw new PokedexProtocolException(
                $"The Pokédex server returned protocol version {response.ProtocolVersion}; PKForge supports version {CurrentVersion}.");
        }
        if (!string.Equals(response.Status, AppliedStatus, StringComparison.Ordinal) &&
            !string.Equals(response.Status, DuplicateStatus, StringComparison.Ordinal))
            throw new PokedexProtocolException($"The Pokédex server returned an unknown status: {response.Status ?? "<missing>"}.");
        if (response.ReceivedAt == default)
            throw new PokedexProtocolException("The Pokédex response is missing receivedAt.");
        if (response.ReceivedAt.Offset != TimeSpan.Zero)
            throw new PokedexProtocolException("The Pokédex response receivedAt must use UTC.");
        if (response.Mapping is null)
            throw new PokedexProtocolException("The Pokédex response is missing mapping.");
        if (response.Checklist is null)
            throw new PokedexProtocolException("The Pokédex response is missing checklist.");

        foreach (var mapping in response.Mapping)
        {
            if (mapping is null || string.IsNullOrWhiteSpace(mapping.SourceId) || string.IsNullOrWhiteSpace(mapping.SlotId) ||
                mapping.DexEntryIds is null || string.IsNullOrWhiteSpace(mapping.MappingStatus))
            {
                throw new PokedexProtocolException("The Pokédex response contains an incomplete mapping entry.");
            }
            switch (mapping.MappingStatus)
            {
                case "matched" when mapping.DexEntryIds.Count > 0 && mapping.MappingReason is null:
                case "unmapped" when mapping.DexEntryIds.Count == 0 && !string.IsNullOrWhiteSpace(mapping.MappingReason):
                case "ambiguous" when !string.IsNullOrWhiteSpace(mapping.MappingReason):
                    break;
                default:
                    throw new PokedexProtocolException("The Pokédex response contains an invalid mapping result.");
            }
        }

        foreach (var entry in response.Checklist)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.DexEntryId) || string.IsNullOrWhiteSpace(entry.DisplayName))
                throw new PokedexProtocolException("The Pokédex response contains an incomplete checklist entry.");
        }

        if (response.BankPlan is null || response.BankPlan.Targets is null)
            throw new PokedexProtocolException("The Pokédex response contains an incomplete bank plan.");
        foreach (var target in response.BankPlan.Targets)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(target.Title) ||
                string.IsNullOrWhiteSpace(target.Source) || string.IsNullOrWhiteSpace(target.GameProgress) ||
                target.Progress is null)
            {
                throw new PokedexProtocolException("The Pokédex response contains an incomplete bank plan target.");
            }

            if (target.Preparations is not null)
            {
                var preparationIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var preparation in target.Preparations)
                {
                    if (preparation is null || string.IsNullOrWhiteSpace(preparation.Id) ||
                        string.IsNullOrWhiteSpace(preparation.Label) || string.IsNullOrWhiteSpace(preparation.Detail) ||
                        !preparationIds.Add(preparation.Id))
                    {
                        throw new PokedexProtocolException("The Pokédex response contains an invalid bank plan preparation.");
                    }
                }
            }

            if (target.Match is { } match &&
                (match.Species is null || match.Species.Count == 0 || match.Species.Any(species => species < 1) ||
                 match.Forms?.Any(form => form < 0) == true ||
                 match.Formats?.Any(string.IsNullOrWhiteSpace) == true))
            {
                throw new PokedexProtocolException("The Pokédex response contains an invalid bank plan match rule.");
            }

            if (target.Progress.Preparation?.Any(item => string.IsNullOrWhiteSpace(item.Key)) == true)
                throw new PokedexProtocolException("The Pokédex response contains an invalid bank plan preparation status.");

            if (target.WonderCards?.Any(reference => reference is null) == true)
                throw new PokedexProtocolException("The Pokédex response contains an invalid Wonder Card reference.");
        }
    }
}

public static class PokedexConnectionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            WriteIndented = true,
        };
        options.MakeReadOnly();
        return options;
    }
}

public sealed record PokedexSyncRequest(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("sources")] IReadOnlyList<PokedexSource> Sources);

public sealed record PokedexSource(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("observations")] IReadOnlyList<PokedexObservation> Observations);

public sealed record PokedexObservation(
    [property: JsonPropertyName("slotId")] string SlotId,
    [property: JsonPropertyName("species")] int Species,
    [property: JsonPropertyName("form")] int Form,
    [property: JsonPropertyName("gender")] int? Gender,
    [property: JsonPropertyName("formArgument")] int? FormArgument,
    [property: JsonPropertyName("canGigantamax")] bool? CanGigantamax,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("shiny")] bool Shiny,
    [property: JsonPropertyName("nickname")] string? Nickname,
    [property: JsonPropertyName("box")] int Box,
    [property: JsonPropertyName("slot")] int Slot);

public sealed record PokedexSyncResponse(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("mapping")] IReadOnlyList<PokedexMapping> Mapping,
    [property: JsonPropertyName("checklist")] IReadOnlyList<PokedexChecklistEntry> Checklist,
    [property: JsonPropertyName("bankPlan")] PokedexBankPlan BankPlan);

public sealed record PokedexMapping(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("slotId")] string SlotId,
    [property: JsonPropertyName("dexEntryIds")] IReadOnlyList<string> DexEntryIds,
    [property: JsonPropertyName("mappingStatus")] string MappingStatus,
    [property: JsonPropertyName("mappingReason")] string? MappingReason,
    [property: JsonPropertyName("sourceLabel")] string? SourceLabel = null);

public sealed record PokedexChecklistEntry(
    [property: JsonPropertyName("dexEntryId")] string DexEntryId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("normalOwned")] bool NormalOwned,
    [property: JsonPropertyName("shinyOwned")] bool ShinyOwned,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record PokedexBankPlan(
    [property: JsonPropertyName("targets")] IReadOnlyList<PokedexBankPlanTarget> Targets);

public sealed record PokedexBankPlanTarget(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("gameProgress")] string GameProgress,
    [property: JsonPropertyName("progress")] PokedexBankPlanProgress Progress)
{
    [JsonPropertyName("preparations")]
    public IReadOnlyList<PokedexBankPlanPreparation>? Preparations { get; init; }

    [JsonPropertyName("match")]
    public PokedexBankPlanMatch? Match { get; init; }

    // Older cached plans predate the curated Wonder Card references. A new sync fills these in.
    [JsonPropertyName("wonderCards")]
    public IReadOnlyList<PokedexWonderCardReference>? WonderCards { get; init; }
}

public sealed record PokedexBankPlanPreparation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record PokedexBankPlanMatch(
    [property: JsonPropertyName("species")] IReadOnlyList<int>? Species = null,
    [property: JsonPropertyName("forms")] IReadOnlyList<int>? Forms = null,
    [property: JsonPropertyName("shiny")] bool? Shiny = null,
    [property: JsonPropertyName("formats")] IReadOnlyList<string>? Formats = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public sealed record PokedexWonderCardReference(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("games")] string Games,
    [property: JsonPropertyName("cardId")] string CardId)
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("searchTerm")]
    public string? SearchTerm { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; init; }
}

public sealed record PokedexBankPlanProgress(
    [property: JsonPropertyName("acquired")] bool Acquired,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("inBank")] bool InBank,
    [property: JsonPropertyName("homeVerified")] bool HomeVerified,
    [property: JsonPropertyName("notes")] string? Notes)
{
    // This remains nullable so caches written before preparation tracking still load.
    [JsonPropertyName("preparation")]
    public IReadOnlyDictionary<string, bool>? Preparation { get; init; }
}

public sealed record PokedexInventorySource(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("lastAttemptComplete")] bool LastAttemptComplete,
    [property: JsonPropertyName("lastAttemptAt")] DateTimeOffset LastAttemptAt,
    [property: JsonPropertyName("observations")] IReadOnlyList<PokedexInventoryObservation> Observations);

public sealed record PokedexInventoryObservation(
    [property: JsonPropertyName("observation")] PokedexObservation Observation,
    [property: JsonPropertyName("dexEntryIds")] IReadOnlyList<string> DexEntryIds,
    [property: JsonPropertyName("mappingStatus")] string MappingStatus,
    [property: JsonPropertyName("mappingReason")] string? MappingReason);

public sealed record PokedexConnectionState
{
    public bool Enabled { get; init; }
    public string ServerUrl { get; init; } = PokedexConnectionProtocol.DefaultServerUrl;
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("D");
    public long LastSequence { get; init; }
    public PokedexSyncRequest? Pending { get; init; }
    public PokedexSyncResponse? Cache { get; init; }
    public IReadOnlyList<PokedexInventorySource> InventorySources { get; init; } = [];
    public DateTimeOffset? LastSyncedAt { get; init; }
    public string? LastError { get; init; }

    public PokedexConnectionState QueueSnapshot(
        string deviceName,
        DateTimeOffset observedAt,
        IReadOnlyList<PokedexSource> sources)
    {
        if (Pending is not null)
            return this;
        if (LastSequence == long.MaxValue)
            throw new InvalidOperationException("The Pokédex sync sequence cannot be incremented.");
        if (!Guid.TryParse(DeviceId, out _))
            throw new InvalidOperationException("The Pokédex device ID is invalid.");
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("The Pokédex device name is required.", nameof(deviceName));
        if (observedAt == default)
            throw new ArgumentException("The Pokédex observation time is required.", nameof(observedAt));
        ArgumentNullException.ThrowIfNull(sources);

        var snapshot = sources.Select(source => source with
        {
            Observations = source.Observations?.ToArray()
                ?? throw new ArgumentException("Each Pokédex source needs an observation list.", nameof(sources)),
        }).ToArray();
        var nextSequence = LastSequence + 1;
        var request = new PokedexSyncRequest(
            PokedexConnectionProtocol.CurrentVersion,
            DeviceId,
            deviceName.Trim(),
            nextSequence,
            observedAt.ToUniversalTime(),
            snapshot);
        PokedexConnectionProtocol.ValidateRequest(request);

        return this with
        {
            LastSequence = nextSequence,
            Pending = request,
            LastError = null,
        };
    }

    public PokedexConnectionState Accept(PokedexSyncResponse response, DateTimeOffset? syncedAt = null)
    {
        PokedexConnectionProtocol.ValidateResponse(response);
        var completedAt = (syncedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var inventory = MergeInventorySources(InventorySources, Pending, response);
        return this with
        {
            Pending = null,
            Cache = response,
            InventorySources = inventory,
            LastSyncedAt = completedAt,
            LastError = null,
        };
    }

    public PokedexConnectionState RecordFailure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("The Pokédex sync error is required.", nameof(error));
        return this with { LastError = error.Trim() };
    }

    public PokedexConnectionState ChangeServerUrl(string serverUrl)
    {
        var normalized = PokedexConnectionProtocol.NormalizeServerUrl(serverUrl);
        if (string.Equals(normalized, ServerUrl, StringComparison.Ordinal))
            return this with { ServerUrl = normalized };

        return this with
        {
            ServerUrl = normalized,
            DeviceId = Guid.NewGuid().ToString("D"),
            LastSequence = 0,
            Pending = null,
            Cache = null,
            InventorySources = [],
            LastSyncedAt = null,
            LastError = null,
        };
    }

    private static IReadOnlyList<PokedexInventorySource> MergeInventorySources(
        IReadOnlyList<PokedexInventorySource>? current,
        PokedexSyncRequest? request,
        PokedexSyncResponse response)
    {
        if (request is null)
            return current?.ToArray() ?? [];

        var bySource = (current ?? []).ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var mappings = response.Mapping
            .GroupBy(mapping => (mapping.SourceId, mapping.SlotId))
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var source in request.Sources)
        {
            if (!source.Complete)
            {
                if (bySource.TryGetValue(source.SourceId, out var existing))
                {
                    bySource[source.SourceId] = existing with
                    {
                        Label = source.Label,
                        Kind = source.Kind,
                        LastAttemptComplete = false,
                        LastAttemptAt = request.ObservedAt,
                    };
                }
                continue;
            }

            var observations = source.Observations.Select(observation =>
            {
                if (!mappings.TryGetValue((source.SourceId, observation.SlotId), out var mapping))
                {
                    return new PokedexInventoryObservation(
                        observation,
                        [],
                        "unmapped",
                        "The Pokédex response did not include a mapping for this observation.");
                }

                return new PokedexInventoryObservation(
                    observation,
                    mapping.DexEntryIds.ToArray(),
                    mapping.MappingStatus,
                    mapping.MappingReason);
            }).ToArray();

            bySource[source.SourceId] = new PokedexInventorySource(
                source.SourceId,
                source.Label,
                source.Kind,
                request.ObservedAt,
                response.ReceivedAt,
                true,
                request.ObservedAt,
                observations);
        }

        return bySource.Values
            .OrderBy(source => source.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.SourceId, StringComparer.Ordinal)
            .ToArray();
    }
}

public class PokedexProtocolException : Exception
{
    public PokedexProtocolException(string message) : base(message)
    {
    }

    public PokedexProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
