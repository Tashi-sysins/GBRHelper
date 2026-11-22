using System;
using System.Reflection;

namespace GBRHelper.Ipc;

/// <summary>Uses the 7.5.8 page API; returns false only when the legacy tab UI needs the fallback.</summary>
public static class GbrWindowAccess
{
    public const string AutoGatherPage = "auto_gather";

    public static bool TryOpenAutoGather(object plugin)
    {
        var ui = plugin.GetType().GetField("Interface", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(plugin) ?? throw new InvalidOperationException("GBR の画面を取得できません");
        var open = ui.GetType().GetMethod("Open", BindingFlags.Public | BindingFlags.Instance, null, [typeof(string)], null);
        if (open is null) return false;
        if (open.ReturnType != typeof(void)) throw new NotSupportedException("GBR の画面切り替えの形式が未対応です");
        open.Invoke(ui, [AutoGatherPage]); // Open restores a minimized window, selects the page, and never toggles it closed.
        return true;
    }
}
