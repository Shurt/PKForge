using System.Globalization;

namespace PKForge.Domain;

/// <summary>Matches curated Bank Plan distributions, including item and prerequisite gifts.</summary>
public static class BankPlanWonderCards
{
    public static bool Matches(EventGift gift, EventGiftSaveProfile? profile, PokedexBankPlan? plan)
    {
        if (profile is null || !EventGiftRules.IsCompatible(gift, profile)
            || string.IsNullOrWhiteSpace(gift.Details?.SourceFile) || plan?.Targets is null)
            return false;

        // Older caches use generation-relative paths. New archives retain the canonical
        // Gallery prefix so event sync can identify the same file across apps.
        var source = gift.Details.SourceFile.Replace('\\', '/');
        var prefix = $"Released/Gen {gift.Generation}/";
        if (source.StartsWith("Released/", StringComparison.Ordinal) && !source.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var fullPath = source.StartsWith(prefix, StringComparison.Ordinal) ? source : prefix + source;
        return plan.Targets.Any(target => target?.WonderCards?.Any(reference =>
            reference is not null
            && int.TryParse(reference.CardId, NumberStyles.None, CultureInfo.InvariantCulture, out var cardId)
            && cardId == gift.CardId
            && string.Equals(reference.SourcePath?.Replace('\\', '/'), fullPath, StringComparison.Ordinal)
            && IsReceivingGame(reference.Games, profile.GameLabel)) == true);
    }

    private static bool IsReceivingGame(string? games, string gameLabel)
    {
        if (string.IsNullOrWhiteSpace(games)) return false;
        // References list receiving games before any transfer instructions, e.g.
        // "Diamond / Pearl / Platinum, then trade to HGSS". HGSS is not a receiving game.
        var receivingGames = games.Split(',')[0];
        var current = NormalizeGame(gameLabel);
        return current.Length > 0 && receivingGames.Split('/').Any(game => NormalizeGame(game) == current);
    }

    private static string NormalizeGame(string game) =>
        string.Concat(game.Replace("Pokémon", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Pokemon", "", StringComparison.OrdinalIgnoreCase)
            .Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
