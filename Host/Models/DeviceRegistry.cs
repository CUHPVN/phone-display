using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneDisplay.Host.Models;

public enum DeviceTrustStatus
{
    Pending,
    Approved,
    Banned
}

public enum DeviceAuthDecision
{
    Approved,
    Pending,
    Banned,
    InvalidSecret
}

public sealed class DeviceRecord
{
    public string DeviceId { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public string DeviceName { get; set; } = "Unknown Device";
    public string LastIp { get; set; } = string.Empty;
    public DeviceTrustStatus Status { get; set; } = DeviceTrustStatus.Pending;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public DeviceRecord Clone() => new()
    {
        DeviceId = DeviceId,
        SecretHash = SecretHash,
        DeviceName = DeviceName,
        LastIp = LastIp,
        Status = Status,
        FirstSeenUtc = FirstSeenUtc,
        LastSeenUtc = LastSeenUtc
    };
}

/// <summary>
/// Thread-safe, atomic JSON registry of Approved / Pending / Banned client devices.
/// </summary>
public sealed class DeviceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceRecord> _records = new(StringComparer.Ordinal);
    private readonly string _filePath;

    public DeviceRegistry()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneDisplay");
        _filePath = Path.Combine(dir, "devices.json");
        Load();
    }

    public static string HashSecret(string secret)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool ConstantTimeHashEquals(string hexA, string hexB)
    {
        byte[] a = System.Text.Encoding.UTF8.GetBytes(hexA);
        byte[] b = System.Text.Encoding.UTF8.GetBytes(hexB);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string NormalizeName(string? name)
    {
        string cleaned = string.IsNullOrWhiteSpace(name) ? "Unknown Device" : name.Trim();
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    private static bool IsLoopbackIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) &&
        IPAddress.TryParse(ip, out var parsed) &&
        IPAddress.IsLoopback(parsed);

    public DeviceAuthDecision EvaluateOrRegister(
        string deviceId,
        string deviceSecret,
        string? deviceName,
        string remoteIp,
        bool autoApproveBootstrap)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceSecret))
            return DeviceAuthDecision.InvalidSecret;

        string safeId = deviceId.Trim();
        string safeName = NormalizeName(deviceName);
        string secretHash = HashSecret(deviceSecret);
        DateTime now = DateTime.UtcNow;

        lock (_lock)
        {
            if (!IsLoopbackIp(remoteIp) &&
                _records.Values.Any(r =>
                    r.Status == DeviceTrustStatus.Banned &&
                    string.Equals(r.LastIp, remoteIp, StringComparison.OrdinalIgnoreCase)))
            {
                return DeviceAuthDecision.Banned;
            }

            if (_records.TryGetValue(safeId, out var existing))
            {
                existing.LastSeenUtc = now;
                if (!string.IsNullOrWhiteSpace(remoteIp))
                    existing.LastIp = remoteIp;
                if (!string.IsNullOrWhiteSpace(deviceName))
                    existing.DeviceName = safeName;

                if (existing.Status == DeviceTrustStatus.Banned)
                {
                    SaveLocked();
                    return DeviceAuthDecision.Banned;
                }

                if (existing.Status == DeviceTrustStatus.Approved)
                {
                    if (string.IsNullOrEmpty(existing.SecretHash))
                    {
                        existing.SecretHash = secretHash;
                    }
                    else if (!ConstantTimeHashEquals(existing.SecretHash, secretHash))
                    {
                        return DeviceAuthDecision.InvalidSecret;
                    }

                    SaveLocked();
                    return DeviceAuthDecision.Approved;
                }

                existing.SecretHash = secretHash;
                if (autoApproveBootstrap)
                {
                    existing.Status = DeviceTrustStatus.Approved;
                    SaveLocked();
                    return DeviceAuthDecision.Approved;
                }

                SaveLocked();
                return DeviceAuthDecision.Pending;
            }

            var created = new DeviceRecord
            {
                DeviceId = safeId,
                SecretHash = secretHash,
                DeviceName = safeName,
                LastIp = remoteIp,
                Status = autoApproveBootstrap ? DeviceTrustStatus.Approved : DeviceTrustStatus.Pending,
                FirstSeenUtc = now,
                LastSeenUtc = now
            };
            _records[safeId] = created;
            SaveLocked();
            return autoApproveBootstrap ? DeviceAuthDecision.Approved : DeviceAuthDecision.Pending;
        }
    }

    public bool Approve(string deviceId)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(deviceId, out var rec))
                return false;
            rec.Status = DeviceTrustStatus.Approved;
            rec.LastSeenUtc = DateTime.UtcNow;
            SaveLocked();
            return true;
        }
    }

    public bool Ban(string deviceId)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(deviceId, out var rec))
                return false;
            rec.Status = DeviceTrustStatus.Banned;
            rec.LastSeenUtc = DateTime.UtcNow;
            SaveLocked();
            return true;
        }
    }

    public bool Unban(string deviceId)
    {
        lock (_lock)
        {
            if (_records.Remove(deviceId))
            {
                SaveLocked();
                return true;
            }
            return false;
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_lock)
        {
            if (!_records.TryGetValue(deviceId, out var rec))
                return false;
            rec.Status = DeviceTrustStatus.Pending;
            SaveLocked();
            return true;
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_lock)
        {
            if (_records.Remove(deviceId))
            {
                SaveLocked();
                return true;
            }
            return false;
        }
    }

    public IReadOnlyList<DeviceRecord> GetAll()
    {
        lock (_lock)
        {
            return _records.Values
                .OrderByDescending(r => r.LastSeenUtc)
                .Select(r => r.Clone())
                .ToList();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            _records.Clear();
            try
            {
                if (!File.Exists(_filePath)) return;
                string json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<List<DeviceRecord>>(json, JsonOptions);
                if (loaded is null) return;
                foreach (var rec in loaded)
                {
                    if (!string.IsNullOrWhiteSpace(rec.DeviceId))
                        _records[rec.DeviceId] = rec;
                }
            }
            catch
            {
                // Ignore corrupt file cleanly
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(_records.Values.ToList(), JsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Non-fatal if persistence directory is locked
        }
    }
}
