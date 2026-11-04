using System;
using System.Linq;
using System.Reflection;

namespace NINA.Plugins.MLAstroRPA;

internal static class TppaSelection
{
    public static void SetExternal(bool enabled, string pluginId)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NINA.Plugins.PolarAlignment")
            ?? throw new InvalidOperationException("TPPA is not loaded.");
        var type = assembly.GetType("NINA.Plugins.PolarAlignment.Properties.Settings", true);
        var settings = type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var system = type.GetProperty("SelectedPolarAlignmentSystem") ?? throw new InvalidOperationException("This TPPA version does not expose controller selection.");
        var controller = type.GetProperty("SelectedBridgeControllerId") ?? throw new InvalidOperationException("This TPPA version does not expose external controllers.");
        // Persist ordinary TPPA first. A process crash must not leave RPA selected on disk.
        system.SetValue(settings, "None");
        controller.SetValue(settings, "");
        type.GetMethod("Save", Type.EmptyTypes).Invoke(settings, null);
        if (enabled) {
            controller.SetValue(settings, Guid.Parse(pluginId).ToString("N"));
            system.SetValue(settings, "External");
        }
    }
}
