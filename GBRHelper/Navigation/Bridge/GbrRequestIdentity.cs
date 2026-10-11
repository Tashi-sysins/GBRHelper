using System.Reflection;

namespace GBRHelper.Navigation;

public static class GbrRequestIdentity
{
    public static bool MatchesRequestedItem(object autoGather, uint requested)
    {
        var item = autoGather.GetType().GetProperty("CurrentRequestedTarget", BindingFlags.Public | BindingFlags.Instance)?.GetValue(autoGather);
        return item?.GetType().GetProperty("ItemId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) is uint current && current == requested;
    }
}
