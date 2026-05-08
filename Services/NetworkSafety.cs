using System.Net;
using System.Net.Sockets;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>SSRF guard helpers shared by tools/services that fetch from arbitrary URLs.
/// Centralized here so each new tool doesn't reinvent the same loopback/private-range checks
/// (and accidentally miss IPv6 unique-local or cloud-metadata addresses).</summary>
public static class NetworkSafety
{
    /// <summary>True if the given URI resolves to a loopback, private, link-local, or otherwise
    /// non-routable address. Blocks SSRF to internal SwarmUI services, cloud metadata endpoints
    /// (like 169.254.169.254), LAN hosts, etc. Returns false on DNS failure so the caller sees
    /// a real network error instead of a misleading "blocked" message.</summary>
    public static bool IsPrivateOrLoopback(Uri uri)
    {
        string host = uri?.Host;
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (IPAddress.TryParse(host, out IPAddress ip))
        {
            return IsPrivateIp(ip);
        }
        try
        {
            IPAddress[] addrs = Dns.GetHostAddresses(host);
            foreach (IPAddress a in addrs)
            {
                if (IsPrivateIp(a))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>True for loopback / RFC1918 / link-local / IPv6 ULA / loopback6.</summary>
    public static bool IsPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 127) return true;
            if (b[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            byte[] b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
        }
        return false;
    }
}
