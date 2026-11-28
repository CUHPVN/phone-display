using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PhoneDisplay.Host.Network;

public static class SelfSignedCertGenerator
{
    public static List<IPAddress> GetLocalIPv4Addresses()
    {
        var candidates = new List<(IPAddress Ip, int Priority)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            string nameDesc = $"{ni.Name} {ni.Description}".ToLowerInvariant();
            bool isVirtual = nameDesc.Contains("radmin") ||
                             nameDesc.Contains("vpn") ||
                             nameDesc.Contains("hamachi") ||
                             nameDesc.Contains("tailscale") ||
                             nameDesc.Contains("zerotier") ||
                             nameDesc.Contains("virtual") ||
                             nameDesc.Contains("vmware") ||
                             nameDesc.Contains("vbox") ||
                             nameDesc.Contains("vethernet") ||
                             nameDesc.Contains("wsl") ||
                             nameDesc.Contains("hyper-v") ||
                             nameDesc.Contains("bluetooth");

            foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(unicast.Address))
                    continue;

                byte[] b = unicast.Address.GetAddressBytes();
                bool isLinkLocal = b[0] == 169 && b[1] == 254;
                bool isPrivateLan = (b[0] == 192 && b[1] == 168) ||
                                    b[0] == 10 ||
                                    (b[0] == 172 && b[1] >= 16 && b[1] <= 31);

                int priority = 100;
                if (!isVirtual && !isLinkLocal)
                {
                    priority = ni.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => isPrivateLan ? 1 : 4,
                        NetworkInterfaceType.Ethernet => isPrivateLan ? 2 : 5,
                        _ => isPrivateLan ? 3 : 6
                    };
                }
                else if (isVirtual)
                {
                    priority = 50;
                }

                candidates.Add((unicast.Address, priority));
            }
        }

        return candidates
            .OrderBy(c => c.Priority)
            .Select(c => c.Ip)
            .Distinct()
            .ToList();
    }

    public static IPAddress? GetUsbTetheringIPv4Address()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            string nameDesc = $"{ni.Name} {ni.Description}".ToLowerInvariant();
            bool isUsbNet = nameDesc.Contains("rndis") ||
                            nameDesc.Contains("remote ndis") ||
                            nameDesc.Contains("usb") ||
                            nameDesc.Contains("tethering") ||
                            nameDesc.Contains("android");

            foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(unicast.Address))
                    continue;

                byte[] b = unicast.Address.GetAddressBytes();
                if (isUsbNet || (b[0] == 192 && b[1] == 168 && b[2] == 42))
                    return unicast.Address;
            }
        }
        return null;
    }

    public static IPAddress? GetRadminOrVpnIPv4Address()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            string nameDesc = $"{ni.Name} {ni.Description}".ToLowerInvariant();
            bool isRemoteVpn = nameDesc.Contains("radmin") ||
                               nameDesc.Contains("tailscale") ||
                               nameDesc.Contains("zerotier") ||
                               nameDesc.Contains("hamachi");
            if (!isRemoteVpn) continue;

            foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicast.Address))
                {
                    return unicast.Address;
                }
            }
        }
        return null;
    }

    public static X509Certificate2 LoadOrCreateCertificate(IReadOnlyCollection<IPAddress> lanIps)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneDisplay");
        Directory.CreateDirectory(dir);

        string pfxPath = Path.Combine(dir, "phonedisplay-lan.pfx");
        string metaPath = Path.Combine(dir, "phonedisplay-lan.ips");
        string ipSignature = string.Join(",", lanIps.Select(ip => ip.ToString()).OrderBy(s => s));

        if (File.Exists(pfxPath) && File.Exists(metaPath))
        {
            try
            {
                string savedSig = File.ReadAllText(metaPath).Trim();
                if (savedSig == ipSignature)
                {
                    var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, string.Empty, X509KeyStorageFlags.Exportable);
                    if (existing.NotAfter > DateTime.UtcNow.AddDays(7))
                        return existing;
                    existing.Dispose();
                }
            }
            catch
            {
                // Regenerate on corrupt or unreadable cert
            }
        }

        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=PhoneDisplay Local Host",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in lanIps)
        {
            sanBuilder.AddIpAddress(ip);
        }
        req.CertificateExtensions.Add(sanBuilder.Build());

        using var ephemeral = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx, string.Empty);
        File.WriteAllBytes(pfxPath, pfxBytes);
        File.WriteAllText(metaPath, ipSignature);

        return X509CertificateLoader.LoadPkcs12(pfxBytes, string.Empty, X509KeyStorageFlags.Exportable);
    }
}
