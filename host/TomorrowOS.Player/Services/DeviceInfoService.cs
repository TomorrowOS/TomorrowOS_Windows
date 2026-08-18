using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TomorrowOS.Player.Services;

internal sealed class DeviceInfoService
{
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private Dictionary<string, object?>? _cachedInfo;
    private DateTime _cacheUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public Dictionary<string, object?> GetDeviceInfo()
    {
        if (_cachedInfo != null && DateTime.UtcNow - _cacheUtc < CacheTtl)
        {
            // Refresh live fields every call.
            _cachedInfo["online"] = NetworkInterface.GetIsNetworkAvailable();
            _cachedInfo["bootUptimeSec"] = GetBootUptimeSec();
            return _cachedInfo;
        }

        var deviceId = GetStableDeviceId();
        var model = GetHardwareModel();
        var firmware = GetBiosFirmwareVersion();
        var serial = GetBiosSerialNumber() ?? deviceId;
        var osEdition = GetWindowsEdition();

        _cachedInfo = new Dictionary<string, object?>
        {
            ["online"] = NetworkInterface.GetIsNetworkAvailable(),
            ["deviceId"] = deviceId,
            ["model"] = model,
            ["firmware"] = firmware,
            ["serialNumber"] = serial,
            ["hostname"] = Environment.MachineName,
            ["bootUptimeSec"] = GetBootUptimeSec(),
            ["osEdition"] = osEdition,
            ["playerVersion"] = "1.0.0",
            ["runtimeVersion"] = Environment.Version.ToString()
        };
        _cacheUtc = DateTime.UtcNow;
        return _cachedInfo;
    }

    public double GetBootUptimeSec()
    {
        try
        {
            return GetTickCount64() / 1000.0;
        }
        catch
        {
            return Environment.TickCount64 / 1000.0;
        }
    }

    /// <summary>Stable pairing id (SMBIOS UUID / MachineGuid). Not the chassis serial.</summary>
    public string GetStableDeviceId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject obj in searcher.Get())
            {
                var uuid = obj["UUID"]?.ToString();
                if (!string.IsNullOrWhiteSpace(uuid) &&
                    !uuid.Equals("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase))
                {
                    return uuid!;
                }
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrWhiteSpace(guid))
            {
                return guid!;
            }
        }
        catch
        {
            // fall through
        }

        return Environment.MachineName;
    }

    /// <summary>Hardware model, e.g. "Dell Inc. Latitude 7440" or "LENOVO Yoga Slim 7".</summary>
    private static string GetHardwareModel()
    {
        string? manufacturer = null;
        string? model = null;
        string? productName = null;
        string? productVersion = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                manufacturer = CleanWmiString(obj["Manufacturer"]?.ToString());
                model = CleanWmiString(obj["Model"]?.ToString());
                break;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Version FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject obj in searcher.Get())
            {
                productName = CleanWmiString(obj["Name"]?.ToString());
                productVersion = CleanWmiString(obj["Version"]?.ToString());
                break;
            }
        }
        catch
        {
            // ignore
        }

        // Prefer marketing-ish product version when Model is only a type code (common on Lenovo).
        var rich = productVersion;
        if (rich == null || LooksLikeTypeCode(rich))
        {
            rich = productName;
        }

        if (rich != null && !LooksLikeTypeCode(rich))
        {
            if (manufacturer != null &&
                !rich.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
            {
                return $"{manufacturer} {rich}";
            }

            return rich;
        }

        if (model != null && manufacturer != null &&
            !model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
        {
            return $"{manufacturer} {model}";
        }

        return model ?? manufacturer ?? Environment.MachineName;
    }

    private static bool LooksLikeTypeCode(string value)
    {
        // e.g. Lenovo "83JM", short alphanumeric SKUs without spaces.
        if (value.Contains(' ')) return false;
        return value.Length <= 6;
    }

    /// <summary>BIOS / UEFI firmware version string.</summary>
    private static string GetBiosFirmwareVersion()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion, Version, Manufacturer FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
            {
                var smbios = CleanWmiString(obj["SMBIOSBIOSVersion"]?.ToString());
                if (smbios != null) return smbios;

                var version = CleanWmiString(obj["Version"]?.ToString());
                if (version != null) return version;
            }
        }
        catch
        {
            // fall through
        }

        return "Unknown";
    }

    /// <summary>Chassis / BIOS serial from SMBIOS.</summary>
    private static string? GetBiosSerialNumber()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
            {
                var serial = CleanWmiString(obj["SerialNumber"]?.ToString());
                if (serial != null) return serial;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject obj in searcher.Get())
            {
                var serial = CleanWmiString(obj["SerialNumber"]?.ToString());
                if (serial != null) return serial;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject obj in searcher.Get())
            {
                var serial = CleanWmiString(obj["IdentifyingNumber"]?.ToString());
                if (serial != null) return serial;
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    /// <summary>Marketing edition, e.g. "Windows 11 Pro".</summary>
    private static string GetWindowsEdition()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = CleanWmiString(key?.GetValue("ProductName")?.ToString());
            var display = CleanWmiString(key?.GetValue("DisplayVersion")?.ToString());
            var build = CleanWmiString(key?.GetValue("CurrentBuild")?.ToString());

            // Older registries still say "Windows 10 Pro" on Windows 11 builds (>= 22000).
            if (productName != null &&
                int.TryParse(build, out var buildNum) &&
                buildNum >= 22000 &&
                productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            if (productName != null && display != null)
            {
                return $"{productName} ({display})";
            }

            if (productName != null)
            {
                return productName;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var caption = CleanWmiString(obj["Caption"]?.ToString());
                if (caption != null) return caption;
            }
        }
        catch
        {
            // fall through
        }

        return RuntimeInformation.OSDescription;
    }

    private static string? CleanWmiString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;

        string[] placeholders =
        [
            "To Be Filled By O.E.M.",
            "To be filled by O.E.M.",
            "Default string",
            "Default String",
            "System Serial Number",
            "System Product Name",
            "System manufacturer",
            "System Manufacturer",
            "None",
            "N/A",
            "NA",
            "0",
            "OOOOOO",
            "Not Applicable",
            "Unknown"
        ];

        foreach (var p in placeholders)
        {
            if (trimmed.Equals(p, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return trimmed;
    }
}
