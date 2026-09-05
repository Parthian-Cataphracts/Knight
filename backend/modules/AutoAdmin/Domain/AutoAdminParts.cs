namespace AutoAdmin.Domain;

/// <summary>How autonomous the admin is for one customer (docs/adr/0038).</summary>
public enum AutonomyMode
{
    /// <summary>Everything is drafted and waits for the merchant to approve it. The default.</summary>
    ApprovalRequired = 0,

    /// <summary>Content is generated and published without waiting — an opt-in the customer takes deliberately.</summary>
    FullyAutomatic = 1,
}

/// <summary>A kind of content the admin can generate.</summary>
public enum ContentKind
{
    Image = 0,
    Caption = 1,
    Story = 2,
    Video = 3,
}

/// <summary>
/// The Automatic Admin's parts, by slug, and what each one means to the engine.
/// Each part is a Feature in the catalogue (docs/adr/0037); this maps the ones
/// the engine acts on — the generation kinds and the channels — to the enums and
/// channel keys it works in. The mapping is the single place the slug strings
/// live, so a new part is added here and nowhere else.
/// </summary>
public static class AutoAdminParts
{
    public const string ParentSlug = "auto-admin";

    public const string AutopilotSlug = "auto-admin-autopilot";
    public const string AutoReplySlug = "auto-admin-autoreply";
    public const string BoostSlug = "auto-admin-boost";

    /// <summary>Generation part slug → the kind it produces.</summary>
    public static readonly IReadOnlyDictionary<string, ContentKind> GenerationKinds =
        new Dictionary<string, ContentKind>(StringComparer.Ordinal)
        {
            ["auto-admin-image"] = ContentKind.Image,
            ["auto-admin-caption"] = ContentKind.Caption,
            ["auto-admin-story"] = ContentKind.Story,
            ["auto-admin-video"] = ContentKind.Video,
        };

    /// <summary>Channel part slug → the channel key a publisher is registered under.</summary>
    public static readonly IReadOnlyDictionary<string, string> Channels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auto-admin-telegram"] = "telegram",
            ["auto-admin-instagram"] = "instagram",
            ["auto-admin-divar"] = "divar",
            ["auto-admin-basalam"] = "basalam",
        };

    /// <summary>True when the slug is one of the Automatic Admin's parts.</summary>
    public static bool IsPart(string slug) =>
        slug.StartsWith("auto-admin-", StringComparison.Ordinal);
}
