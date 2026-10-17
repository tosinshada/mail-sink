using System.Globalization;

namespace Mailsink.Core.Domain;

/// <summary>Global capture id, used by the API and UI.</summary>
public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.CreateVersion7());

    public static bool TryParse(string? text, out MessageId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new MessageId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public readonly record struct MailboxId(long Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
