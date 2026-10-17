using System.Collections.Concurrent;
using System.Net;

namespace Mailsink.Core.Networking;

public sealed record EndpointInfo(string Name, string Protocol, int Port, bool ImplicitTls, string Address);

/// <summary>Listeners report their actual bound ports here (port 0 means OS-assigned).</summary>
public sealed class EndpointRegistry
{
    private readonly ConcurrentDictionary<string, EndpointInfo> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public void Register(EndpointInfo info) => _endpoints[info.Name] = info;

    public void Remove(string name) => _endpoints.TryRemove(name, out _);

    public int? GetPort(string name) => _endpoints.TryGetValue(name, out var info) ? info.Port : null;

    public IReadOnlyList<EndpointInfo> All => [.. _endpoints.Values.OrderBy(e => e.Port)];
}

public static class ListenerAddress
{
    public static IPAddress Parse(string? value) => value switch
    {
        null or "" or "*" or "0.0.0.0" => IPAddress.Any,
        "::" or "[::]" => IPAddress.IPv6Any,
        "localhost" => IPAddress.Loopback,
        _ => IPAddress.Parse(value),
    };
}
