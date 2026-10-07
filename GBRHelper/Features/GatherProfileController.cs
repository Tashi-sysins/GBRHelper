using System;
using System.Collections.Generic;
using System.Linq;
using GBRHelper.Ipc;

namespace GBRHelper.Features;

/// <summary>
/// タブのチェックを保存し、「Auto-Gatherに追加」を押した機能のリストだけを、現在のキャラクター用に GBR へ反映する。
/// 要望：解放採取と全素材の補充の「Auto-Gatherに追加」は別物。押した機能のリストだけを書き換え、もう一方の機能のリストには触らない。
/// チェックは画面の選択だけ。押したときに「反映した帯」（GatherProfile.Applied）へ写し、リストはそれで作る（ログインし直したときも同じ）。
/// </summary>
public sealed class GatherProfileController(Configuration config, LiveCatalogBuilder builder,
    GatheringCompletionReader reader, GbrAutoGatherListAccess lists, GatherBuddyIpc gbr,
    GbrTimedAccess access, Func<bool> busy)
{
    /// <summary>採取中やほかの自動処理中で、チェックを変えられないときの文（指定の文）。</summary>
    public const string BusyText = "採取中、または自動処理中につき選択不可";

    private static readonly GatherProfileKind[] AllKinds = Enum.GetValues<GatherProfileKind>();

    public ulong Character { get; private set; }
    public ulong EditingCharacter { get; private set; }

    /// <summary>その回に GBR のリストを書き直す機能（「Auto-Gatherに追加」を押した機能・ログインしたときは全部）。空なら何もしない。</summary>
    private readonly HashSet<GatherProfileKind> scope = [];

    /// <summary>リストの書き直しを待っているか。</summary>
    public bool Pending => scope.Count > 0;

    /// <summary>その機能のリストの書き直しを待っているか（ベンチャー回収から戻る前に、全素材の補充の作り直しを待つため。StockFeature.ResumeWait）。</summary>
    public bool PendingFor(GatherProfileKind kind) => scope.Contains(kind);

    private readonly Dictionary<GatherProfileKind, string> statuses = new();
    private string status = "保存した採取リストを確認しています";

    /// <summary>最後の結果の文（ほかのキャラクターのタブに出す）。書くと、その回に書き直している機能の結果の文にもなる。</summary>
    public string Status
    {
        get => status;
        private set
        {
            status = value;
            foreach (var kind in scope) statuses[kind] = value;
        }
    }

    /// <summary>その機能の結果の文（各機能の「Auto-Gatherに追加」の下に出す。もう一方の機能の結果は出さない）。</summary>
    public string StatusOf(GatherProfileKind kind) => statuses.GetValueOrDefault(kind, "");

    /// <summary>その機能の結果の文を書く。current が false（ほかのキャラクターのタブ）なら、その機能の文は変えない。</summary>
    private void Report(GatherProfileKind kind, string text, bool current = true)
    {
        status = text;
        if (current) statuses[kind] = text;
    }

    public IReadOnlyDictionary<ulong, GatherProfile> Profiles => config.GatherProfiles;
    private DateTime nextAttempt, nextIdentityAttempt;
    // メモリから消えた後の保存失敗でも、同じ所有権で削除保存を再試行する。
    private ProfileListSync sync = new();
    private ProfileListSync removalSync = new();
    private ProfileUpdateCycle cycle = new();
    private readonly ProfileRetrySchedule retry = new();
    private GatherableCatalog? cachedCatalog;
    private bool firstObservation = true, inspectOnLoad, allowStop = true, appliedFilled;
    public bool IsWriting => cycle.IsWriting;

    /// <summary>
    /// GBR の自動採集中に「Auto-Gatherに追加」に乗せたときの文（指定の文。橙色で出す）。
    /// 自動採集中は、解放採取・全素材の補充・霊砂・クリスタルのどの「Auto-Gatherに追加」も押せない。
    /// </summary>
    public const string AutoGatheringText = "採取中につき操作を受け付けられません";

    public GatherProfile Get(ulong cid)
    {
        if (cid == 0) throw new InvalidOperationException("保存先のキャラクターを選んでください");
        // 新しいキャラクターは「反映した帯」が空（チェックしただけでは反映しない）。
        if (!config.GatherProfiles.TryGetValue(cid, out var profile)) config.GatherProfiles[cid] = profile = new() { AppliedUnlock = [], AppliedStock = [] };
        return profile;
    }
    public bool Selected(GatherProfileKind kind, GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => Get(EditingCharacter).Bands(kind).Contains(GatherProfiles.Key(job, band));
    public void Edit(ulong cid)
    {
        if (cid == 0 || cid != Character) throw new InvalidOperationException("現在ログイン中のキャラクターだけ操作できます");
        EditingCharacter = cid;
    }

    /// <summary>
    /// チェックを保存する。GBR には書かない（その機能の「Auto-Gatherに追加」で反映する）。
    /// 前のリストの記録（手で足した品・無効・目標）を捨てるのも、押したとき（Commit）に行う。チェックを外して入れ直しただけなら、何も変わらない。
    /// </summary>
    public void Set(GatherProfileKind kind, GatherableCatalog.Job job, GatherableCatalog.LevelBand band, bool selected)
    {
        var profile = Get(EditingCharacter);
        var bands = profile.Bands(kind);
        var key = GatherProfiles.Key(job, band);
        var old = bands.Contains(key);
        if (old == selected) return;
        var current = EditingCharacter == Character;
        // 反映した帯をまだ覚えていない前の設定は、チェックを変える前の状態で確定させる
        // （確定させないと「反映した帯＝いまのチェック」とみなされ、外したことが押したときに伝わらない）。
        GatherProfiles.FillApplied([profile]);
        if (selected) bands.Add(key); else bands.Remove(key);
        try { config.Save(); }
        catch (Exception ex)
        {
            if (old) bands.Add(key); else bands.Remove(key);
            Report(kind, "チェック内容を保存できません：" + ex.GetBaseException().Message, current);
            return;
        }
        Report(kind, current
            ? "チェックを保存しました。「Auto-Gatherに追加」を押すと GBR に反映します。"
            : "保存しました。このキャラクターのゲームで反映するときは「Auto-Gatherに追加」を押してください。", current);
    }

    /// <summary>
    /// 「Auto-Gatherに追加」（その機能のボタン）：その機能のチェックした帯のリストを GBR に作り、外した帯のリストを消す。もう一方の機能のリストには触らない。
    /// GBR の自動採集が止まっているときだけ受け付ける（決定「自動採集中は押せない」。
    /// 自動採集中にリストを変えると、予期しない・意図しない動きになりうるため）。画面のボタンも自動採集中は押せない。
    /// 14〜16時の版は、自動採集中に押すと新しい帯のリストを無効で足し、止まったら有効にしていた（同日 14時の指摘に合わせた作り。やめた）。
    /// </summary>
    public void Apply(GatherProfileKind kind)
    {
        if (Character == 0) { Report(kind, "ログインしてください"); return; }
        if (ApplyRefusal(gbr.IsAutoGatherEnabled()) is { } refusal) { Report(kind, refusal); return; }
        if (!Commit(kind, Character)) return;
        Request([kind]);
        allowStop = false;
        Report(kind, "GBR に反映しています…");
    }

    /// <summary>「Auto-Gatherに停止」の中身（Plugin が入れる：ベンチャー回収の見張りを止めて GBR の自動採集を OFF。結果の文を返す）。</summary>
    public Func<string>? StopHandler { get; set; }

    /// <summary>「Auto-Gatherに停止」を押せない理由（Plugin が入れる。押せるなら null）。</summary>
    public Func<string?>? StopBlock { get; set; }

    /// <summary>このゲームの「Auto-Gatherに停止」。結果はその機能の結果の文に出す。</summary>
    public void Stop(GatherProfileKind kind)
    {
        if (StopBlock?.Invoke() is { } block) { Report(kind, block); return; }
        Report(kind, StopHandler?.Invoke() ?? "止められません");
    }

    public static string? ApplyRefusal(bool? autoGatherEnabled) => autoGatherEnabled switch
    {
        null => "GatherBuddyReborn の状態を読めません",
        true => AutoGatheringText,
        false => null,
    };

    /// <summary>
    /// 「Auto-Gatherに追加」を押したとき、その機能のチェックを「反映した帯」として保存する（もう一方の機能には触らない）。
    /// 反映した帯から外した帯・新しく入れた帯（全素材の補充は、数・希望所持数を変えた帯も）は、前のリストの記録
    /// （手で足した品・無効・採集中に無効で足した印・全素材の補充の目標）を捨て、その時点から作り直す
    /// （外したあと入れ直しても、削除前の旧リストから再取込みしない・目標はその時点の所持数から決め直す）。
    /// 保存できなければ全部元に戻して false。
    /// </summary>
    public bool Commit(GatherProfileKind kind, ulong cid)
    {
        var profile = Get(cid);
        var unknown = profile.AppliedUnknown(kind);
        var before = new HashSet<int>(profile.Applied(kind));
        var next = profile.Bands(kind).Where(k => k is >= 0 and < 20).ToHashSet();
        var changed = new HashSet<int>(before);
        changed.SymmetricExceptWith(next);
        if (kind == GatherProfileKind.Stock) changed.UnionWith(profile.StockSettingsChanged.Where(next.Contains));

        // 元に戻すための写し
        var names = changed.ToDictionary(k => k, k => GatherProfiles.Name(kind, k, cid));
        var oldMemory = names.Values.ToDictionary(n => n, n => config.GatherListMemory.GetValueOrDefault(n));
        var oldReset = names.Values.Where(config.GatherListReset.Contains).ToHashSet();
        var oldAdded = names.Values.Where(config.GatherListAddedDisabled.Contains).ToHashSet();
        var oldTargets = changed.ToDictionary(k => k, k => profile.StockTargets.GetValueOrDefault(k));
        var oldSettingsChanged = new HashSet<int>(profile.StockSettingsChanged);
        var oldAppliedQuantities = new Dictionary<int, int>(profile.AppliedStockQuantities);
        var oldAppliedDesired = new HashSet<int>(profile.AppliedStockDesired);

        foreach (var (key, name) in names)
        {
            config.GatherListMemory.Remove(name);
            config.GatherListReset.Add(name);
            config.GatherListAddedDisabled.Remove(name);
            if (kind == GatherProfileKind.Stock) profile.StockTargets.Remove(key);
        }
        profile.SetApplied(kind, next);
        if (kind == GatherProfileKind.Stock)
        {
            profile.StockSettingsChanged.Clear();
            // 押した時点の数・希望所持数を覚える（リストの中身と名前はこれで作る。GatherProfile.AppliedQuantity/AppliedDesired）。
            profile.AppliedStockQuantities = next.ToDictionary(k => k, profile.StockQuantity);
            profile.AppliedStockDesired = next.Where(profile.StockDesiredTotal.Contains).ToHashSet();
        }
        try { config.Save(); return true; }
        catch (Exception ex)
        {
            profile.SetApplied(kind, unknown ? null : before);
            foreach (var (key, name) in names)
            {
                if (oldMemory[name] is { } memory) config.GatherListMemory[name] = memory;
                if (!oldReset.Contains(name)) config.GatherListReset.Remove(name);
                if (oldAdded.Contains(name)) config.GatherListAddedDisabled.Add(name);
                if (oldTargets[key] is { } targets) profile.StockTargets[key] = targets;
            }
            profile.StockSettingsChanged = oldSettingsChanged;
            profile.AppliedStockQuantities = oldAppliedQuantities;
            profile.AppliedStockDesired = oldAppliedDesired;
            Report(kind, "反映する帯を保存できません：" + ex.GetBaseException().Message, cid == Character);
            return false;
        }
    }
    /// <summary>編集中のキャラクターの、全素材の補充のその帯の「さらに採る数」。</summary>
    public int Quantity(GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => Get(EditingCharacter).StockQuantity(GatherProfiles.Key(job, band));

    /// <summary>編集中のキャラクターの、全素材の補充のその帯で決めた品ごとの目標（まだリストを作っていなければ null）。</summary>
    public IReadOnlyDictionary<uint, uint>? StockTargets(GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => Get(EditingCharacter).StockTargets.GetValueOrDefault(GatherProfiles.Key(job, band));

    /// <summary>
    /// 編集中のキャラクターの、全素材の補充のその帯の「さらに採る数」を保存する。
    /// 数はチェックを入れた時点で GBR のリストに使う。チェックが入っている帯は変えない（画面でも入力欄を止める）。
    /// 変えるときは、チェックを外して（リストを消して）から数を変え、もう一度チェックを入れる。
    /// </summary>
    public bool SetQuantity(GatherableCatalog.Job job, GatherableCatalog.LevelBand band, int quantity)
    {
        var profile = Get(EditingCharacter);
        var key = GatherProfiles.Key(job, band);
        if (profile.Stock.Contains(key)) return false;
        var value = GatherProfiles.ClampQuantity(quantity);
        var had = profile.StockQuantities.TryGetValue(key, out var old);
        if (profile.StockQuantity(key) == value) return true;
        profile.StockQuantities[key] = value;
        // 次に「Auto-Gatherに追加」を押したとき、この帯のリストを新しい数で作り直す（反映した帯のままでも）。
        var wasChanged = !profile.StockSettingsChanged.Add(key);
        try { config.Save(); }
        catch (Exception ex)
        {
            if (had) profile.StockQuantities[key] = old; else profile.StockQuantities.Remove(key);
            if (!wasChanged) profile.StockSettingsChanged.Remove(key);
            Report(GatherProfileKind.Stock, "数を保存できません：" + ex.GetBaseException().Message, EditingCharacter == Character);
            return false;
        }
        return true;
    }

    /// <summary>編集中のキャラクターの、全素材の補充のその帯の「希望所持数」のチェック。</summary>
    public bool DesiredTotal(GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => Get(EditingCharacter).StockDesiredTotal.Contains(GatherProfiles.Key(job, band));

    /// <summary>
    /// 「希望所持数」のチェックを保存する。帯にチェックが入っている間は変えない（数の欄と同じ）。
    /// </summary>
    public bool SetDesiredTotal(GatherableCatalog.Job job, GatherableCatalog.LevelBand band, bool desired)
    {
        var profile = Get(EditingCharacter);
        var key = GatherProfiles.Key(job, band);
        if (profile.Stock.Contains(key)) return false;
        if (profile.StockDesiredTotal.Contains(key) == desired) return true;
        if (desired) profile.StockDesiredTotal.Add(key); else profile.StockDesiredTotal.Remove(key);
        // 次に「Auto-Gatherに追加」を押したとき、この帯のリストを作り直す（数と同じ）。
        var wasChanged = !profile.StockSettingsChanged.Add(key);
        try { config.Save(); }
        catch (Exception ex)
        {
            if (desired) profile.StockDesiredTotal.Remove(key); else profile.StockDesiredTotal.Add(key);
            if (!wasChanged) profile.StockSettingsChanged.Remove(key);
            Report(GatherProfileKind.Stock, "希望所持数を保存できません：" + ex.GetBaseException().Message, EditingCharacter == Character);
            return false;
        }
        return true;
    }

    /// <summary>その機能（kinds。省略で全部）のリストの書き直しを頼む。ほかの機能の書き直しを待っていれば、それも続ける。</summary>
    public void Request(IEnumerable<GatherProfileKind>? kinds = null)
    {
        var added = (kinds ?? AllKinds).ToArray();
        scope.UnionWith(added);
        inspectOnLoad = false; allowStop = true;
        cachedCatalog = null; retry.Reset(); nextAttempt = default;
        foreach (var kind in added) Report(kind, "保存済み。計画を確認してからリストへ反映します。");
    }

    /// <summary>その機能のリストを、GBR が止まっているときだけ作り直す（GBR は止めない。解放採取の対象品がなくなった帯の片付け）。</summary>
    public void RefreshWhenStopped(GatherProfileKind kind)
    {
        if (ProfileUpdateCycle.RefreshMayRequest(gbr.IsAutoGatherEnabled()))
        { Request([kind]); allowStop = false; }
    }

    /// <summary>書き直しを終える（結果の文を、書き直した機能に出してから範囲を空にする）。</summary>
    private void Finish(string text)
    {
        Status = text;
        scope.Clear();
    }
    public string Tag(GatherProfileKind kind) => GatherProfiles.Tag(kind, Character);
    public string Name(GatherProfileKind kind, GatherableCatalog.Job job, GatherableCatalog.LevelBand band)
        => GatherProfiles.Name(kind, GatherProfiles.Key(job, band), Character);

    public const string StoppedText = "GBR の自動採集は止まっています";
    public void ResetCharacter()
    {
        Character = EditingCharacter = 0; scope.Clear(); statuses.Clear();
        sync = new(); removalSync = new(); cycle = new(); retry.Reset();
        nextAttempt = nextIdentityAttempt = default; cachedCatalog = null;
        firstObservation = true; inspectOnLoad = false; allowStop = true; appliedFilled = false;
        desiredBasis = new();
        reader.InvalidateAll();
    }

    public void Tick()
    {
        // この仕組みより前の設定は、反映した帯をチェックで埋める（一度だけ。そのころはチェックがそのまま反映されていた）。
        if (!appliedFilled)
        {
            appliedFilled = true;
            if (GatherProfiles.FillApplied(config.GatherProfiles.Values))
            {
                try { config.Save(); }
                catch (Exception ex) { Svc.Log.Warning($"[GBRHelper] 反映した帯を保存できません（次に保存するときに書きます）: {ex.GetBaseException().Message}"); }
            }
        }

        var cid = Svc.PlayerState.IsLoaded ? Svc.PlayerState.ContentId : 0;
        if (Character != cid)
        {
            Character = cid; EditingCharacter = cid; nextIdentityAttempt = default; reader.InvalidateAll(); Request();
            inspectOnLoad = firstObservation && cid != 0;
            allowStop = !inspectOnLoad;
        }
        firstObservation = false;
        if (cid == 0) return;
        try
        {
            var player = Svc.Objects.LocalPlayer;
            if (player is not null && DateTime.UtcNow >= nextIdentityAttempt)
            {
                nextIdentityAttempt = DateTime.UtcNow.AddSeconds(2);
                var profile = Get(cid);
                var name = player.Name.ToString(); var world = player.HomeWorld.Value.Name.ToString();
                if (profile.Name != name || profile.World != world)
                {
                    var oldName = profile.Name; var oldWorld = profile.World;
                    profile.Name = name; profile.World = world;
                    try { config.Save(); } catch { profile.Name = oldName; profile.World = oldWorld; throw; }
                }
            }
            if (!Pending || DateTime.UtcNow < nextAttempt) return;
            nextAttempt = DateTime.UtcNow.AddSeconds(2);
            var snapshot = lists.ListAll() ?? throw new InvalidOperationException(lists.LastError);
            var owned = snapshot.Select(GatherProfiles.Identify).OfType<GatherProfiles.Owned>().ToArray();
            // その回に書き直す機能のリスト（押していない機能のリストには触らない）。別のキャラクターのリストは機能によらず消す。
            var ownedInScope = owned.Where(x => scope.Contains(x.Kind)).ToArray();
            var wanted = scope.Any(k => GatherProfiles.Effective(Profiles, cid, k).Count > 0);
            if (!wanted && ownedInScope.Length == 0 && !owned.Any(x => x.Character != 0 && x.Character != cid)
                && !sync.HasPendingDeletes && !removalSync.HasPendingDeletes)
            { Finish("GBR に追加するリストはありません。"); return; }
            if (inspectOnLoad && !HasPendingMemory() && !HasResetForCurrent() && !sync.HasPendingDeletes && !removalSync.HasPendingDeletes
                && ProfileUpdateCycle.ReuseOnLoad(Profiles, cid, snapshot))
            {
                CaptureEdits(cid, snapshot);
                inspectOnLoad = false;
                Finish("現在のキャラクターの登録済みリストをそのまま使用します。");
                return;
            }
            var obsolete = ProfileUpdateCycle.Obsolete(owned, cid, Profiles, config.GatherProfilesMigrated, scope);
            var cleanup = obsolete.Length > 0 || removalSync.HasPendingDeletes;
            // 「Auto-Gatherに追加」は GBR が止まっているときだけ押せるが、押した直後に GBR が動き出したときなどは、GBR を止めずに止まるまで待つ。
            // 別のキャラクターのリストだけは、その人の品を採ってしまうので、従来どおり採集の切れ目で止めて消す。
            var otherCharacter = obsolete.Any(x => x.Character != 0 && x.Character != cid);
            if (ProfileUpdateCycle.WaitWhileGathering(gbr.IsAutoGatherEnabled(), allowStop, otherCharacter))
            {
                Status = "反映を待っています：" + WaitReason();
                return;
            }
            var plans = new List<ProfileListSync.Plan>();
            var generated = new List<ProfileListSync.Plan>();
            var result = cycle.Run(cleanup, busy(), allowStop,
                () =>
                {
                    Migrate(cid, owned); CaptureEdits(cid, snapshot);
                    generated = Build(cid, snapshot); // GBRはまだ止めない。読取りだけを先に完了する。
                    plans = MergeEdits(cid, generated);
                    if (!HasResetForCurrent() && !sync.HasPendingDeletes && ProfileUpdateCycle.SamePlans(plans, ProfileUpdateCycle.InScope(snapshot, scope)))
                    { CommitGenerated(cid, generated); Finish("リストの内容は一致しています。作り直しは不要です。"); return false; }
                    return true;
                },
                // GBR が止まっていれば、リストを書くのに採集の切れ目は待たない（GbrTimedAccess.CanWriteListsWhileStopped）。
                () => gbr.IsAutoGatherEnabled() == false ? access.CanWriteListsWhileStopped : access.SafeBoundary, gbr.IsAutoGatherEnabled,
                () => { gbr.SetAutoGatherEnabled(false); Status = "計画を確認しました。採集の切れ目でGBRを停止しています。"; },
                () =>
                {
                    EnsureStopped(cid);
                    if (cleanup)
                    {
                        Migrate(cid, owned); CaptureEdits(cid, snapshot);
                        removalSync.Apply(obsolete, [], () => EnsureStopped(cid), lists.RemoveManagedList, _ => true);
                        Status = "解除した選択・別キャラクターのリストを削除しました。残りの計画を確認します。";
                        nextAttempt = default;
                        return;
                    }
                    // 書込みの途中で失敗しても、生成した品を手動追加と誤認しないよう先に記録する。
                    RememberGenerated(generated);
                    // 消して作り直すのは、その回に書き直す機能のリストだけ（もう一方の機能のリストは残す）。
                    sync.Apply(ownedInScope, plans, () => EnsureStopped(cid), lists.RemoveManagedList,
                        plan => lists.WriteManagedList(plan.GbrName, plan.Tag, plan.Entries, plan.Enabled, plan.UsesRetainerInventory).Ok);
                    CommitGenerated(cid, generated);
                    inspectOnLoad = false; retry.Reset();
                    Finish($"GBR の Auto-Gather に反映しました（{plans.Count}リスト）。採集する場合はGBRをONにしてください。");
                });
            if (result.Error.Length != 0)
            {
                nextAttempt = DateTime.UtcNow + retry.Failed();
                Status = "計画を作れません（GBRとベンチャー回収は停止しません）：" + result.Error;
            }
            else if (result.Action == ProfileUpdateCycle.Step.Wait && Pending)
            {
                // 待っている理由を出す（以前は理由を出さずに待ち続け、反映されない理由が分からなかった。2026-10-05）。
                Status = "反映を待っています：" + WaitReason();
            }
        }
        catch (Exception ex)
        {
            // 書き直しを待っている機能（scope）はそのまま残し、間を空けて試し直す。
            nextAttempt = DateTime.UtcNow + retry.Failed();
            Status = "リストの反映待ち：" + ex.GetBaseException().Message;
        }
    }

    private void Migrate(ulong cid, IReadOnlyList<GatherProfiles.Owned> owned)
    {
        if (!config.GatherProfilesMigrated)
        {
            foreach (var old in owned.Where(x => x.Legacy && (x.Character == 0 || x.Character == cid)))
            {
                var owner = old.Character == 0 ? cid : old.Character;
                if (!config.GatherListReset.Contains(GatherProfiles.Name(old.Kind, old.Key, owner)))
                {
                    // 旧リストはそのころ反映されていたものなので、チェックと「反映した帯」の両方に取り込む。
                    var profile = Get(owner);
                    profile.Bands(old.Kind).Add(old.Key);
                    profile.Applied(old.Kind).Add(old.Key);
                }
            }
            config.GatherProfilesMigrated = true;
            try { config.Save(); } catch { config.GatherProfilesMigrated = false; throw; }
        }
    }

    private void SaveMemory(Dictionary<string, ProfileListMemory> next, HashSet<string>? resets = null)
    {
        resets ??= new(config.GatherListReset);
        if (resets.SetEquals(config.GatherListReset) && next.Count == config.GatherListMemory.Count && next.All(p => config.GatherListMemory.TryGetValue(p.Key, out var old) && p.Value.Same(old))) return;
        var before = config.GatherListMemory; var beforeReset = config.GatherListReset;
        config.GatherListMemory = next; config.GatherListReset = resets;
        try { config.Save(); } catch { config.GatherListMemory = before; config.GatherListReset = beforeReset; throw; }
    }
    private void CaptureEdits(ulong cid, IReadOnlyList<GbrAutoGatherListAccess.ListSummary> snapshot)
    {
        var next = config.GatherListMemory.ToDictionary(p => p.Key, p => p.Value.Copy());
        foreach (var list in snapshot)
        {
            var owned = GatherProfiles.Identify(list);
            if (owned is null || (owned.Character != 0 && owned.Character != cid)) continue;
            var name = GatherProfiles.Name(owned.Kind, owned.Key, owned.Character == 0 ? cid : owned.Character);
            // 前の版（2026-10-05 14〜16時）で、自動採集中にこちらが無効で足したリストは、無効を利用者が選んだこととして覚えない
            // （GBR が止まって書き直すときに有効にするため。いまは自動採集中に押せないので、新しく印は付かない）。
            next[name] = ProfileListMemory.Capture(next.GetValueOrDefault(name), list, config.GatherListReset.Contains(name),
                config.GatherListAddedDisabled.Contains(name));
        }
        SaveMemory(next);
    }

    private List<ProfileListSync.Plan> MergeEdits(ulong cid, IReadOnlyList<ProfileListSync.Plan> generated)
    {
        var plans = new List<ProfileListSync.Plan>();
        foreach (var kind in ScopeInOrder())
            foreach (var key in GatherProfiles.Effective(Profiles, cid, kind).Order())
            {
                var name = GatherProfiles.Name(kind, key, cid);
                var original = generated.FirstOrDefault(p => p.Name == name)
                    ?? new(name, Tag(kind), [], UsesRetainerInventory: ProfileUpdateCycle.UsesRetainers(kind));
                // GBR に作る名前は短い形（GatherProfiles.ListName）。記録の鍵は name のまま。
                var merged = ProfileListMemory.Merge(original, config.GatherListMemory.GetValueOrDefault(name))
                    with { ListName = GatherProfiles.ListName(kind, key, Get(cid)) };
                if (merged.Entries.Count > 0) plans.Add(merged);
            }
        return plans;
    }
    private void RememberGenerated(IReadOnlyList<ProfileListSync.Plan> generated)
    {
        var next = config.GatherListMemory.ToDictionary(p => p.Key, p => p.Value.Copy());
        foreach (var plan in generated)
        {
            if (!next.TryGetValue(plan.Name, out var memory)) next[plan.Name] = memory = new();
            // 過去の生成IDも残す。途中失敗で旧リストが残っていても手動品にしない。
            memory.PendingGeneratedIds.UnionWith(plan.Entries.Select(e => e.ItemId));
        }
        SaveMemory(next);
    }
    private void CommitGenerated(ulong cid, IReadOnlyList<ProfileListSync.Plan> generated)
    {
        var next = config.GatherListMemory.ToDictionary(p => p.Key, p => p.Value.Copy());
        var resets = new HashSet<string>(config.GatherListReset);
        // その回に書き直した機能のリストの記録だけを確定する（書き直していない機能の記録を、空の生成で上書きしない）。
        foreach (var kind in ScopeInOrder())
            foreach (var key in GatherProfiles.Effective(Profiles, cid, kind))
            {
                var name = GatherProfiles.Name(kind, key, cid);
                if (!next.TryGetValue(name, out var memory)) next[name] = memory = new();
                memory.GeneratedIds = generated.FirstOrDefault(p => p.Name == name)?.Entries.Select(e => e.ItemId).ToHashSet() ?? [];
                memory.PendingGeneratedIds.Clear();
                resets.Remove(name);
            }
        SaveMemory(next, resets);

        // 採集中に無効で足したリストは、いま有効にして書き終えたので印を外す（外せなくても、次に書き終えたときにまた外す）。
        var current = ScopeInOrder().SelectMany(kind =>
            GatherProfiles.Effective(Profiles, cid, kind).Select(key => GatherProfiles.Name(kind, key, cid))).ToHashSet(StringComparer.Ordinal);
        if (config.GatherListAddedDisabled.RemoveWhere(current.Contains) > 0)
        {
            try { config.Save(); }
            catch (Exception ex) { Svc.Log.Warning($"[GBRHelper] 無効で足したリストの印を保存できません: {ex.GetBaseException().Message}"); }
        }
    }
    private bool HasPendingMemory() => ScopeInOrder().Any(kind =>
        GatherProfiles.Effective(Profiles, Character, kind).Any(key => config.GatherListMemory.GetValueOrDefault(
            GatherProfiles.Name(kind, key, Character))?.PendingGeneratedIds.Count > 0));
    private bool HasResetForCurrent() => ScopeInOrder().Any(kind =>
        GatherProfiles.Effective(Profiles, Character, kind).Any(key => config.GatherListReset.Contains(GatherProfiles.Name(kind, key, Character))));

    /// <summary>その回に書き直す機能を、解放採取 → 全素材の補充の順で（同じ品は先に作った解放採取のリストに入れる）。</summary>
    private GatherProfileKind[] ScopeInOrder() => AllKinds.Where(scope.Contains).ToArray();
    private bool ListEnabled(string name) => config.GatherListMemory.GetValueOrDefault(name)?.Enabled ?? true;

    private void EnsureStopped(ulong cid)
    {
        if (Svc.PlayerState.ContentId != cid || !Svc.PlayerState.IsLoaded || gbr.IsAutoGatherEnabled() != false || busy() || !access.CanWriteListsWhileStopped)
            throw new InvalidOperationException("キャラクターと停止状態を確認できません");
    }

    /// <summary>リストへの反映を待っている理由。</summary>
    private string WaitReason()
    {
        if (busy()) return "ほかの自動処理が動いています";
        var enabled = gbr.IsAutoGatherEnabled();
        if (enabled is null) return "GBR の状態を読めません";
        if (enabled == true) return allowStop ? "採集の切れ目で GBR の自動採集を止めます" + (access.BoundaryProblem() is { } p ? $"（いま：{p}）" : "")
            : "GBR の自動採集が止まったら反映します";
        return access.CanWriteListsWhileStopped ? "準備中です" : "キャラクターを読めません";
    }
    private List<ProfileListSync.Plan> Build(ulong cid, IReadOnlyList<GbrAutoGatherListAccess.ListSummary> snapshot)
    {
        // その回に書き直す機能のリストだけを作る（押していない機能のリストは作らない・触らない）。
        var unlock = scope.Contains(GatherProfileKind.Unlock) ? GatherProfiles.Effective(Profiles, cid, GatherProfileKind.Unlock) : [];
        var stock = scope.Contains(GatherProfileKind.Stock) ? GatherProfiles.Effective(Profiles, cid, GatherProfileKind.Stock) : [];
        if (unlock.Count + stock.Count == 0) return [];
        var catalog = cachedCatalog ??= builder.Build() ?? throw new InvalidOperationException(builder.LastError);
        reader.InvalidateAll();
        // 利用者の有効なリストと、書き直さない機能のこちらの有効なリストにある品は入れない（GBR は同じ品の目標数を足すため）。
        var conflicts = ProfileUpdateCycle.ConflictItems(snapshot, scope);
        // 手動追加は、その所有リストでだけ復元する。別の自動生成リストへ重複登録しない。
        foreach (var kind in ScopeInOrder())
            foreach (var key in GatherProfiles.Effective(Profiles, cid, kind))
            {
                var name = GatherProfiles.Name(kind, key, cid);
                if (config.GatherListMemory.TryGetValue(name, out var memory) && memory.Enabled) conflicts.UnionWith(memory.ManualRows.Keys);
            }
        var result = new List<ProfileListSync.Plan>();
        if (unlock.Count > 0)
        {
            // ベンチャーで依頼できる品だけに固定。依頼品を読めなければ作らない。
            const bool ventureOnly = UnvisitedFeature.VentureRequestOnly;
            if (ventureOnly && !catalog.VentureInfoAvailable)
                throw new InvalidOperationException("ベンチャーの依頼品を確認できません");
            var selections = unlock.Order().Select(k => { var (job, band) = GatherProfiles.Decode(k); return new UnvisitedPlan.Selection(job, band); }).ToArray();
            var baseline = new Dictionary<uint, uint>();
            foreach (var selection in selections)
                foreach (var e in catalog.InBand(selection.Job, selection.Band))
                {
                    if (ventureOnly && !e.VentureRequestable) continue;
                    var state = reader.Query(e);
                    if (state == GatheringCompletionReader.State.Unknown) throw new InvalidOperationException("未採取の履歴を確認できません");
                    if (state != GatheringCompletionReader.State.Ungathered || reader.FolkloreOk(e) != true || conflicts.Contains(e.ItemId)) continue;
                    if (!baseline.ContainsKey(e.ItemId)) baseline[e.ItemId] = checked((uint)(lists.GetTotalCount(e.ItemId)
                        ?? throw new InvalidOperationException(lists.LastError)));
                }
            var plan = UnvisitedPlan.Build(cid, selections, catalog, reader, baseline, conflicts, GatherProfiles.Prefix(cid), DateTime.UtcNow, ventureOnly);
            foreach (var list in plan.Lists)
            {
                var entries = list.Entries.Select(e => (e.ItemId, e.TargetQuantity)).ToList();
                if (entries.Count == 0) continue;
                result.Add(new(list.Name, Tag(GatherProfileKind.Unlock), entries));
                if (ListEnabled(list.Name)) conflicts.UnionWith(entries.Select(e => e.ItemId));
            }
        }
        if (stock.Count > 0)
        {
            var levels = Enum.GetValues<GatherableCatalog.Job>().ToDictionary(j => j, MaterialInventory.Level);
            var candidates = stock.Order().SelectMany(k => { var (j, b) = GatherProfiles.Decode(k); return catalog.InBand(j, b); })
                .Where(e => MaterialPlan.CanStock(e, levels[e.Job], reader.FolkloreOk)).ToArray();
            // 所持数はいま本人の鞄だけで数える（GatherProfiles.StockUsesRetainers）。
            var inventory = new MaterialInventory(lists);
            var counts = inventory.Read(candidates.Select(e => e.ItemId), GatherProfiles.StockUsesRetainers)
                ?? throw new InvalidOperationException(inventory.Error);
            var profile = Get(cid);
            var decided = new List<(int Key, uint ItemId, uint Target)>();
            var basis = new Dictionary<int, Dictionary<uint, DesiredItemBasis>>();
            // リテイナーの数は、希望所持数の帯があるときだけ、1 回だけまとめて読む（読めなければ例外＝その回は作らない）。
            Dictionary<uint, int>? retainerCounts = null;
            foreach (var key in stock.Order())
            {
                var (job, band) = GatherProfiles.Decode(key);
                // 数と「希望所持数」は、「Auto-Gatherに追加」を押した時点のもの（GatherProfile.AppliedQuantity/AppliedDesired）。
                // チェックを外して数を変えたまま押していない帯を、ログインし直したときなどに新しい数で作らないため。
                // 「希望所持数」の帯は、決めた目標を使わず、毎回いまのリテイナーの数から決め直す（2026-10-07。MaterialPlan.StockBand）。
                // それ以外の帯は、初めて目標を決めた品を覚える（チェックを入れた時点の所持数＋さらに採る数。作り直しでも同じ目標を使うため）。
                var desired = profile.AppliedDesired(key);
                var plan = MaterialPlan.StockBand(catalog.InBand(job, band), levels[job], reader.FolkloreOk, counts, conflicts,
                    profile.AppliedQuantity(key), desired, desired ? null : profile.StockTargets.GetValueOrDefault(key),
                    () => retainerCounts ??= AllaganRetainerCounter.ReadAll());
                decided.AddRange(plan.Decided.Select(d => (key, d.ItemId, d.Target)));
                if (plan.Basis is not null) basis[key] = plan.Basis;
                var rows = plan.Rows;
                if (rows.Count == 0) continue;
                result.Add(new(GatherProfiles.Name(GatherProfileKind.Stock, key, cid), Tag(GatherProfileKind.Stock),
                    rows.Select(r => (r.ItemId, r.Target)).ToList(), UsesRetainerInventory: GatherProfiles.StockUsesRetainers));
                if (ListEnabled(GatherProfiles.Name(GatherProfileKind.Stock, key, cid))) conflicts.UnionWith(rows.Select(r => r.ItemId));
            }
            RememberStockTargets(profile, decided);
            desiredBasis = basis;
        }
        return result;
    }

    /// <summary>
    /// 希望所持数の帯の、いまの GBR のリストを作ったときの数（キーは GatherProfiles.Key。StockFeature が見張りに使う）。
    /// 補充のリストを作り直すたびに置き換える（Build）。ログインし直したとき・プラグインを読み直したときは空。
    /// </summary>
    private Dictionary<int, Dictionary<uint, DesiredItemBasis>> desiredBasis = new();

    /// <summary>その帯の、いまの GBR のリストを作ったときの数（まだ作っていなければ null）。</summary>
    public IReadOnlyDictionary<uint, DesiredItemBasis>? DesiredBasis(int key) => desiredBasis.GetValueOrDefault(key);

    /// <summary>
    /// 初めて決めた全素材の補充の目標を保存する。保存できなければ元に戻して例外にする
    /// （目標を覚えないままリストを書くと、次の作り直しで目標が先へ逃げるため、書込みへ進ませない）。
    /// </summary>
    private void RememberStockTargets(GatherProfile profile, IReadOnlyList<(int Key, uint ItemId, uint Target)> decided)
    {
        if (decided.Count == 0) return;
        var before = profile.StockTargets.ToDictionary(p => p.Key, p => new Dictionary<uint, uint>(p.Value));
        foreach (var (key, item, target) in decided)
        {
            if (!profile.StockTargets.TryGetValue(key, out var map)) profile.StockTargets[key] = map = new();
            map[item] = target;
        }
        try { config.Save(); }
        catch { profile.StockTargets = before; throw; }
    }
}
