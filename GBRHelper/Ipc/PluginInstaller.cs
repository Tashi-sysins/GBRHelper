using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Plugin;
using GBRHelper.Ui;

namespace GBRHelper.Ipc;

/// <summary>
/// 足りないプラグインを、ボタン 1 つで配布元の登録からインストールまで行う（「必要なプラグイン」。要望「ワンクリック方式で」）。
/// Dalamud の公式の配布の品（Allagan Tools）は、配布元を足さずに公式の配布の版を入れる（配布一覧の SourceRepo.IsThirdParty が false のもの）。
/// 無効の配布元は有効に戻す・秒数で待たず Dalamud の配布元の読み直しの完了を待つ・
/// 決めた配布元の今の Dalamud で読み込める版だけを入れる（RequiredPlugins.Pick）・停止中は Dalamud 標準の /xlenableplugin で有効にする。
///
/// Dalamud は「導入済みか調べる」「インストール画面を開く」は公開 API を持つが、配布元の追加とインストールは公開していない。
/// そこだけは内部（Service&lt;DalamudConfiguration&gt;・Service&lt;PluginManager&gt;）へリフレクションで触る（Questionable などと同じ）。
/// 使う名前（Dalamud のソースで確かめた）：DalamudConfiguration.ThirdRepoList（ThirdPartyRepoSettings：Url・IsEnabled）・QueueSave()、
///   PluginManager.SetPluginReposFromConfigAsync(bool)・ReloadAllReposAsync()・Repos（PluginRepository：PluginMasterUrl・IsThirdParty・State）・
///   AvailablePlugins（RemotePluginManifest：InternalName・DalamudApiLevel・SourceRepo）・InstallPluginAsync(RemotePluginManifest, bool useTesting, PluginLoadReason)。
/// Dalamud の更新で名前が変わると動かなくなるので、例外は外へ出さず失敗の文にして、インストール画面への入口を出す（本体の動作には影響させない）。
/// </summary>
public sealed class PluginInstaller
{
    /// <summary>1 つのプラグインの操作の進み（描画のスレッドと裏のスレッドから触るので、変えるときは丸ごと差し替える）。</summary>
    /// <param name="Busy">操作中か（ボタンを押せなくする）。</param>
    /// <param name="Ok">結果。null は操作中、false は手で入れる入口を出す。</param>
    /// <param name="Message">画面に出す文。</param>
    /// <param name="EnablingSince">有効にする命令を送った時刻（読み込まれたかを見張る）。</param>
    public sealed record Job(bool Busy, bool? Ok, string Message, DateTime? EnablingSince = null);

    /// <summary>
    /// 有効にする命令を送ってから、読み込まれないまま諦めるまでの上限（次へ進む待ちではない。読み込まれたらすぐ「有効にしました」にする）。
    /// Dalamud は前に読み込みに失敗したプラグインや、複数のコレクションに入っているプラグインは有効にせず、チャットに理由を出すだけなので、
    /// いつまでも「有効にしています…」のままにしない。
    /// </summary>
    public static readonly TimeSpan EnableGiveUp = TimeSpan.FromSeconds(20);

    /// <summary>配布元が「読み込み中」のときに読み直しを待つ回数の上限（読み直しは全部の配布元を読み終えると終わる Task なので、通常は 1 回で済む）。</summary>
    private const int RepoRounds = 3;

    private readonly ConcurrentDictionary<string, Job> jobs = new(StringComparer.Ordinal);

    /// <summary>そのプラグインの操作の進み（無ければ null）。</summary>
    public Job? JobOf(string internalName) => this.jobs.TryGetValue(internalName, out var job) ? job : null;

    /// <summary>今の Dalamud の API（PluginManager.DalamudApiLevel と同じく Dalamud.dll の版の頭の数）。</summary>
    private static int ApiLevel => DalamudAssembly.GetName().Version?.Major ?? 0;

    private static Assembly DalamudAssembly => typeof(IDalamudPluginInterface).Assembly;

    // ------------------------------------------------------------------
    // インストール

    /// <summary>
    /// 配布元の登録からインストールまで行う（ボタンから。描画のスレッド）。
    /// 配布元の一覧の書き換えはここ（押した場）で行う：Dalamud の設定画面も描画のスレッドで書き換えるので、裏のスレッドから一覧に触らない。
    /// 配布元の読み直しの完了待ちとインストールは時間が掛かるので、裏のスレッドで行う（描画を止めない）。
    /// </summary>
    public void Install(RequiredPlugins.Entry entry)
    {
        if (this.JobOf(entry.InternalName) is { Busy: true })
            return;

        var error = EnsureRepo(entry, out var changed);
        if (error != null)
        {
            this.Done(entry, false, $"{error}。インストール画面から入れてください");
            return;
        }

        this.jobs[entry.InternalName] = new Job(true, null, changed ? "配布元を登録しました。配布元を読み込んでいます…" : "インストールしています…");
        Svc.Log.Information($"[GBRHelper] 必要なプラグイン：{entry.DisplayName} のインストールを始めます（配布元の登録 {(changed ? "変えた" : "そのまま")}）");
        _ = Task.Run(() => this.InstallCoreAsync(entry, changed));
    }

    private async Task InstallCoreAsync(RequiredPlugins.Entry entry, bool repoChanged)
    {
        try
        {
            var pm = Service("Dalamud.Plugin.Internal.PluginManager");
            if (pm == null)
            {
                this.Done(entry, false, "Dalamud の内部（PluginManager）が見つからないので、自動ではインストールできません。インストール画面から入れてください");
                return;
            }

            // 登録を変えたら、Dalamud に設定から配布元の一覧を作り直させ、全部の配布元を読み終えるまで待つ（秒数で待たない）
            if (repoChanged)
                await InvokeTask(pm, "SetPluginReposFromConfigAsync", true).ConfigureAwait(false);

            object? manifest = null;
            string? why = null;
            for (var round = 0; round < RepoRounds && manifest == null; round++)
            {
                var (candidates, manifests) = ReadCandidates(pm);
                var (index, pickWhy) = RequiredPlugins.Pick(entry, candidates, ApiLevel);
                if (index >= 0)
                {
                    manifest = manifests[index];
                    break;
                }

                why = pickWhy;
                var load = RequiredPlugins.RepoLoadState(entry, ReadRepoStates(pm));
                if (load == RequiredPlugins.RepoLoad.Loaded)
                    break; // 配布元は読めたが、入れられる版が無い（理由は Pick の文）
                if (load == RequiredPlugins.RepoLoad.Failed)
                {
                    why = $"{RequiredPlugins.RepoName(entry)}を読み込めませんでした（回線・配布元の停止など）。少し置いてもう一度押すか、インストール画面から入れてください";
                    break;
                }

                this.jobs[entry.InternalName] = new Job(true, null, "配布元を読み込んでいます…");
                if (load == RequiredPlugins.RepoLoad.NotListed)
                    await InvokeTask(pm, "SetPluginReposFromConfigAsync", true).ConfigureAwait(false); // 設定は有効なのに、Dalamud の一覧にまだ入っていない
                else
                    await InvokeTask(pm, "ReloadAllReposAsync").ConfigureAwait(false); // 読み込み中なら終わるまで待つ。まだなら読ませる
            }

            if (manifest == null)
            {
                this.Done(entry, false, (why ?? $"{entry.DisplayName} を配布一覧に見つけられませんでした") + "。インストール画面から入れてください");
                return;
            }

            this.jobs[entry.InternalName] = new Job(true, null, "インストールしています…");
            var install = pm.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "InstallPluginAsync")
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
            if (install == null)
            {
                this.Done(entry, false, "Dalamud のインストールの処理（InstallPluginAsync）が見つかりません。インストール画面から入れてください");
                return;
            }

            // 引数は型を見て埋める（Dalamud の版で並びが変わり得る）。
            // bool は必ず false：useTesting で、true にすると配布情報の「テスト版のダウンロード先」を使い、多くの配布元はそこが空なので
            // 「An invalid request URI was provided」で失敗する（実機で踏んだ罠）。
            var ps = install.GetParameters();
            var args = new object?[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                args[i] = i == 0 ? manifest
                    : t == typeof(bool) ? false
                    : t.IsEnum ? LoadReason(t)
                    : ps[i].HasDefaultValue ? ps[i].DefaultValue
                    : t.IsValueType ? Activator.CreateInstance(t)
                    : null;
            }

            if (install.Invoke(pm, args) is not Task task)
            {
                this.Done(entry, false, "インストールを始められませんでした。インストール画面から入れてください");
                return;
            }

            await task.ConfigureAwait(false);

            // 結果は「実行した」ではなく、実際に入って読み込まれたかで決める
            var state = RequiredPlugins.Check(RequiredPlugins.Read(Svc.PluginInterface.InstalledPlugins))
                .First(s => s.Entry.InternalName == entry.InternalName);
            switch (state.Status)
            {
                case RequiredPlugins.Status.Loaded:
                    this.Done(entry, true, $"インストールしました（{state.Version ?? "版不明"}）");
                    break;
                case RequiredPlugins.Status.NotLoaded:
                    this.Done(entry, false, "インストールしましたが、読み込まれていません。「有効にする」を押すか、Dalamud のプラグイン一覧を確かめてください");
                    break;
                default:
                    this.Done(entry, false, "インストールを実行しましたが、導入済みの一覧に出てきません。インストール画面を確かめてください");
                    break;
            }
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            this.Done(entry, false, $"インストールに失敗しました（{inner.GetBaseException().Message}）。インストール画面から入れてください");
        }
    }

    /// <summary>操作を終える（dalamud.log にも残す）。</summary>
    private void Done(RequiredPlugins.Entry entry, bool ok, string message)
    {
        this.jobs[entry.InternalName] = new Job(false, ok, message);
        if (Svc.Log is not { } log)
            return; // ゲーム無しの試験（Dalamud のログが無い）
        if (ok)
            log.Information($"[GBRHelper] 必要なプラグイン：{entry.DisplayName}：{message}");
        else
            log.Warning($"[GBRHelper] 必要なプラグイン：{entry.DisplayName}：{message}");
    }

    // ------------------------------------------------------------------
    // 有効にする（導入済み・停止中）

    /// <summary>
    /// Dalamud 標準のコマンド /xlenableplugin で有効にする（公開 API の ProcessCommand から送る）。
    /// Dalamud の中では、プラグイン一覧の切り替えと同じく、そのプラグインの入っているコレクションへ有効と書いて読み込む
    /// （Dalamud Plugin\Internal\Profiles\PluginManagementCommandHandler.cs。InternalName で探す）。
    /// 内部をまねて有効にすると、コレクションの扱い（複数に入っている・読み込みに失敗済み）を取り違えるので、Dalamud 自身に任せる。
    /// 読み込まれたかは Observe で見張る。
    /// </summary>
    public void Enable(RequiredPlugins.Entry entry)
    {
        if (this.JobOf(entry.InternalName) is { Busy: true })
            return;

        this.jobs[entry.InternalName] = new Job(true, null, "有効にしています…", DateTime.UtcNow);
        Svc.Log.Information($"[GBRHelper] 必要なプラグイン：{entry.DisplayName} を有効にします（/xlenableplugin）");
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (!Svc.Commands.ProcessCommand($"/xlenableplugin \"{entry.InternalName}\""))
                    this.Done(entry, false, "Dalamud の /xlenableplugin が見つかりません。Dalamud のプラグイン一覧から有効にしてください");
            }
            catch (Exception ex)
            {
                this.Done(entry, false, $"有効にする命令を送れませんでした（{ex.Message}）。Dalamud のプラグイン一覧から有効にしてください");
            }
        });
    }

    /// <summary>有効にしている途中のプラグインが読み込まれたかを見る（画面が状態を読み直すたびに呼ぶ）。</summary>
    public void Observe(IReadOnlyList<RequiredPlugins.State> states)
    {
        foreach (var state in states)
        {
            if (this.JobOf(state.Entry.InternalName) is not { Busy: true, EnablingSince: { } since })
                continue;
            if (state.Status == RequiredPlugins.Status.Loaded)
                this.Done(state.Entry, true, "有効にしました");
            else if (DateTime.UtcNow - since > EnableGiveUp)
                this.Done(state.Entry, false, "有効になりませんでした。Dalamud のチャット欄に理由が出ています（前に読み込みに失敗した・複数のコレクションに入っている など）。Dalamud のプラグイン一覧から有効にしてください");
        }
    }

    // ------------------------------------------------------------------
    // 手で入れる入口

    /// <summary>Dalamud のインストール画面を、そのプラグインを検索した状態で開く（導入済みなら導入済みの一覧）。</summary>
    public static void OpenInstaller(RequiredPlugins.Entry entry, bool installed)
    {
        try
        {
            Svc.PluginInterface.OpenPluginInstallerTo(installed ? PluginInstallerOpenKind.InstalledPlugins : PluginInstallerOpenKind.AllPlugins, entry.DisplayName);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GBRHelper] 必要なプラグイン：インストール画面を開けません: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 配布元（Dalamud の設定）

    /// <summary>Dalamud の設定に登録された配布元（URL・有効か）。読めなければ null。</summary>
    public static IReadOnlyList<(string Url, bool Enabled)>? ReadRepos()
    {
        try
        {
            if (RepoList(out _) is not { } list)
                return null;
            var repos = new List<(string Url, bool Enabled)>();
            foreach (var item in list)
            {
                if (item == null)
                    continue;
                var url = item.GetType().GetProperty("Url")?.GetValue(item) as string;
                if (string.IsNullOrEmpty(url))
                    continue;
                repos.Add((url, item.GetType().GetProperty("IsEnabled")?.GetValue(item) is true));
            }

            return repos;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 配布元を使える状態にする：公式の配布なら何もしない。無効なら有効に戻し、無ければ先頭の URL を足して、Dalamud の設定を保存する。
    /// changed は登録を変えたか（変えたら Dalamud に配布元を読み直させる）。失敗したら理由を返す。
    /// </summary>
    private static string? EnsureRepo(RequiredPlugins.Entry entry, out bool changed)
    {
        changed = false;
        if (entry.RepoUrls is not { Count: > 0 } urls)
            return null; // 公式の配布（Dalamud が初めから読む）
        try
        {
            var list = RepoList(out var config);
            if (list == null || config == null)
                return "Dalamud の設定（配布元の一覧）を読めません";

            switch (RequiredPlugins.RepoState(entry, ReadRepos()))
            {
                case RequiredPlugins.RepoStatus.Added:
                    return null;
                case RequiredPlugins.RepoStatus.Disabled:
                    foreach (var item in list)
                    {
                        if (item != null && urls.Any(u => RequiredPlugins.SameUrl(u, item.GetType().GetProperty("Url")?.GetValue(item) as string)))
                        {
                            item.GetType().GetProperty("IsEnabled")?.SetValue(item, true);
                            changed = true;
                            break;
                        }
                    }

                    break;
                case RequiredPlugins.RepoStatus.Missing:
                    var settingsType = DalamudAssembly.GetType("Dalamud.Configuration.ThirdPartyRepoSettings");
                    if (settingsType == null)
                        return "Dalamud の配布元の設定の型（ThirdPartyRepoSettings）が見つかりません";
                    var repo = Activator.CreateInstance(settingsType);
                    if (repo == null)
                        return "Dalamud の配布元の設定を作れません";
                    settingsType.GetProperty("Url")?.SetValue(repo, urls[0]);
                    settingsType.GetProperty("IsEnabled")?.SetValue(repo, true);
                    list.Add(repo);
                    changed = true;
                    break;
                default:
                    return "Dalamud の設定（配布元の一覧）を読めません";
            }

            if (!changed)
                return "配布元を有効に戻せませんでした";
            config.GetType().GetMethod("QueueSave")?.Invoke(config, null);
            Svc.Log.Information($"[GBRHelper] 必要なプラグイン：配布元を登録しました: {urls[0]}（{entry.DisplayName}）");
            return null;
        }
        catch (Exception ex)
        {
            return $"配布元を登録できませんでした（{ex.GetBaseException().Message}）";
        }
    }

    private static IList? RepoList(out object? config)
    {
        config = Service("Dalamud.Configuration.Internal.DalamudConfiguration");
        return config?.GetType().GetProperty("ThirdRepoList")?.GetValue(config) as IList;
    }

    // ------------------------------------------------------------------
    // Dalamud の内部（PluginManager）

    /// <summary>Dalamud の内部のサービス（Service&lt;T&gt;.Get()）。見つからなければ null。</summary>
    private static object? Service(string typeName)
    {
        try
        {
            var type = DalamudAssembly.GetType(typeName);
            var service = DalamudAssembly.GetType("Dalamud.Service`1");
            if (type == null || service == null)
                return null;
            return service.MakeGenericType(type)
                .GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
                .Invoke(null, null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Task を返す内部のメソッドを呼んで、終わるまで待つ。見つからなければ例外（呼んだ側で失敗の文にする）。</summary>
    private static async Task InvokeTask(object target, string method, params object[] args)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public, args.Select(a => a.GetType()).ToArray())
                ?? throw new MissingMethodException(target.GetType().FullName, method);
        if (m.Invoke(target, args) is Task task)
            await task.ConfigureAwait(false);
    }

    /// <summary>配布一覧（AvailablePlugins）を、判断に使う形（名前・配布元・API・公式か）と manifest そのものの組で読む。</summary>
    private static (List<RequiredPlugins.Candidate> Candidates, List<object> Manifests) ReadCandidates(object pm)
    {
        var candidates = new List<RequiredPlugins.Candidate>();
        var manifests = new List<object>();
        if (pm.GetType().GetProperty("AvailablePlugins")?.GetValue(pm) is not IEnumerable list)
            return (candidates, manifests);
        foreach (var m in list)
        {
            if (m == null)
                continue;
            var type = m.GetType();
            var repo = type.GetProperty("SourceRepo")?.GetValue(m);
            candidates.Add(new RequiredPlugins.Candidate(
                type.GetProperty("InternalName")?.GetValue(m) as string,
                repo?.GetType().GetProperty("PluginMasterUrl")?.GetValue(repo) as string,
                type.GetProperty("DalamudApiLevel")?.GetValue(m) is int api ? api : 0,
                repo?.GetType().GetProperty("IsThirdParty")?.GetValue(repo) is false));
            manifests.Add(m);
        }

        return (candidates, manifests);
    }

    /// <summary>Dalamud の配布元の一覧（Repos）を、URL・読み込み状態の名前・公式かで読む。</summary>
    private static List<(string? Url, string? State, bool Official)> ReadRepoStates(object pm)
    {
        var states = new List<(string? Url, string? State, bool Official)>();
        if (pm.GetType().GetProperty("Repos")?.GetValue(pm) is not IEnumerable repos)
            return states;
        foreach (var r in repos)
        {
            if (r == null)
                continue;
            var t = r.GetType();
            states.Add((t.GetProperty("PluginMasterUrl")?.GetValue(r) as string, t.GetProperty("State")?.GetValue(r)?.ToString(),
                t.GetProperty("IsThirdParty")?.GetValue(r) is false));
        }

        return states;
    }

    /// <summary>読み込みの理由の列挙から「インストール画面から」を選ぶ。無ければ既定値。</summary>
    private static object? LoadReason(Type enumType)
    {
        try
        {
            foreach (var name in Enum.GetNames(enumType))
            {
                if (name.Equals("Installer", StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse(enumType, name);
            }
        }
        catch (Exception)
        {
            // 既定値へ
        }

        return Activator.CreateInstance(enumType);
    }
}
