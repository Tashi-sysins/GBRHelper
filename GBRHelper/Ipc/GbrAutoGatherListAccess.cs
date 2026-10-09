using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GBRHelper.Ipc;

/// <summary>GBR 自身の「Artisan から読み込む」で作られたリストを扱う口（ArtisanImportCleaner が使う。試験では偽物に差し替える）。</summary>
public interface IGbrArtisanImports
{
    IReadOnlyList<GbrAutoGatherListAccess.ImportedList>? ArtisanImportedLists(out object? manager);

    int? SavedArtisanImportCount(string name);

    List<uint>? RemoveItems(object list, Func<uint, bool> remove);

    string LastError { get; }
}

/// <summary>
/// GatherBuddyReborn（GBR）の自動採集リスト（AutoGatherList / AutoGatherListsManager）を
/// リフレクションで触る窓口。
///
/// 【なぜ AutoGatherList か】
/// GBR の自動採集が実際に見るのは AutoGatherListsManager.ActiveItems であり、その元が
/// AutoGatherList（Items・Quantities・EnabledItems・Fallback・Enabled）である。
/// 別クラスの GatherWindowPreset は数量を持たず、自動採集には使われない（表示用）。
/// 元資料で扱っていた GatherWindowPreset はこの目的では使えないため、こちらを使う。
///
/// 【経路】
///   GbrConfigAccess.Plugin()
///     → GatherBuddy.GatherBuddy インスタンス
///     → internal フィールド AutoGatherListsManager（GatherBuddy.cs:96）
///     → public IEnumerable&lt;AutoGatherList&gt; Lists
///     → AutoGatherList.Add(IGatherable, uint quantity)
///     → AutoGatherListsManager.AddList / DeleteList（内部で Save & SetActiveItems）
///
/// 【IGatherable の型】
///   指示書で指摘されたとおり、IGatherable は GatherBuddy.GameData 側（別アセンブリ）に
///   ある可能性が高い。GBR 本体アセンブリ 1 本から全型を取れると仮定せず、必要メソッドの
///   引数型・実際にロード済みのアイテムインスタンスから型を取る。
///
/// 【書込みの原則】（指示書「リフレクションと書込みの保護」）
///   1. 旧リストを消してから組立を始めない。先に全アイテムを解決し、新リストをメモリで作る。
///   2. Invoke が例外なく終わっただけで保存成功と見なさない。保存ファイルで対象エントリを確認する。
///   3. 稼働中の JSON を直接書き換えない（本クラスはすべて反射で呼ぶ）。
///   4. 名前の接頭辞だけで所有権を判断しない（Description に Helper の管理タグを埋め込む）。
///   5. GBR 自動採集が false かつ Relay 非活動のときだけ呼ばれる前提（呼び出し側で保証）。
/// </summary>
public sealed class GbrAutoGatherListAccess : IGbrArtisanImports
{
    private const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static;

    private readonly GbrConfigAccess shared;

    /// <summary>直近の失敗。無ければ空。</summary>
    public string LastError { get; private set; } = string.Empty;

    public GbrAutoGatherListAccess(GbrConfigAccess shared)
    {
        this.shared = shared;
    }

    // ------------------------------------------------------------------
    // 公開 API

    /// <summary>GBR のリテイナー在庫参照設定。読取り不能は null。</summary>
    public bool? GetCheckRetainers() => this.shared.GetBool("CheckRetainers");

    /// <summary>
    /// GBR 自身の数量判定を呼ぶ。新規管理リストと同じ useRetainerInventory=true を使う。
    /// 型やアイテムを保持せず、再ロード後も現在の GBR から取り直す。取得不能は 0 と区別する。
    /// </summary>
    public int? GetTotalCount(uint itemId)
    {
        try
        {
            if (!Svc.Framework.IsInFrameworkUpdateThread || Svc.PlayerState.ContentId == 0)
            {
                this.LastError = "ログイン中のゲーム更新スレッドでのみ所持数を取得できます";
                return null;
            }

            if (this.shared.Plugin() is not { } plugin)
            {
                this.LastError = this.shared.LastError;
                return null;
            }

            var dictionary = this.GatherablesDictionary();
            if (dictionary is null) return null;
            var item = TryGetItemFromDictionary(dictionary, itemId);
            if (item is null)
            {
                this.LastError = $"GBR にアイテム {itemId} が見つからず、所持数を取得できません";
                return null;
            }

            // 全アセンブリ検索はしない。古い GBR のロード文脈と混ぜないため。
            var type = plugin.GetType().Assembly.GetType("GatherBuddy.AutoGather.Extensions.GatherableExtensions");
            var method = type?.GetMethods(PubStatic).SingleOrDefault(m =>
            {
                var args = m.GetParameters();
                return m.Name == "GetTotalCount" && m.ReturnType == typeof(int)
                    && args.Length == 2 && args[0].ParameterType.IsInstanceOfType(item)
                    && args[1].ParameterType == typeof(bool);
            });
            if (method is null)
            {
                this.LastError = "GBR の GetTotalCount(IGatherable, bool) が見つかりません（版の違いを確認してください）";
                return null;
            }

            if (method.Invoke(null, [item, true]) is not int count || count < 0)
            {
                this.LastError = $"GBR がアイテム {itemId} の有効な所持数を返しませんでした";
                return null;
            }

            this.LastError = string.Empty;
            return count;
        }
        catch (Exception ex)
        {
            this.LastError = $"アイテム {itemId} の所持数を取得できません: {ex.GetBaseException().Message}";
            return null;
        }
    }

    /// <summary>
    /// GBR の自動採集リスト一覧（名前と内容）を返す。
    /// GBR 未起動なら null、1 件も無ければ空リスト。
    /// </summary>
    public IReadOnlyList<ListSummary>? ListAll()
    {
        try
        {
            if (this.Manager() is not { } mgr)
                return null;

            var lists = mgr.GetType().GetProperty("Lists", PubInst)?.GetValue(mgr) as IEnumerable;
            if (lists is null)
            {
                this.LastError = "GBR の AutoGatherListsManager.Lists に届きません";
                return null;
            }

            var result = new List<ListSummary>();
            foreach (var list in lists)
            {
                if (list is null)
                    continue;

                var t = list.GetType();
                var name = t.GetProperty("Name", PubInst)?.GetValue(list) as string ?? "(無題)";
                var description = t.GetProperty("Description", PubInst)?.GetValue(list) as string ?? "";
                var enabled = t.GetProperty("Enabled", PubInst)?.GetValue(list) as bool? ?? false;

                var items = t.GetProperty("Items", PubInst)?.GetValue(list) as IEnumerable;
                var quantitiesObj = t.GetProperty("Quantities", PubInst)?.GetValue(list) as IDictionary;

                var entries = new List<(uint ItemId, uint Quantity)>();
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item?.GetType().GetProperty("ItemId", PubInst)?.GetValue(item) is not uint id)
                            continue;
                        uint q = 1;
                        if (quantitiesObj != null && quantitiesObj.Contains(item))
                        {
                            if (quantitiesObj[item] is uint qq)
                                q = qq;
                        }
                        entries.Add((id, q));
                    }
                }

                var flags = t.GetProperty("EnabledItems", PubInst)?.GetValue(list) as IDictionary;
                var allItemsEnabled = flags is not null && items is not null && items.Cast<object>().All(i => flags.Contains(i) && flags[i] is true);
                var disabled = flags is null || items is null ? new HashSet<uint>()
                    : items.Cast<object>().Where(i => !(flags.Contains(i) && flags[i] is true))
                        .Select(i => i.GetType().GetProperty("ItemId", PubInst)?.GetValue(i)).OfType<uint>().ToHashSet();
                var fallback = t.GetProperty("Fallback", PubInst)?.GetValue(list) as bool? ?? true;
                var usesRetainers = t.GetProperty("UsesRetainerInventory", AnyInst)?.GetValue(list) as bool? ?? true;
                result.Add(new ListSummary(name, description, enabled, entries, allItemsEnabled, fallback, usesRetainers) { DisabledItems = disabled });
            }

            this.LastError = string.Empty;
            return result;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の自動採集リストを読めません: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Helper が作る管理リストを、指定された内容で作成または更新する。
    /// すでに同名の Helper 管理リストがあれば削除してから作り直す。
    /// 名前が同じでも、Helper の管理タグが Description に無ければ「ユーザー作成」とみなし触らない。
    ///
    /// 【処理の順序】
    ///  1. 全アイテム ID の解決を先に試す（1 つでも失敗したらリスト作成に進まない）
    ///  2. メモリ上で新しい AutoGatherList を作り、Add で詰める
    ///  3. 旧リストを削除してから AddList を呼ぶ
    ///  4. 保存結果を確認（JSON ファイルに書けたか）
    /// </summary>
    /// <param name="listName">リスト名（例：GBRHelper_鉱_Lv1-10_未採取）</param>
    /// <param name="managementTag">Description に埋める管理タグ（所有権確認用）</param>
    /// <param name="entries">(アイテムID, 数量) のリスト。並びはそのまま入る。</param>
    /// <param name="enabled">リストを enabled にして入れるか。既定 false（指示書の方針）</param>
    public WriteResult WriteManagedList(string listName, string managementTag, IReadOnlyList<(uint ItemId, uint Quantity)> entries, bool enabled = false, bool usesRetainerInventory = true, string? previousListName = null)
    {
        try
        {
            if (this.Manager() is not { } mgr)
                return WriteResult.Fail(this.shared.LastError);

            // アイテム ID → IGatherable を全部先に解決する（1 件でも失敗したら作らない）。
            var gatherables = this.GatherablesDictionary();
            if (gatherables is null)
                return WriteResult.Fail("GBR の GameData.Gatherables に届きません");

            var resolved = new List<(object Gatherable, uint Quantity)>();
            var unresolved = new List<uint>();
            // 魚も入れられる（Crafting Lists の素材に魚が入るため。2026-10-05）。採集品を先に引く。
            var fishes = this.FishesDictionary();
            foreach (var (id, q) in entries)
            {
                var item = TryGetItemFromDictionary(gatherables, id) ?? (fishes is null ? null : TryGetItemFromDictionary(fishes, id));
                if (item is null)
                    unresolved.Add(id);
                else
                    resolved.Add((item, q));
            }

            if (resolved.Count == 0)
                return WriteResult.Fail($"解決できたアイテムが 0 件でした（{entries.Count} 件中）");
            if (unresolved.Count > 0)
                return WriteResult.Fail($"アイテムを解決できないため既存リストを変更しません: {string.Join(", ", unresolved)}");

            // 記録した前回名と現在名だけ。GBRの複製はDescriptionも同じだが所有対象に広げない。
            var ownedExisting = previousListName is not null
                ? (mgr.GetType().GetProperty("Lists", PubInst)?.GetValue(mgr) as IEnumerable)?.Cast<object>()
                    .Where(x => x.GetType().GetProperty("Description", PubInst)?.GetValue(x) as string == managementTag
                        && (x.GetType().GetProperty("Name", PubInst)?.GetValue(x) as string is { } n)
                        && (n == previousListName || n == listName)).ToArray() ?? []
                : this.FindOwnedManagedList(mgr, listName, managementTag) is { } one ? new[] { one } : [];
            var oldNames = ownedExisting.Select(x => x.GetType().GetProperty("Name", PubInst)?.GetValue(x) as string)
                .OfType<string>().Where(n => n != listName).Distinct().ToArray();

            // 新しい AutoGatherList を作る。ここではまだ Manager に入れない。
            if (this.FindGbrType(resolved[0].Gatherable, "GatherBuddy.AutoGather.Lists.AutoGatherList") is not { } listType)
                return WriteResult.Fail("GBR の AutoGatherList 型が見つかりません");

            var newList = Activator.CreateInstance(listType);
            if (newList is null)
                return WriteResult.Fail("GBR の AutoGatherList を作れません");

            listType.GetProperty("Name", PubInst)?.SetValue(newList, listName);
            listType.GetProperty("Description", PubInst)?.SetValue(newList, managementTag);
            listType.GetProperty("Enabled", PubInst)?.SetValue(newList, enabled);
            listType.GetProperty("RemoveCompletedItems", PubInst)?.SetValue(newList, false);

            listType.GetProperty("UsesRetainerInventory", AnyInst)?.SetValue(newList, usesRetainerInventory);

            // UsesRetainerInventory は internal setter（AutoGatherList.cs:37）。
            // 既定は true のまま。利用者の挙動に影響するので変えない。

            // Add(IGatherable, uint) を取る。IGatherable の型は解決済みアイテムの実型から推定する
            // （指示書：アセンブリまたぎで型が別物になる罠を避ける）。
            var addMethods = listType.GetMethods(PubInst).Where(m => m.Name == "Add" && m.GetParameters().Length == 2).ToList();
            if (addMethods.Count == 0)
                return WriteResult.Fail("GBR の AutoGatherList.Add(IGatherable, uint) が見つかりません");

            var addMethod = addMethods[0];

            var addedCount = 0;
            foreach (var (item, q) in resolved)
            {
                var result = addMethod.Invoke(newList, [item, q]);
                if (result is true)
                    addedCount++;
            }

            if (addedCount != entries.Count)
                return WriteResult.Fail($"Add の成功は {addedCount}/{entries.Count} 件でした。リストは登録していません");

            // 既存の Helper 管理リストがあれば削除。所有権を再確認してから。
            if (ownedExisting.Length > 0)
            {
                var deleteMethod = mgr.GetType().GetMethod("DeleteList", PubInst, new[] { listType });
                if (deleteMethod is null)
                    return WriteResult.Fail("GBR の AutoGatherListsManager.DeleteList が見つかりません");

                foreach (var old in ownedExisting) deleteMethod.Invoke(mgr, [old]);
            }

            // 新リストを Manager に追加（AddList は内部で Save & SetActiveItems を呼ぶ）。
            // folder 引数は省略可能だが、省略可能引数は別オーバーロードを意味しないので
            // Type.Missing を渡して明示する。
            var addListMethod = mgr.GetType().GetMethods(PubInst).FirstOrDefault(m => m.Name == "AddList" && m.GetParameters().Length >= 1);
            if (addListMethod is null)
                return WriteResult.Fail("GBR の AutoGatherListsManager.AddList が見つかりません");

            var parameters = addListMethod.GetParameters();
            var args = new object?[parameters.Length];
            args[0] = newList;
            for (var i = 1; i < parameters.Length; i++)
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;

            addListMethod.Invoke(mgr, args);

            foreach (var oldName in oldNames)
                if (this.SavedListMatches(oldName, managementTag, null, false) != true)
                    return WriteResult.Fail("改名前の管理リストの削除を保存できていません");

            // 保存結果の確認：auto_gather_lists.json に対象のリスト名があるか。
            switch (this.SavedListMatches(listName, managementTag, entries, enabled))
            {
                case null:
                    return WriteResult.Fail($"GBR には渡しましたが、保存を確かめられません: {this.LastError}");
                case false:
                    return WriteResult.Fail("GBR には渡しましたが、保存内容が指定した管理リスト・数量・有効状態と一致しません");
            }

            this.LastError = string.Empty;
            return WriteResult.Success(addedCount, unresolved);
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の自動採集リスト「{listName}」を書けません: {ex.GetType().Name}: {ex.Message}";
            return WriteResult.Fail(this.LastError);
        }
    }

    /// <summary>
    /// Helper が作った管理リストの中の 1 品を、有効または無効にする（2026-10-05 霊砂：目標に届いた霊砂の原料を無効にする）。
    /// GBR の AutoGatherListsManager.ChangeEnabled(list, item, enabled)（保存とリストの読み直しまで行う。
    /// AutoGatherListsManager.ManipPreset.cs 529-546）を呼び、保存ファイルでその品の有効・無効を確かめる。
    /// 管理タグの合わないリスト・リストに無い品には触らない。
    /// </summary>
    public bool SetManagedItemEnabled(string listName, string managementTag, uint itemId, bool enabled)
    {
        try
        {
            if (this.Manager() is not { } mgr)
                return false;

            if (this.FindOwnedManagedList(mgr, listName, managementTag) is not { } owned)
            {
                this.LastError = $"管理リスト「{listName}」が見つかりません";
                return false;
            }

            var items = owned.GetType().GetProperty("Items", PubInst)?.GetValue(owned) as IEnumerable;
            var item = items?.Cast<object>().FirstOrDefault(i => i.GetType().GetProperty("ItemId", PubInst)?.GetValue(i) is uint id && id == itemId);
            if (item is null)
            {
                this.LastError = $"管理リスト「{listName}」に品 {itemId} がありません";
                return false;
            }

            var change = mgr.GetType().GetMethods(PubInst).FirstOrDefault(m => m.Name == "ChangeEnabled" && m.GetParameters() is { Length: 3 } p
                && p[0].ParameterType.IsInstanceOfType(owned) && p[1].ParameterType.IsInstanceOfType(item) && p[2].ParameterType == typeof(bool));
            if (change is null)
            {
                this.LastError = "GBR の AutoGatherListsManager.ChangeEnabled が見つかりません";
                return false;
            }

            change.Invoke(mgr, [owned, item, enabled]);

            var path = this.AutoGatherListsSaveFile();
            if (path is null || !File.Exists(path))
            {
                this.LastError = "GBRの保存ファイルを確認できません";
                return false;
            }

            if (ManagedListPersistence.ItemEnabledMatches(File.ReadAllText(path), listName, managementTag, itemId, enabled) != true)
            {
                this.LastError = $"品 {itemId} の{(enabled ? "有効" : "無効")}を保存できていません";
                return false;
            }

            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = $"管理リスト「{listName}」の品を切り替えられません: {ex.GetBaseException().Message}";
            return false;
        }
    }

    /// <summary>
    /// Helper が作った管理リストを削除する。
    /// Description の管理タグが一致するもののみ削除する（ユーザー作成の同名リストは触らない）。
    /// </summary>
    public bool RemoveManagedList(string listName, string managementTag)
    {
        try
        {
            if (this.Manager() is not { } mgr)
                return false;

            var owned = this.FindOwnedManagedList(mgr, listName, managementTag);
            if (owned is null)
            {
                // 前回、メモリからは消えたが保存に失敗した場合にも再試行できる。
                mgr.GetType().GetMethod("Save", PubInst, null, Type.EmptyTypes, null)?.Invoke(mgr, null);
                var absent = this.SavedListMatches(listName, managementTag, null, false);
                if (absent == true) { this.LastError = string.Empty; return true; }
                this.LastError = "管理リストの削除を保存できていません";
                return false;
            }

            var listType = owned.GetType();
            var deleteMethod = mgr.GetType().GetMethod("DeleteList", PubInst, new[] { listType });
            if (deleteMethod is null)
            {
                this.LastError = "GBR の AutoGatherListsManager.DeleteList が見つかりません";
                return false;
            }

            deleteMethod.Invoke(mgr, [owned]);

            // 削除確認：保存ファイルから当該リスト名が消えたか。確かめられないときも成功にしない。
            switch (this.SavedListMatches(listName, managementTag, null, false))
            {
                case null:
                    this.LastError = $"GBR には渡しましたが、削除の保存を確かめられません: {this.LastError}";
                    return false;
                case false:
                    this.LastError = "GBR には渡しましたが、保存ファイルに管理リストが残っています";
                    return false;
            }

            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の自動採集リスト「{listName}」を消せません: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Helper が作った管理リストを、同じ場所（フォルダー）のリストの一番上へ動かす（2026-10-06 霊砂・クリスタル。
    /// 要望「ほかのリストを無効にさせず、自動的に Auto-Gather の一番上に入れる」）。すでに一番上なら何もしない（moved＝false）。
    ///
    /// 【一番上にする理由】GBR は「品目の並べ方：なし」のとき、出ている刻限の品を先に、そのあと常に採れる品を並べ、
    /// 同じ扱いの品どうしはリストの並びの順に採る（GBR 7.5.6.1 ActiveItemList.cs 389-391）。リストの並びは
    /// フォルダーの中のリストが先、同じ場所のリストは Order の小さい順（ManualOrderSortMode・ElliLib の GetAllDescendants）。
    /// AddList は新しいリストを一番下（Order＝最大＋1）に置く（AutoGatherListsManager.OnFileSystemChanged）。
    /// 【動かし方】GBR の MoveList(動かすリスト, 入れる位置のリスト, false)（ManipPreset.cs 623-640）で Order を書き換えて保存する。
    /// MoveList は採る順の一覧（ActiveItems）を作り直さない（GBR の画面で並べ替えたときも同じ）ので、続けて SetActiveItems(false) を呼ぶ。
    /// 保存ファイルで、同じ場所のほかのリストより Order が小さいことを確かめる（1 本だけの MoveManagedListsToTop）。
    /// </summary>
    public bool MoveManagedListToTop(string listName, string managementTag, out bool moved)
        => this.MoveManagedListsToTop([listName], managementTag, out moved);

    /// <summary>
    /// Helper が作った管理リスト（listNames の順）を、それぞれの場所（フォルダー）で一番上からこの順に並べる
    /// （2026-10-06 霊砂とクリスタルを同時に登録できるようにした。霊砂のリストを一番上、クリスタルのリストをその下）。
    /// すでにその並びなら何もしない（moved＝false）。1 本ずつ別々に一番上へ動かすと、2 本が互いに一番上を取り合って保存し続けるため、まとめて扱う。
    /// 【動かし方】後ろのリストから順に、その時点の一番上のリストの位置へ MoveList(動かすリスト, 一番上のリスト, false) で入れる
    /// （最後に動かした先頭のリストが一番上になる）。すでに一番上（ほかのどれよりも Order が小さい）のリストは動かさない
    /// （MoveList は、上にあるリストを動かすと「下へ動かした」とみなして、入れる位置の 1 つ下に入れるため）。
    /// 保存ファイルで並びを確かめる（ManagedListPersistence.AreFirstInFolder）。
    /// </summary>
    public bool MoveManagedListsToTop(IReadOnlyList<string> listNames, string managementTag, out bool moved)
    {
        moved = false;
        try
        {
            if (this.Manager() is not { } mgr)
                return false;

            var fileSystem = mgr.GetType().GetProperty("FileSystem", PubInst)?.GetValue(mgr);
            var tryGet = fileSystem?.GetType().GetMethods(PubInst).FirstOrDefault(m => m.Name == "TryGetValue" && m.GetParameters().Length == 2);
            if (fileSystem is null || tryGet is null)
            {
                this.LastError = "GBR のリストの並び（AutoGatherListsManager.FileSystem）に届きません（版の違いを確認してください）";
                return false;
            }

            var leaves = new List<object>();
            foreach (var listName in listNames)
            {
                if (this.FindOwnedManagedList(mgr, listName, managementTag) is not { } owned)
                {
                    this.LastError = $"管理リスト「{listName}」が見つかりません";
                    return false;
                }

                var args = new object?[] { owned, null };
                if (tryGet.Invoke(fileSystem, args) is not true || args[1] is not { } leaf)
                {
                    this.LastError = $"GBR のリストの並びに管理リスト「{listName}」が見つかりません";
                    return false;
                }

                leaves.Add(leaf);
            }

            var move = mgr.GetType().GetMethods(PubInst).FirstOrDefault(m => m.Name == "MoveList" && m.GetParameters() is { Length: 3 } p
                && leaves.Count > 0 && p[0].ParameterType.IsInstanceOfType(leaves[0]) && p[2].ParameterType == typeof(bool));
            var setActive = mgr.GetType().GetMethods(PubInst).FirstOrDefault(m => m.Name == "SetActiveItems" && m.GetParameters() is { Length: 1 } p
                && p[0].ParameterType == typeof(bool));

            // 場所（親のフォルダー）ごとに並べる。GBR の MoveList は同じ場所のリストどうしでしか動かせない。
            foreach (var group in leaves.GroupBy(ParentOf))
            {
                var mine = group.ToArray(); // listNames の順
                var siblings = (group.Key.GetType().GetMethod("GetLeaves", PubInst, Type.EmptyTypes)?.Invoke(group.Key, null) as IEnumerable)?.Cast<object>().ToArray();
                if (siblings is null)
                {
                    this.LastError = "GBR のリストの並び（同じ場所のリスト）を読めません";
                    return false;
                }

                if (InOrder(mine, siblings))
                    continue;

                if (move is null || setActive is null)
                {
                    this.LastError = "GBR の AutoGatherListsManager.MoveList / SetActiveItems が見つかりません（版の違いを確認してください）";
                    return false;
                }

                for (var i = mine.Length - 1; i >= 0; i--)
                {
                    var leaf = mine[i];
                    var others = siblings.Where(x => !ReferenceEquals(x, leaf)).ToArray();
                    if (others.Length == 0 || others.All(x => OrderOf(x) > OrderOf(leaf)))
                        continue; // もう一番上
                    move.Invoke(mgr, [leaf, others.MinBy(OrderOf)!, false]);
                    moved = true;
                }
            }

            if (!moved)
            {
                this.LastError = string.Empty;
                return true;
            }

            // MoveList は採る順の一覧（ActiveItems）を作り直さない。
            setActive!.Invoke(mgr, [false]);

            var path = this.AutoGatherListsSaveFile();
            if (path is null || !File.Exists(path))
            {
                this.LastError = "GBRの保存ファイルを確認できません";
                return false;
            }

            if (ManagedListPersistence.AreFirstInFolder(File.ReadAllText(path), listNames, managementTag) != true)
            {
                this.LastError = $"管理リスト「{string.Join("」「", listNames)}」を一番上へ動かしたのを保存できていません";
                return false;
            }

            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = $"管理リスト「{string.Join("」「", listNames)}」を一番上へ動かせません: {ex.GetBaseException().Message}";
            return false;
        }

        // GBR の AutoGatherList.Order（int）。リストの並びの番号で、小さいほど上。
        static int OrderOf(object leaf)
            => leaf.GetType().GetProperty("Value", PubInst)?.GetValue(leaf) is { } list
               && list.GetType().GetProperty("Order", PubInst)?.GetValue(list) is int order
                ? order
                : throw new InvalidOperationException("GBR のリストの並びの番号（Order）を読めません");

        static object ParentOf(object leaf)
            => leaf.GetType().GetProperty("Parent", PubInst)?.GetValue(leaf)
               ?? throw new InvalidOperationException("GBR のリストの場所（Parent）を読めません");

        // 管理リストが mine の順に Order が小さくなっていて、ほかのどのリストよりも Order が小さいか。
        static bool InOrder(object[] mine, object[] siblings)
        {
            for (var i = 0; i + 1 < mine.Length; i++)
                if (OrderOf(mine[i]) >= OrderOf(mine[i + 1]))
                    return false;
            var last = OrderOf(mine[^1]);
            return siblings.Where(x => !mine.Any(m => ReferenceEquals(m, x))).All(x => OrderOf(x) > last);
        }
    }

    // ------------------------------------------------------------------
    // GBR 自身の「Artisan から読み込む」で作られたリスト（要望：シャード・クリスタル・クラスターは抽出しない）

    /// <summary>GBR 自身の「Artisan から読み込む」が作るリストの説明（GatherBuddy/AutoGather/Helpers/Reflection.cs:88）。</summary>
    public const string GbrArtisanImportDescription = "Imported from Artisan";

    /// <summary>GBR の「Artisan から読み込む」で作られたリスト。Handle はリストの入れ物（同じリストかを参照で見分ける）。</summary>
    public sealed record ImportedList(object Handle, string Name, IReadOnlyList<uint> Items);

    /// <summary>
    /// 説明が GBR の取り込み（GbrArtisanImportDescription）のリストの一覧。manager は GBR の管理の部品（GBR を読み直したかを見分ける）。
    /// GBR が無い・読めないときは null。
    /// </summary>
    public IReadOnlyList<ImportedList>? ArtisanImportedLists(out object? manager)
    {
        manager = null;
        try
        {
            if (this.Manager() is not { } mgr)
                return null;
            if (mgr.GetType().GetProperty("Lists", PubInst)?.GetValue(mgr) is not IEnumerable lists)
            {
                this.LastError = "GBR の AutoGatherListsManager.Lists に届きません";
                return null;
            }

            var result = new List<ImportedList>();
            foreach (var list in lists.Cast<object>().ToList())
            {
                if (list is null)
                    continue;
                var t = list.GetType();
                if (t.GetProperty("Description", PubInst)?.GetValue(list) as string != GbrArtisanImportDescription)
                    continue;
                var name = t.GetProperty("Name", PubInst)?.GetValue(list) as string ?? "";
                var items = (t.GetProperty("Items", PubInst)?.GetValue(list) as IEnumerable)?.Cast<object>()
                    .Select(i => i?.GetType().GetProperty("ItemId", PubInst)?.GetValue(i)).OfType<uint>().ToList() ?? [];
                result.Add(new ImportedList(list, name, items));
            }

            manager = mgr;
            return result;
        }
        catch (Exception ex)
        {
            // GBR の取り込みは別のスレッドでリストを足すので、一覧をなめている途中で変わることがある。次の回に読み直す。
            this.LastError = $"GBR のリストを読めません: {ex.GetBaseException().Message}";
            return null;
        }
    }

    /// <summary>
    /// 保存ファイルにある、その名前で説明が GBR の取り込みのリストの数（GBR が取り込みを保存し終えたかの確かめ）。読めなければ null。
    /// </summary>
    public int? SavedArtisanImportCount(string name)
    {
        try
        {
            var path = this.AutoGatherListsSaveFile();
            if (path is null || !File.Exists(path))
            {
                this.LastError = "GBRの保存ファイルを確認できません";
                return null;
            }

            return CountSavedLists(File.ReadAllText(path), name, GbrArtisanImportDescription);
        }
        catch (Exception ex)
        {
            this.LastError = $"保存内容を確認できません: {ex.Message}";
            return null;
        }
    }

    /// <summary>保存ファイルの中身（リストの配列）で、名前と説明が同じリストの数。形が違えば null。</summary>
    public static int? CountSavedLists(string json, string name, string description)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            return doc.RootElement.EnumerateArray().Count(e => e.ValueKind == System.Text.Json.JsonValueKind.Object
                && e.TryGetProperty("Name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String && n.GetString() == name
                && e.TryGetProperty("Description", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String && d.GetString() == description);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// そのリストから remove に合う品を、GBR の RemoveItem（GBR の画面で品を消すのと同じ：消す→保存→そのリストが有効なら採る順を作り直す。
    /// AutoGatherListsManager.ManipPreset.cs:495-504）で、後ろから消す。消した品番を返す。
    /// 消したあとに合う品が残っていれば失敗（null。理由は LastError）。
    /// </summary>
    public List<uint>? RemoveItems(object list, Func<uint, bool> remove)
    {
        try
        {
            if (this.Manager() is not { } mgr)
                return null;
            var t = list.GetType();
            var method = mgr.GetType().GetMethod("RemoveItem", PubInst, [t, typeof(int)]);
            if (method is null)
            {
                this.LastError = "GBR の AutoGatherListsManager.RemoveItem が見つかりません（版の違いを確認してください）";
                return null;
            }

            List<uint> Ids() => (t.GetProperty("Items", PubInst)?.GetValue(list) as IEnumerable)?.Cast<object>()
                .Select(i => i?.GetType().GetProperty("ItemId", PubInst)?.GetValue(i) as uint? ?? 0u).ToList() ?? [];
            var ids = Ids();
            var removed = new List<uint>();
            for (var i = ids.Count - 1; i >= 0; i--)
            {
                if (!remove(ids[i]))
                    continue;
                method.Invoke(mgr, [list, i]);
                removed.Add(ids[i]);
            }

            removed.Reverse();
            if (Ids().Any(remove))
            {
                this.LastError = "消したはずの品がリストに残っています";
                return null;
            }

            this.LastError = string.Empty;
            return removed;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR のリストから品を消せません: {ex.GetBaseException().Message}";
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 内部

    /// <summary>AutoGatherListsManager インスタンス。</summary>
    private object? Manager()
    {
        if (this.shared.Plugin() is not { } p)
        {
            this.LastError = this.shared.LastError;
            return null;
        }

        FieldInfo? field = null;
        for (var t = p.GetType(); t != null && field is null; t = t.BaseType)
            field = t.GetField("AutoGatherListsManager", AnyInst);

        if (field is null)
        {
            this.LastError = "GBR の AutoGatherListsManager フィールドが見つかりません（版が変わった可能性）";
            return null;
        }

        var mgr = field.GetValue(p);
        if (mgr is null)
        {
            this.LastError = "GBR の AutoGatherListsManager が未初期化です";
            return null;
        }

        return mgr;
    }

    /// <summary>GatherBuddy.GameData.Gatherables を取得。IDictionary にキャスト。</summary>
    private IDictionary? GatherablesDictionary()
    {
        if (this.shared.Plugin() is not { } p)
            return null;

        var gameData = p.GetType().GetProperty("GameData", PubStatic)?.GetValue(null);
        if (gameData is null)
        {
            this.LastError = "GBR の GameData（public static）に届きません";
            return null;
        }

        var g = gameData.GetType().GetProperty("Gatherables", PubInst)?.GetValue(gameData);
        if (g is IDictionary d)
            return d;

        this.LastError = "GBR の Gatherables を IDictionary にキャストできません";
        return null;
    }

    /// <summary>GatherBuddy.GameData.Fishes（魚。Crafting Lists の素材に魚が入るため。2026-10-05）。届かなければ null。</summary>
    private IDictionary? FishesDictionary()
    {
        if (this.shared.Plugin() is not { } p)
            return null;
        var gameData = p.GetType().GetProperty("GameData", PubStatic)?.GetValue(null);
        return gameData?.GetType().GetProperty("Fishes", PubInst)?.GetValue(gameData) as IDictionary;
    }

    /// <summary>
    /// Artisan の Crafting Lists の素材（品番 → 数）を、GBR の Auto-Gather に入れられる品に絞る。
    /// GBR 自身の「Artisan から読み込む」（GatherBuddy/AutoGather/Helpers/Reflection.cs 66-117 の ImportArtisanList）と同じ決まり：
    ///   ディアデムの品は原料の品番に置き換え（Diadem.ApprovedToRawItemIds）、採集品（Gatherables）か魚（Fishes）で、採れる場所（Locations）がある品だけ。
    ///   それ以外（購入品・ドロップ品・製作品など）は Skipped（元の品番）。同じ品番になった品は数を足す。
    /// 読めなければ null（理由は LastError）。
    /// </summary>
    public (List<(uint ItemId, uint Quantity)> Entries, List<uint> Skipped)? ResolveForAutoGather(IReadOnlyDictionary<uint, int> materials)
    {
        try
        {
            var gatherables = this.GatherablesDictionary();
            if (gatherables is null)
                return null;
            var fishes = this.FishesDictionary();

            // Diadem は GBR の internal クラス。読めなければ置き換えなしで続ける（ディアデムの品が採れない品として外れるだけ）。
            IDictionary? diadem = null;
            if (this.shared.Plugin() is { } p)
                diadem = p.GetType().Assembly.GetType("GatherBuddy.AutoGather.Helpers.Diadem")
                    ?.GetProperty("ApprovedToRawItemIds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;

            var sums = new Dictionary<uint, long>();
            var skipped = new List<uint>();
            foreach (var (id, quantity) in materials)
            {
                if (quantity <= 0)
                    continue;
                var mapped = diadem is not null && TryGetItemFromDictionary(diadem, id) is uint raw ? raw : id;
                var item = TryGetItemFromDictionary(gatherables, mapped) ?? (fishes is null ? null : TryGetItemFromDictionary(fishes, mapped));
                if (item is null || !HasLocations(item))
                {
                    skipped.Add(id);
                    continue;
                }

                sums[mapped] = sums.GetValueOrDefault(mapped) + quantity;
            }

            this.LastError = string.Empty;
            return (sums.Select(p => (p.Key, (uint)Math.Min(p.Value, uint.MaxValue))).ToList(), skipped);
        }
        catch (Exception ex)
        {
            this.LastError = $"素材を GBR の品に照らせません: {ex.GetBaseException().Message}";
            return null;
        }
    }

    /// <summary>GBR の品に、採れる場所（Locations）が 1 つ以上あるか（GBR の取り込みと同じ条件）。</summary>
    private static bool HasLocations(object item)
        => item.GetType().GetProperty("Locations", PubInst)?.GetValue(item) is IEnumerable locations && locations.Cast<object>().Any();

    /// <summary>
    /// FrozenDictionary を IDictionary 経由で引く。non-generic IDictionary は FrozenDictionary でも
    /// 動くが、動かない利用環境では null を返す（指示書：失敗を「対象0件」として扱わない）。
    /// </summary>
    private static object? TryGetItemFromDictionary(IDictionary dict, uint itemId)
    {
        try
        {
            if (dict.Contains(itemId))
                return dict[itemId];
        }
        catch
        {
            // FrozenDictionary の非ジェネリック経路が落ちることがある。
            // そのときは Enumerator で引く（遅いがここは起動時 1 回だけなので許容）。
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is uint key && key == itemId)
                    return entry.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// 名前が一致 かつ Description に Helper の管理タグを含むリストを返す。
    /// 無ければ null。
    /// </summary>
    private object? FindOwnedManagedList(object mgr, string listName, string managementTag)
    {
        var lists = mgr.GetType().GetProperty("Lists", PubInst)?.GetValue(mgr) as IEnumerable;
        if (lists is null)
            return null;

        foreach (var list in lists)
        {
            if (list is null)
                continue;
            var t = list.GetType();
            var name = t.GetProperty("Name", PubInst)?.GetValue(list) as string;
            if (name != listName)
                continue;
            var desc = t.GetProperty("Description", PubInst)?.GetValue(list) as string ?? "";
            if (desc.Contains(managementTag, StringComparison.Ordinal))
                return list;
        }

        return null;
    }

    /// <summary>
    /// 保存ファイル auto_gather_lists.json に対象のリスト名が入っているかを確認する。
    /// 直接書き換えはしない（読み取りのみ）。確かめられなければ null（理由は LastError）。
    /// GBR の Save は File.WriteAllText で同じ呼び出しの中で書き終える（AutoGatherListsManager.cs:157-176）ので、
    /// AddList / DeleteList が戻った直後に読めば反映済みのはず。
    /// </summary>
    private bool? SavedListMatches(string name, string tag, IReadOnlyList<(uint ItemId, uint Quantity)>? entries, bool enabled)
    {
        try
        {
            var path = this.AutoGatherListsSaveFile();
            if (path is null || !File.Exists(path))
            {
                this.LastError = "GBRの保存ファイルを確認できません";
                return null;
            }
            return ManagedListPersistence.Matches(File.ReadAllText(path), name, tag, entries, enabled);
        }
        catch (Exception ex)
        {
            this.LastError = $"保存内容を確認できません: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// auto_gather_lists.json の絶対パスを、GBR 自身が保存に使う関数で求める。
    ///
    /// 【2026-10-05 直した】以前は「pluginConfigs\GatherBuddy\」と決め打ちしていたが、実機の GBR は
    /// 「pluginConfigs\GatherBuddyReborn\auto_gather_lists.json」に保存している（GBR の設定フォルダは
    /// InternalName から決まる。旧 GatherBuddy の「GatherBuddy」フォルダも残っていて紛らわしい）。
    /// 決め打ちのままだと、リストを作れても毎回「保存失敗」と表示していた。
    /// GBR の保存は AutoGatherListsManager.Save → Functions.ObtainSaveFile("auto_gather_lists.json")
    /// （GatherBuddy/Plugin/Functions.cs:28）なので、同じ関数を呼べば同じ場所になる。
    /// </summary>
    private string? AutoGatherListsSaveFile()
    {
        try
        {
            if (this.shared.Plugin() is not { } p)
                return null;

            var functions = p.GetType().Assembly.GetType("GatherBuddy.Plugin.Functions");
            var obtain = functions?.GetMethod("ObtainSaveFile", PubStatic, null, [typeof(string)], null);
            if (obtain is null)
            {
                this.LastError = "GBR の保存先を求める関数（Functions.ObtainSaveFile）が見つかりません（版の違いを確認してください）";
                return null;
            }

            return (obtain.Invoke(null, ["auto_gather_lists.json"]) as FileInfo)?.FullName;
        }
        catch (Exception ex)
        {
            this.LastError = $"GBR の保存先を求められません: {ex.GetBaseException().Message}";
            return null;
        }
    }

    /// <summary>解決済みのアイテムインスタンスから、同じアセンブリ内の型を取る。</summary>
    private Type? FindGbrType(object anyKnownItem, string fullName)
    {
        try
        {
            // 1) GBR 本体アセンブリから
            if (this.shared.Plugin() is { } p)
            {
                var t = p.GetType().Assembly.GetType(fullName);
                if (t is not null)
                    return t;
            }

            // 2) 解決済みアイテム（Gatherable）のアセンブリから
            var itemAssembly = anyKnownItem.GetType().Assembly;
            var t2 = itemAssembly.GetType(fullName);
            if (t2 is not null)
                return t2;

            // 3) ロード済みの全アセンブリから
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t3 = asm.GetType(fullName);
                if (t3 is not null)
                    return t3;
            }
        }
        catch
        {
            // 失敗したら null を返して呼び出し側に扱わせる
        }

        return null;
    }

    // ------------------------------------------------------------------

    public sealed record ListSummary(string Name, string Description, bool Enabled, IReadOnlyList<(uint ItemId, uint Quantity)> Entries,
        bool AllItemsEnabled = true, bool Fallback = false, bool UsesRetainerInventory = true)
    {
        /// <summary>リストの中で無効になっている品（GBR の EnabledItems が true でない品）。</summary>
        public IReadOnlySet<uint> DisabledItems { get; init; } = new HashSet<uint>();
    }

    public sealed record WriteResult(bool Ok, int Added, IReadOnlyList<uint> Unresolved, string Error)
    {
        public static WriteResult Success(int added, IReadOnlyList<uint> unresolved) => new(true, added, unresolved, string.Empty);
        public static WriteResult Fail(string error) => new(false, 0, [], error);
    }
}
