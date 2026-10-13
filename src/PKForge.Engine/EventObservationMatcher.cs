using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Card compatibility is a candidate, never proof of a distribution's acquisition history.</summary>
public sealed class EventObservationMatcher
{
    private readonly EventDatabaseService.ArchiveIndex _archive = EventDatabaseService.Archive;
    private readonly IEventReceiptStore? _receipts;
    private readonly Dictionary<byte, EventDatabaseService.ArchiveCard[]> _cardsByGeneration;
    private readonly HashSet<string> _payloadKeys;
    public bool IsComplete => _archive.Complete && (_receipts?.IsAvailable ?? true);

    public EventObservationMatcher(IEventReceiptStore? receipts = null)
    {
        _receipts = receipts;
        _cardsByGeneration = _archive.Cards.GroupBy(card => card.Gift.Generation)
            .ToDictionary(group => group.Key, group => group.ToArray());
        _payloadKeys = _archive.Sources.Keys.Select(PayloadKey).ToHashSet(StringComparer.Ordinal);
    }

    public PokedexObservation Match(PKM entity, PokedexObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = EventEntityFingerprint.Create(entity);
        var complete = IsComplete;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var chains = new Dictionary<EntityContext, EvoCriteria[]>();
        var receipt = fingerprint is null ? null : _receipts?.Find(fingerprint);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (entity.Language == 0) complete = false;
            else if (entity.Language == (int)LanguageID.English && (entity.Generation is < 4 or > 9 || fingerprint is null)) complete = false;
            else if (entity.Language == (int)LanguageID.English)
            {
                // Enumerate every match, not LegalityAnalysis's final fallback encounter.
                // Origin-chain and match-rating checks below discard deferred/invalid fallbacks.
                var info = new LegalInfo(entity, []);
                foreach (var encounter in EncounterGenerator.GetEncounters(entity, info))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (encounter is MysteryGift gift) Add(gift);
                    if (paths.Count > EventObservationRules.MaxMatches) { complete = false; break; }
                }

                // PKHeX's encounter iterator can prune non-fateful distributions and later
                // alternatives. Check the indexed templates too so every compatible card survives.
                if (_cardsByGeneration.TryGetValue(entity.Generation, out var candidates))
                {
                    var ordered = receipt is null ? candidates.AsEnumerable()
                        : candidates.OrderByDescending(card => receipt.SourcePaths.Intersect(card.SourcePaths).Any());
                    foreach (var card in ordered)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (paths.Count > EventObservationRules.MaxMatches) { complete = false; break; }
                        Add(card.Gift);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Species/storage observations remain usable even when one event template cannot be read.
            complete = false;
        }

        var recorded = receipt is { Ambiguous: false, SourcePaths.Count: 1 }
            && paths.Contains(receipt.SourcePaths[0]) ? receipt.SourcePaths[0] : null;
        var matches = paths.OrderByDescending(path => path == recorded).ThenBy(path => path, StringComparer.Ordinal)
            .Take(EventObservationRules.MaxMatches).Select(path => new PokedexEventMatch(path, path == recorded ? "recorded" : "possible")).ToArray();
        return observation with
        {
            EntityFingerprint = fingerprint,
            EventMatches = matches,
            EventScanComplete = complete,
        };

        void Add(MysteryGift gift)
        {
            if (!gift.IsEntity || gift.Species == 0 || gift.Generation != entity.Generation
                || gift.FatefulEncounter != entity.FatefulEncounter) return;
            if (!chains.TryGetValue(gift.Context, out var chain))
                chains[gift.Context] = chain = EncounterOrigin.GetOriginChain(entity, gift.Generation, gift.Context);
            if (!chain.Any(evo => evo.Species == gift.Species && gift.IsMatchExact(entity, evo))
                || gift.GetMatchRating(entity) != EncounterMatchRating.Match) return;
            var key = EventDatabaseService.ContentKey(gift);
            if (key is null || !visited.Add(key)) return;
            if (!_archive.Sources.TryGetValue(key, out var sources))
            {
                // Embedded PGFs may know restrictions absent from raw Gallery files.
                // The separately checked Gallery template supplies candidates in that case;
                // do not attach its paths to this metadata-different embedded template.
                if (!_payloadKeys.Contains(PayloadKey(key))) complete = false;
                return;
            }
            foreach (var path in sources.Where(EventObservationRules.IsSourcePath)) paths.Add(path);
        }
    }

    private static string PayloadKey(string key) => key[..key.IndexOf(':')] + key[key.LastIndexOf(':')..];
}
