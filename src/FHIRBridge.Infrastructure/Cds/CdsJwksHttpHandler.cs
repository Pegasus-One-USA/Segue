using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// The primary handler of the <see cref="CdsJwksProvider.HttpClientName"/> client. A JWKS URL is admin-entered, so
/// without a check it could point Segue at internal-only endpoints (cloud metadata, internal services). Every
/// connection is made to an address resolved here, and private, loopback, link-local and other non-public
/// addresses are refused unless <see cref="CdsOptions.AllowPrivateJwksAddresses"/> is set. Checking the address
/// actually connected to (not just the host name) also covers DNS names that resolve to an internal address.
/// </summary>
public static class CdsJwksHttpHandler
{
    public static SocketsHttpHandler Create(IOptionsMonitor<CdsOptions> options) =>
        CreateGuarded(() => options.CurrentValue.AllowPrivateJwksAddresses);

    /// <summary>
    /// The same guarded handler for any admin- or EHR-supplied URL (also the FHIR callback client, see
    /// <see cref="CdsFhirCallbackClient"/>): no redirects, and only public addresses unless
    /// <paramref name="allowPrivateAddresses"/> answers true at connect time.
    /// </summary>
    public static SocketsHttpHandler CreateGuarded(Func<bool> allowPrivateAddresses) => new()
    {
        AllowAutoRedirect = false,
        // No proxy (HTTP_PROXY etc. are ignored): through a proxy the connect callback would see, and check, the
        // proxy's address while the proxy reached the real target unchecked.
        UseProxy = false,
        Proxy = null,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
            var allowed = allowPrivateAddresses()
                ? addresses
                : addresses.Where(IsPublicAddress).ToArray();
            if (allowed.Length == 0)
            {
                throw new HttpRequestException(
                    $"The host {host} resolves only to private, loopback or link-local addresses; it is not contacted.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>False for loopback, private (RFC 1918 / unique local), carrier-grade NAT, link-local (including the
    /// 169.254.169.254 cloud metadata address), multicast and other reserved ranges.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                     || b[0] == 10
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || b[0] == 127
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                     || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if (EmbeddedIPv4(bytes) is { } embedded)
            {
                // NAT64, 6to4 and IPv4-compatible forms reach the embedded IPv4 address: judge that address.
                return IsPublicAddress(embedded);
            }

            return !(address.Equals(IPAddress.IPv6Any)
                     || address.IsIPv6LinkLocal
                     || address.IsIPv6SiteLocal
                     || address.IsIPv6Multicast
                     || (bytes[0] & 0xFE) == 0xFC);
        }

        return false;
    }

    /// <summary>
    /// The IPv4 address embedded in a NAT64 (64:ff9b::/96, 64:ff9b:1::/48), 6to4 (2002::/16) or IPv4-compatible
    /// (::/96, other than :: and ::1) IPv6 address; null for any other address.
    /// </summary>
    private static IPAddress? EmbeddedIPv4(byte[] b)
    {
        static bool Zero(byte[] bytes, int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (bytes[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        // 64:ff9b::/96 (well-known NAT64 prefix): IPv4 in the last 4 bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && Zero(b, 4, 12))
        {
            return new IPAddress(b[12..16]);
        }

        // 64:ff9b:1::/48 (local-use NAT64): the mapping layout varies, so refuse it by reading it as 0.0.0.0.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01)
        {
            return IPAddress.Any;
        }

        // 2002::/16 (6to4): IPv4 in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b[2..6]);
        }

        // ::/96 (IPv4-compatible, deprecated): IPv4 in the last 4 bytes. :: and ::1 are handled elsewhere.
        if (Zero(b, 0, 12) && !(Zero(b, 12, 15) && b[15] <= 1))
        {
            return new IPAddress(b[12..16]);
        }

        return null;
    }
}
