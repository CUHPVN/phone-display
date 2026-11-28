using System.IO;
using System.Security.Cryptography;

namespace PhoneDisplay.Host.Models;

/// <summary>
/// Stores a single persistent access PIN/token so it remains unchanged across app restarts
/// until explicitly rotated by the user or overridden via --pin.
/// </summary>
public static class PinStore
{
    private static string PinFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhoneDisplay",
        "pin.txt");

    public static string LoadOrCreate(string? overridePin = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePin))
        {
            string trimmed = overridePin.Trim();
            Save(trimmed);
            return trimmed;
        }

        try
        {
            if (File.Exists(PinFilePath))
            {
                string existing = File.ReadAllText(PinFilePath).Trim();
                if (!string.IsNullOrEmpty(existing))
                    return existing;
            }
        }
        catch
        {
            // Fall through to generate a fresh PIN if reading fails
        }

        string generated = GenerateNumericPin();
        Save(generated);
        return generated;
    }

    public static string Rotate(string? currentPin = null)
    {
        string next = GenerateNumericPin();
        if (!string.IsNullOrEmpty(currentPin) && string.Equals(next, currentPin, StringComparison.Ordinal))
            next = GenerateNumericPin();

        Save(next);
        return next;
    }

    public static void Save(string pin)
    {
        try
        {
            string? dir = Path.GetDirectoryName(PinFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(PinFilePath, pin.Trim());
        }
        catch
        {
            // Non-fatal if storage directory is read-only
        }
    }

    private static string GenerateNumericPin() =>
        RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
}
