using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RevitVr.Core;

/// <summary>
/// Picks the addresses worth handing to a headset.
///
/// The naive "first non-loopback IPv4" answer is usually wrong on a developer's machine: VPN
/// clients, Hyper-V switches and Docker bridges all present adapters, and the one that wins the
/// enumeration order is frequently not the one the headset can reach. This ranks by evidence —
/// a default gateway, a physical Wi-Fi/Ethernet link, a private range — and demotes the
/// adapters that are known to be virtual.
/// </summary>
public static class NetAddress
{
    private static readonly string[] VirtualHints =
    {
        "vethernet", "vmware", "virtualbox", "hyper-v", "loopback", "tap-", "tap0",
        "tun", "wireguard", "tailscale", "zerotier", "vpn", "docker", "npcap", "bluetooth",
        "default switch", "wsl",
    };

    public sealed record Candidate(string Name, IPAddress Address, int Score)
    {
        public override string ToString() => $"{Address} ({Name})";
    }

    public static IReadOnlyList<Candidate> Ranked()
    {
        var list = new List<Candidate>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); }
            catch { continue; }

            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any));

            var name = ni.Name.ToLowerInvariant();
            var looksVirtual = VirtualHints.Any(h => name.Contains(h));

            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;

                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;      // link-local, no DHCP

                var score = 0;
                if (hasGateway) score += 100;                  // routable off-host
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
                    score += 40;
                if (looksVirtual) score -= 150;
                if (b[0] == 192 && b[1] == 168) score += 30;   // typical home/office Wi-Fi
                else if (b[0] == 10) score += 10;

                list.Add(new Candidate(ni.Name, ip, score));
            }
        }

        return list.OrderByDescending(c => c.Score).ThenBy(c => c.Address.ToString()).ToList();
    }

    /// <summary>The address most likely reachable from a headset on the same network.</summary>
    public static IPAddress? Best() => Ranked().FirstOrDefault()?.Address;

    public static IReadOnlyList<IPAddress> All() => Ranked().Select(c => c.Address).ToList();
}
