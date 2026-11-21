// Game/plugin dependencies are replaced only at these boundaries. The conflict,
// ownership, catalog, completion and gather-plan logic all link production files.
namespace GBRHelper.Ipc
{
    public sealed class GbrAutoGatherListAccess
    {
        // Matches the production ListSummary contract. No reflection, IPC or
        // loaded plugins are used by this test executable.
        public sealed record ListSummary(string Name, string Description, bool Enabled,
            IReadOnlyList<(uint ItemId, uint Quantity)> Entries, bool AllItemsEnabled = true,
            bool Fallback = false, bool UsesRetainerInventory = true)
        {
            public IReadOnlySet<uint> DisabledItems { get; init; } = new HashSet<uint>();
        }
    }
}

namespace GBRHelper.Features
{
    // UI feature constants required by the production GatherProfiles naming code.
    public static class UnvisitedFeature
    {
        public const string ManagementTag = "[GBRHelper:VentureUnlock]";
        public const string ManagementTagPrefix = "GBRHelper_解放";
        public const string ListNamePrefix = "GBRHelper_開放";
    }

    public sealed class UngatheredEffects : ICompletionEffects
    {
        public ulong? GetLocalContentId() => 1;
        public bool? CallIsGatheringItemGathered(ushort gatheringItemId) => false;
        public bool? IsFolkloreBookRead(uint bookItemId) => true;
    }
}
