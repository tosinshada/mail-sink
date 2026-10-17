using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Mailsink.Core.Options;
using Scriban;
using Scriban.Runtime;

namespace Mailsink.Core.Templates;

public sealed record SendTemplate(
    Guid Id,
    string Name,
    string? Subject,
    string? Text,
    string? Html,
    string? SampleData,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TemplateInput(string Name, string? Subject, string? Text, string? Html, string? SampleData);

[JsonSerializable(typeof(List<SendTemplate>))]
internal sealed partial class TemplateJsonContext : JsonSerializerContext;

/// <summary>Saved send templates (Scriban syntax, e.g. <c>Hi {{name}}</c>). Persisted as JSON in <c>DataDir</c> for SQLite storage.</summary>
public sealed class TemplateService
{
    private readonly Lock _gate = new();
    private readonly string? _path;
    private List<SendTemplate> _templates = [];

    public TemplateService(IOptions<MailsinkOptions> options)
    {
        if (options.Value.Storage == StorageKind.Sqlite)
        {
            _path = Path.Combine(Path.GetFullPath(options.Value.DataDir), "templates.json");
            if (File.Exists(_path))
            {
                _templates = JsonSerializer.Deserialize(File.ReadAllText(_path), TemplateJsonContext.Default.ListSendTemplate) ?? [];
            }
        }
    }

    public IReadOnlyList<SendTemplate> List()
    {
        lock (_gate)
        {
            return [.. _templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public SendTemplate? Find(Guid id)
    {
        lock (_gate)
        {
            return _templates.FirstOrDefault(t => t.Id == id);
        }
    }

    public SendTemplate? FindByName(string name)
    {
        lock (_gate)
        {
            return _templates.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public SendTemplate Save(Guid? id, TemplateInput input)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var existing = id is { } key ? _templates.FirstOrDefault(t => t.Id == key) : null;
            var template = existing is null
                ? new SendTemplate(id ?? Guid.NewGuid(), input.Name, input.Subject, input.Text, input.Html, input.SampleData, now, now)
                : existing with { Name = input.Name, Subject = input.Subject, Text = input.Text, Html = input.Html, SampleData = input.SampleData, UpdatedAt = now };
            _templates = [.. _templates.Where(t => t.Id != template.Id), template];
            Persist();
            return template;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var before = _templates.Count;
            _templates = [.. _templates.Where(t => t.Id != id)];
            Persist();
            return _templates.Count != before;
        }
    }

    /// <summary>Renders a Scriban template against JSON data. Member names are used exactly as written in the JSON.</summary>
    public static string? Render(string? template, JsonElement? data)
    {
        if (string.IsNullOrEmpty(template) || data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            return template;
        }

        var parsed = Template.Parse(template);
        if (parsed.HasErrors)
        {
            throw new FormatException(string.Join("; ", parsed.Messages.Select(m => m.Message)));
        }

        var context = new TemplateContext { MemberRenamer = member => member.Name, StrictVariables = false };
        var globals = new ScriptObject();
        foreach (var property in data.Value.EnumerateObject())
        {
            globals[property.Name] = ToScriptValue(property.Value);
        }

        context.PushGlobal(globals);
        return parsed.Render(context);
    }

    private static object? ToScriptValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => ToScriptObject(value),
        JsonValueKind.Array => new ScriptArray(value.EnumerateArray().Select(ToScriptValue)),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static ScriptObject ToScriptObject(JsonElement value)
    {
        var obj = new ScriptObject();
        foreach (var property in value.EnumerateObject())
        {
            obj[property.Name] = ToScriptValue(property.Value);
        }

        return obj;
    }

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_templates, TemplateJsonContext.Default.ListSendTemplate));
    }
}
