using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace NINA.Plugins.MLAstroRPA;

/// <summary>Linux USB metadata discovery only. Enumeration never opens or writes a serial port.</summary>
public static class UsbDiscovery
{
    public sealed record Device(string path, string label, bool likelyRpa, bool compatible, string target);
    public static Device[] Enumerate(string devRoot = "/dev", string ttyRoot = "/sys/class/tty")
    {
        var result = new List<Device>();
        if (!Directory.Exists(ttyRoot)) return [];
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var byId = Path.Combine(devRoot, "serial/by-id");
        if (Directory.Exists(byId)) foreach (var entry in Directory.EnumerateFiles(byId).OrderBy(p => p, StringComparer.Ordinal)) {
            try {
                var target = new FileInfo(entry).ResolveLinkTarget(true)?.FullName;
                if (target != null) aliases.TryAdd(target, entry);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var tty in Directory.EnumerateDirectories(ttyRoot).OrderBy(p => p, StringComparer.Ordinal)) {
            var name = Path.GetFileName(tty);
            if (!name.StartsWith("ttyUSB", StringComparison.Ordinal) && !name.StartsWith("ttyACM", StringComparison.Ordinal)) continue;
            var target = Path.Combine(devRoot, name);
            if (!File.Exists(target)) continue;
            try {
                // Resolve the class symlink first: resolving only its child leaves an
                // intermediate symlink in the path, so Parent walks the wrong directory tree.
                var realTty = new DirectoryInfo(tty).ResolveLinkTarget(true) as DirectoryInfo ?? new DirectoryInfo(tty);
                var link = new DirectoryInfo(Path.Combine(realTty.FullName, "device"));
                var parent = link.ResolveLinkTarget(true) as DirectoryInfo ?? link;
                for (int level = 0; parent != null && level < 12; level++, parent = parent.Parent) {
                    var vendor = Read(parent.FullName, "idVendor").ToLowerInvariant();
                    var product = Read(parent.FullName, "idProduct").ToLowerInvariant();
                    if (vendor.Length == 0 || product.Length == 0) continue;
                    var description = string.Join(" ", new[] { Read(parent.FullName, "manufacturer"), Read(parent.FullName, "product"), Read(parent.FullName, "serial") }.Where(v => v.Length > 0));
                    var path = aliases.GetValueOrDefault(target, target);
                    var branded = description.Contains("MLAstro", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Contains("MLAstro", StringComparison.OrdinalIgnoreCase);
                    var compatible = branded || vendor == "303a" || (vendor == "10c4" && product == "ea60") || (vendor == "1a86" && (product == "7523" || product == "55d4"));
                    result.Add(new(path, $"{(description.Length > 0 ? description : "USB serial device")} ({vendor}:{product})", branded, compatible, target));
                    break;
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return result.ToArray();
    }
    private static string Read(string directory, string name)
    {
        try { var path = Path.Combine(directory, name); return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; }
    }
    public static string Select(Device[] devices, string verifiedPath)
    {
        // Only a stable, previously identity-confirmed path may bypass ambiguity.
        var remembered = devices.FirstOrDefault(d => d.path == verifiedPath && verifiedPath?.Contains("/serial/by-id/", StringComparison.Ordinal) == true);
        if (remembered != null) return remembered.path;
        var branded = devices.Where(d => d.likelyRpa).ToArray();
        if (branded.Length == 1) return branded[0].path;
        var eligible = branded.Length > 1 ? branded : devices.Where(d => d.compatible).ToArray();
        if (eligible.Length == 1) return eligible[0].path;
        throw new InvalidOperationException(eligible.Length == 0
            ? "No identifiable RPA USB device found. Plug it into the Pi and refresh, or choose its USB device from the list."
            : "Several possible RPA USB devices found. Choose the RPA from the USB device list.");
    }
}
