using System.Globalization;
using AngleSharp.Dom;
using Ganss.Xss;
using MimeKit;

namespace Mailsink.Api;

/// <summary>
/// Produces the HTML shown in the preview iframe: sanitized (no scripts, handlers or forms), <c>cid:</c> references
/// rewritten to part URLs, and remote images blocked unless the viewer opts in.
/// </summary>
internal static class HtmlPreview
{
    public static string ContentSecurityPolicy(bool allowRemote) =>
        $"default-src 'none'; img-src data: 'self'{(allowRemote ? " http: https:" : "")}; style-src 'unsafe-inline'{(allowRemote ? " http: https:" : "")}; font-src data:{(allowRemote ? " http: https:" : "")}; frame-ancestors 'self'";

    public static string Render(MimeMessage message, string partBaseUrl, bool allowRemote)
    {
        var html = message.HtmlBody;
        if (html is null)
        {
            var text = System.Net.WebUtility.HtmlEncode(message.TextBody ?? "");
            return $"<!doctype html><html><body><pre style=\"white-space:pre-wrap;font-family:ui-monospace,monospace\">{text}</pre></body></html>";
        }

        var cidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var part in message.BodyParts)
        {
            if (part.ContentId is { } cid)
            {
                cidMap[cid.Trim('<', '>')] = partBaseUrl + index.ToString(CultureInfo.InvariantCulture);
            }

            index++;
        }

        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedTags.Add("center");
        sanitizer.AllowedTags.Add("font");
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        sanitizer.AllowedAttributes.Add("bgcolor");
        sanitizer.AllowedAttributes.Add("background");
        sanitizer.AllowedAttributes.Add("color");
        sanitizer.AllowedAttributes.Add("face");
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        sanitizer.KeepChildNodes = true;
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element)
            {
                return;
            }

            if (element.LocalName == "a")
            {
                element.SetAttribute("target", "_blank");
                element.SetAttribute("rel", "noopener noreferrer");
            }

            foreach (var attribute in new[] { "src", "background" })
            {
                var value = element.GetAttribute(attribute);
                if (value is null)
                {
                    continue;
                }

                if (value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                {
                    element.SetAttribute(attribute, cidMap.GetValueOrDefault(value[4..].Trim('<', '>')) ?? "");
                }
                else if (!allowRemote && (value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("//", StringComparison.Ordinal)))
                {
                    element.SetAttribute("data-blocked-src", value);
                    element.SetAttribute(attribute, "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
                }
            }
        };

        return sanitizer.SanitizeDocument(html);
    }
}
