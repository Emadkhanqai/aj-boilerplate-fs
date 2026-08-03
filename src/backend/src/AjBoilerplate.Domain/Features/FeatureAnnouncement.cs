using System.Text.Json;
using AjBoilerplate.Domain.Common;

namespace AjBoilerplate.Domain.Features;

/// <summary>
/// A "what's new" announcement, shown to each authenticated user exactly once — the first time they
/// land on one of the pages the announcement is bound to. Dismissal is recorded per user by
/// <see cref="FeatureAcknowledgement"/>, deliberately server-side rather than in the browser, so the
/// announcement does not reappear on another device and cannot be resurrected by clearing site data.
///
/// Announcements are CONTENT, not user input: there is no write endpoint. A new one ships as an
/// INSERT-only EF Core migration (see <c>docs/whats-new.md</c>), which is why this aggregate exposes
/// a factory and two lifecycle methods rather than a full edit surface.
/// </summary>
public sealed class FeatureAnnouncement : AuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>; mirrored by the EF configuration.</summary>
    public const int MaxKeyLength = 80;

    /// <summary>Maximum length of <see cref="TitleEn"/> and <see cref="TitleAr"/>.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Maximum length of <see cref="BodyEn"/> and <see cref="BodyAr"/>.</summary>
    public const int MaxBodyLength = 2000;

    /// <summary>Maximum length of the serialised <see cref="PagesJson"/> array.</summary>
    public const int MaxPagesJsonLength = 1000;

    /// <summary>The value of <see cref="PagesJson"/> that means "show on every route".</summary>
    public const string EveryPage = "[]";

    private FeatureAnnouncement()
    {
        Key = string.Empty;
        TitleEn = string.Empty;
        BodyEn = string.Empty;
        PagesJson = EveryPage;
    }

    private FeatureAnnouncement(
        Guid id,
        string key,
        string titleEn,
        string? titleAr,
        string bodyEn,
        string? bodyAr,
        string pagesJson,
        bool isActive,
        int displayOrder,
        DateTimeOffset nowUtc)
    {
        Id = id;
        Key = key;
        TitleEn = titleEn;
        TitleAr = titleAr;
        BodyEn = bodyEn;
        BodyAr = bodyAr;
        PagesJson = pagesJson;
        IsActive = isActive;
        DisplayOrder = displayOrder;
        MarkCreated(nowUtc);
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// Stable machine identifier for this announcement (for example <c>"search-v2"</c>). Unique, and
    /// never shown to a user — it exists so whoever authors the next migration has a handle that does
    /// not change when the copy is reworded.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>English title. Required.</summary>
    public string TitleEn { get; private set; }

    /// <summary>Arabic title; null falls back to <see cref="TitleEn"/> at the presentation layer.</summary>
    public string? TitleAr { get; private set; }

    /// <summary>English body. Plain text with light conventions the client renders (see
    /// <c>docs/whats-new.md</c>); the API never interprets it.</summary>
    public string BodyEn { get; private set; }

    /// <summary>Arabic body; null falls back to <see cref="BodyEn"/> at the presentation layer.</summary>
    public string? BodyAr { get; private set; }

    /// <summary>
    /// JSON array of URL path PREFIXES this announcement may trigger on, for example
    /// <c>["/reports","/settings"]</c>. An empty array (<see cref="EveryPage"/>) means "every route".
    /// Stored as JSON rather than a child table because it is a handful of strings per announcement
    /// that are only ever read as a whole — see <see cref="Targets"/>.
    /// </summary>
    public string PagesJson { get; private set; }

    /// <summary>
    /// Soft on/off switch. Setting it false retires an announcement WITHOUT deleting it, so the
    /// acknowledgement history stays intact and the same rows can answer "who has seen this?" later.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>Order among several pending announcements; lower shows first. Ties break by
    /// <see cref="AuditedEntity.CreatedAt"/>.</summary>
    public int DisplayOrder { get; private set; }

    /// <summary>Creates an announcement. Every bound is enforced here so the entity, the EF
    /// configuration, and the migration cannot drift apart.</summary>
    public static FeatureAnnouncement Create(
        Guid id,
        string key,
        string titleEn,
        string? titleAr,
        string bodyEn,
        string? bodyAr,
        string? pagesJson,
        bool isActive,
        int displayOrder,
        DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("An announcement's id is required.");
        }

        return new FeatureAnnouncement(
            id,
            Required(key, MaxKeyLength, "key"),
            Required(titleEn, MaxTitleLength, "English title"),
            Optional(titleAr, MaxTitleLength, "Arabic title"),
            Required(bodyEn, MaxBodyLength, "English body"),
            Optional(bodyAr, MaxBodyLength, "Arabic body"),
            NormalizePages(pagesJson),
            isActive,
            displayOrder,
            nowUtc);
    }

    /// <summary>Retires the announcement without deleting it or its acknowledgements. Idempotent.</summary>
    public void Retire(DateTimeOffset nowUtc)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        Touch(nowUtc);
    }

    /// <summary>Brings a retired announcement back. Users who already acknowledged it still do not
    /// see it again — their acknowledgement row was never removed.</summary>
    public void Reinstate(DateTimeOffset nowUtc)
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        Touch(nowUtc);
    }

    /// <summary>
    /// Does this announcement target <paramref name="requestPath"/>?
    ///
    /// An empty (or unreadable) page list matches EVERY route. Otherwise the CANONICALISED request
    /// path must start with one of the registered prefixes, compared case-insensitively.
    /// <paramref name="requestPath"/> is normalised here rather than by the caller so the
    /// path-traversal guard in <see cref="FeaturePath.Normalize"/> cannot be bypassed by forgetting
    /// to apply it; normalisation is idempotent, so applying it twice is harmless.
    /// </summary>
    public bool Targets(string? requestPath)
    {
        var pages = ParsePages(PagesJson);
        if (pages.Count == 0)
        {
            return true;
        }

        var path = FeaturePath.Normalize(requestPath);
        foreach (var prefix in pages)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                continue;
            }

            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The configured prefixes, or an empty list when the column holds anything this cannot read.
    ///
    /// Malformed content DEGRADES to "matches everywhere" rather than throwing: this runs on a
    /// read-only lookup the client fires on every navigation, and one bad row must not turn that
    /// into a 500 for every user on every page. Announcements carry no authority — showing one too
    /// widely is a cosmetic fault, and a broken page-targeting query is not.
    /// </summary>
    private static List<string?> ParsePages(string pagesJson)
    {
        if (string.IsNullOrWhiteSpace(pagesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string?>>(pagesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizePages(string? pagesJson)
    {
        var trimmed = pagesJson?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return EveryPage;
        }

        if (trimmed.Length > MaxPagesJsonLength)
        {
            throw new DomainException($"An announcement's page list cannot exceed {MaxPagesJsonLength} characters.");
        }

        return trimmed;
    }

    private static string Required(string value, int maxLength, string label)
    {
        var trimmed = Optional(value, maxLength, label);
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException($"An announcement's {label} is required.");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"An announcement's {label} cannot exceed {maxLength} characters.");
        }

        return trimmed;
    }
}
