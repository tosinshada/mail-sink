using System.Text.Json.Serialization;

namespace Mailsink.Core.Chaos;

[JsonConverter(typeof(JsonStringEnumConverter<ChaosStage>))]
public enum ChaosStage { Connect, Helo, Auth, MailFrom, RcptTo, Data }

[JsonConverter(typeof(JsonStringEnumConverter<ChaosAction>))]
public enum ChaosAction { Respond, Delay, Drop }

public sealed record ChaosMatch(string? Sender = null, string? Recipient = null, string? ClientIp = null, string? User = null);

public sealed record ChaosResponse(int Code, string Text);

/// <summary>
/// A fault-injection rule. Properties are settable (not init) so JSON payloads that omit them keep the defaults.
/// Shape: <c>{ "stage": "RcptTo", "match": {...}, "response": {...}, "probability": 1.0 }</c>.</summary>
public sealed record ChaosRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public ChaosStage Stage { get; set; }
    public ChaosMatch? Match { get; set; }
    public ChaosResponse? Response { get; set; }
    public int DelayMs { get; set; }
    public ChaosAction? Action { get; set; }
    public double Probability { get; set; } = 1.0;

    [JsonIgnore]
    public ChaosAction EffectiveAction => Action ?? (Response is not null ? ChaosAction.Respond : DelayMs > 0 ? ChaosAction.Delay : ChaosAction.Respond);
}

public sealed record ChaosSettings(bool Enabled, IReadOnlyList<ChaosRule> Rules);

public sealed record ChaosContext(string? ClientIp, string? Sender = null, string? Recipient = null, string? User = null);

/// <summary>What a matched rule asks the protocol layer to do. Delay applies first, then Drop or Response.</summary>
public sealed record ChaosOutcome(TimeSpan Delay, bool Drop, ChaosResponse? Response, Guid RuleId);

public interface IChaosEngine
{
    ChaosSettings Settings { get; }

    void Update(ChaosSettings settings);

    ChaosOutcome? Evaluate(ChaosStage stage, ChaosContext context);
}

public sealed class ChaosEngine : IChaosEngine
{
    private volatile ChaosSettings _settings = new(true, []);

    public ChaosSettings Settings => _settings;

    public void Update(ChaosSettings settings) => _settings = settings;

    public ChaosOutcome? Evaluate(ChaosStage stage, ChaosContext context)
    {
        var settings = _settings;
        if (!settings.Enabled)
        {
            return null;
        }

        foreach (var rule in settings.Rules)
        {
            if (!rule.Enabled || rule.Stage != stage || !Matches(rule.Match, context))
            {
                continue;
            }

            if (rule.Probability < 1.0 && Random.Shared.NextDouble() >= rule.Probability)
            {
                continue;
            }

            var delay = TimeSpan.FromMilliseconds(Math.Max(0, rule.DelayMs));
            return rule.EffectiveAction switch
            {
                ChaosAction.Drop => new ChaosOutcome(delay, true, null, rule.Id),
                ChaosAction.Delay => new ChaosOutcome(delay, false, null, rule.Id),
                _ => new ChaosOutcome(delay, false, rule.Response ?? new ChaosResponse(451, "4.3.0 Temporary failure (chaos)"), rule.Id),
            };
        }

        return null;
    }

    private static bool Matches(ChaosMatch? match, ChaosContext context) =>
        match is null
        || (Matches(match.Sender, context.Sender) && Matches(match.Recipient, context.Recipient)
            && Matches(match.ClientIp, context.ClientIp) && Matches(match.User, context.User));

    private static bool Matches(string? pattern, string? value) =>
        string.IsNullOrEmpty(pattern) || (value is not null && Glob.IsMatch(pattern, value));
}
