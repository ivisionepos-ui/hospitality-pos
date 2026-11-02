namespace Pos.Domain.Menus;

/// <summary>A name or description in English and (optionally) Arabic.</summary>
public sealed record LocalizedText
{
    /// <summary>Longest name accepted.</summary>
    public const int MaxLength = 120;

    /// <summary>Creates a text. English is required; Arabic is optional.</summary>
    public LocalizedText(string en, string? ar = null)
    {
        if (string.IsNullOrWhiteSpace(en))
        {
            throw new ArgumentException("An English name is required.", nameof(en));
        }

        En = en.Trim();
        Ar = string.IsNullOrWhiteSpace(ar) ? null : ar.Trim();
        if (En.Length > MaxLength || (Ar?.Length ?? 0) > MaxLength)
        {
            throw new ArgumentException($"Names can be at most {MaxLength} characters.");
        }
    }

    /// <summary>English text.</summary>
    public string En { get; }

    /// <summary>Arabic text, or null if not set.</summary>
    public string? Ar { get; }

    /// <summary>Text for a language code ("ar" or "en"); falls back to English when Arabic is missing.</summary>
    public string For(string languageCode) =>
        string.Equals(languageCode, "ar", StringComparison.OrdinalIgnoreCase) && Ar is not null ? Ar : En;

    /// <inheritdoc />
    public override string ToString() => En;
}
