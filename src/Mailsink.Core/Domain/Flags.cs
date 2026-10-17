namespace Mailsink.Core.Domain;

[Flags]
public enum SystemFlags : byte
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Deleted = 8,
    Draft = 16,
    Recent = 32,
}

public enum FlagOperation { Replace, Add, Remove }

public enum SpecialUse
{
    None = 0,
    Sent = 1,
    Drafts = 2,
    Trash = 3,
    Junk = 4,
    Archive = 5,
    All = 6,
    Flagged = 7,
    Outbox = 8,
}

public static class FlagNames
{
    public static readonly IReadOnlyList<(SystemFlags Flag, string Name)> System =
    [
        (SystemFlags.Answered, "\\Answered"),
        (SystemFlags.Flagged, "\\Flagged"),
        (SystemFlags.Deleted, "\\Deleted"),
        (SystemFlags.Seen, "\\Seen"),
        (SystemFlags.Draft, "\\Draft"),
    ];

    public static bool TryParseSystem(string name, out SystemFlags flag)
    {
        foreach (var (f, n) in System)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                flag = f;
                return true;
            }
        }

        if (string.Equals(name, "\\Recent", StringComparison.OrdinalIgnoreCase))
        {
            flag = SystemFlags.Recent;
            return true;
        }

        flag = SystemFlags.None;
        return false;
    }

    public static IEnumerable<string> ToNames(SystemFlags flags)
    {
        foreach (var (f, n) in System)
        {
            if ((flags & f) != 0)
            {
                yield return n;
            }
        }
    }

    public static string? SpecialUseAttribute(SpecialUse use) => use switch
    {
        SpecialUse.Sent => "\\Sent",
        SpecialUse.Drafts => "\\Drafts",
        SpecialUse.Trash => "\\Trash",
        SpecialUse.Junk => "\\Junk",
        SpecialUse.Archive => "\\Archive",
        SpecialUse.All => "\\All",
        SpecialUse.Flagged => "\\Flagged",
        _ => null,
    };
}
