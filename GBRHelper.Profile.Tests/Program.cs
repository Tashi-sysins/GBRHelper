using GBRHelper.Features;
using GBRHelper.Ipc;

var tests = new (string Name, Action Run)[]
{
    ("profile conflicts ignore disabled items", ProfileDisabledItems),
    ("unvisited conflicts ignore disabled items", UnvisitedDisabledItems),
    ("disabled lists never conflict", DisabledLists),
    ("profile scope preserves other enabled managed lists", ProfileScope),
    ("unvisited ignores only the matching managed list", UnvisitedOwnership),
    ("enabled occurrence in another list still conflicts", MixedOccurrences),
    ("stock plan includes items disabled in other lists", StockPlan),
    ("unlock plan includes items disabled in other lists", UnlockPlan),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static GbrAutoGatherListAccess.ListSummary UserList(bool enabled = true)
    => new("User list", "", enabled, [(100u, 10u), (200u, 10u)], AllItemsEnabled: false)
    { DisabledItems = new HashSet<uint> { 100u } };

static HashSet<GatherProfileKind> StockScope() => [GatherProfileKind.Stock];

static void EqualIds(IEnumerable<uint> actual, params uint[] expected)
{
    var got = actual.ToHashSet();
    if (!got.SetEquals(expected))
        throw new Exception($"expected [{string.Join(',', expected.Order())}], got [{string.Join(',', got.Order())}]");
}

static void ProfileDisabledItems()
    => EqualIds(ProfileUpdateCycle.ConflictItems([UserList()], StockScope()), 200u);

static void UnvisitedDisabledItems()
    => EqualIds(UnvisitedPlan.ConflictingItemIds([UserList()], "Own", "[Own]"), 200u);

static void DisabledLists()
{
    EqualIds(ProfileUpdateCycle.ConflictItems([UserList(false)], StockScope()));
    EqualIds(UnvisitedPlan.ConflictingItemIds([UserList(false)], "Own", "[Own]"));
}

static GbrAutoGatherListAccess.ListSummary Managed(GatherProfileKind kind, uint enabledId, uint disabledId)
    => new(GatherProfiles.ListName(kind, 0, null), GatherProfiles.Tag(kind, 1), true,
        [(enabledId, 1u), (disabledId, 1u)], AllItemsEnabled: false)
    { DisabledItems = new HashSet<uint> { disabledId } };

static void ProfileScope()
{
    var snapshot = new[] { Managed(GatherProfileKind.Stock, 300, 301), Managed(GatherProfileKind.Unlock, 400, 401) };
    EqualIds(ProfileUpdateCycle.ConflictItems(snapshot, StockScope()), 400u);
    EqualIds(ProfileUpdateCycle.ConflictItems(snapshot, new HashSet<GatherProfileKind> { GatherProfileKind.Unlock }), 300u);
    EqualIds(ProfileUpdateCycle.ConflictItems(snapshot, new HashSet<GatherProfileKind> { GatherProfileKind.Unlock, GatherProfileKind.Stock }));
}

static void UnvisitedOwnership()
{
    var own = Managed(GatherProfileKind.Unlock, 300, 301);
    var renamedUser = new GbrAutoGatherListAccess.ListSummary(own.Name, "User notes", true, [(400u, 1u)]);
    var otherBand = own with { Name = "Other managed band", Entries = [(500u, 1u)] };
    EqualIds(UnvisitedPlan.ConflictingItemIds([own, renamedUser, otherBand], own.Name, own.Description), 400u, 500u);
}

static void MixedOccurrences()
{
    var enabledElsewhere = new GbrAutoGatherListAccess.ListSummary("Another list", "", true, [(100u, 1u)]);
    EqualIds(ProfileUpdateCycle.ConflictItems([UserList(), enabledElsewhere], StockScope()), 100u, 200u);
    EqualIds(UnvisitedPlan.ConflictingItemIds([UserList(), enabledElsewhere], "Own", "[Own]"), 100u, 200u);
}

static GatherableCatalog Catalog()
    => GatherableCatalog.BuildGatherables(new Dictionary<uint, GbrGatherableView>
    {
        [100] = new(100, "Disabled elsewhere", 1, 100, GatherableCatalog.GbrGatheringType.Mining, false, NodeKinds: MaterialNodeKinds.Regular),
        [200] = new(200, "Enabled elsewhere", 1, 200, GatherableCatalog.GbrGatheringType.Mining, false, NodeKinds: MaterialNodeKinds.Regular),
        [300] = new(300, "No other occurrence", 1, 300, GatherableCatalog.GbrGatheringType.Mining, false, NodeKinds: MaterialNodeKinds.Regular),
    });

static void StockPlan()
{
    var catalog = Catalog();
    var conflicts = ProfileUpdateCycle.ConflictItems([UserList()], StockScope());
    var rows = MaterialPlan.Stock(catalog.Entries, 100, _ => true,
        new Dictionary<uint, int> { [100] = 0, [200] = 0, [300] = 0 }, conflicts, 100);
    EqualIds(rows.Select(r => r.ItemId), 100u, 300u);
    if (rows.Any(r => r.Target != 100)) throw new Exception("stock quantity changed");
}

static void UnlockPlan()
{
    var conflicts = UnvisitedPlan.ConflictingItemIds([UserList()], "Own", "[Own]");
    var plan = UnvisitedPlan.Build(1, [new(GatherableCatalog.Job.Miner, new(0))], Catalog(),
        new GatheringCompletionReader(new UngatheredEffects()),
        new Dictionary<uint, uint> { [100] = 0, [200] = 0, [300] = 0 }, conflicts,
        UnvisitedFeature.ListNamePrefix, DateTime.UtcNow);
    EqualIds(plan.Lists.SelectMany(l => l.Entries).Select(e => e.ItemId), 100u, 300u);
    EqualIds(plan.Held.Where(e => e.Reason == UnvisitedPlan.HoldReason.ConflictOtherList).Select(e => e.Entry.ItemId), 200u);
}
