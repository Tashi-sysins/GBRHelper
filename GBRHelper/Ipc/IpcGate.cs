using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace GBRHelper.Ipc;

/// <summary>
/// 他プラグインへ IPC を投げるときの共通土台。
///
/// 方針（他プラグイン連携で痛い目を見た箇所なので必ず守る）:
///  ・取得に失敗したら「進めてよい」ではなく「進めない」側へ倒す（fail-closed）。
///  ・例外はまとめて握り潰さず、種類ごとに違う文言でログへ残す。
///    そうしないと「相手がバージョンアップで壊れた」のか「こちらの呼び方が悪い」のかが
///    後から区別できなくなる。
///  ・導入済み判定はキャッシュする。IsLoaded は IPC 呼び出しごとに走るため。
///  ・読めない状態が続くとログが埋まるので間引く。
/// </summary>
public abstract class IpcGate
{
    /// <summary>相手プラグインの InternalName。IPC 名の前置詞にもなる。</summary>
    public abstract string InternalName { get; }

    /// <summary>画面に出すときの表示名。</summary>
    public virtual string DisplayName => this.InternalName;

    private bool loadedCache;
    private DateTime loadedCacheExpiry = DateTime.MinValue;

    // ログ間引き用。同じ失敗を毎フレーム出さない。
    //
    // 1つの時刻を全メッセージで共有すると、先に起きた軽い失敗（起動待ちなど）が
    // 10秒間スロットを占有し、その間に起きた本当に知りたい失敗（型が変わった等）が
    // 消えてしまう。種類ごとに時刻を持つ。
    private readonly Dictionary<string, DateTime> lastWarnAt = new(StringComparer.Ordinal);
    private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(10);

    // 直近の失敗の中身。画面に出して原因を切り分けるために持つ。
    // dalamud.log は 100MB で書き込みが止まり、失敗の記録が残らないことがある。
    // ログに頼らず画面で見えるようにしておく。
    private readonly Dictionary<string, string> lastErrors = new(StringComparer.Ordinal);

    /// <summary>直近に失敗した IPC の内容。ラベルごとに1件。</summary>
    public IReadOnlyDictionary<string, string> LastErrors => this.lastErrors;

    /// <summary>
    /// 相手が導入済みかつロード済みか。5秒キャッシュ。
    /// これを見ずに IPC を呼ぶと、未導入環境で毎フレーム IpcNotReadyError が飛ぶ。
    /// </summary>
    public bool IsLoaded
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now < this.loadedCacheExpiry)
                return this.loadedCache;

            try
            {
                this.loadedCache = Svc.PluginInterface.InstalledPlugins
                    .Any(x => x.InternalName == this.InternalName && x.IsLoaded);
            }
            catch (Exception ex)
            {
                this.WarnThrottled($"導入状態を取得できませんでした: {ex.Message}");
                this.loadedCache = false;
            }

            this.loadedCacheExpiry = now.AddSeconds(5);
            return this.loadedCache;
        }
    }

    protected ICallGateSubscriber<TRet> Func<TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<TRet>(name);

    protected ICallGateSubscriber<T1, TRet> Func<T1, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, TRet>(name);

    protected ICallGateSubscriber<T1, T2, TRet> Func<T1, T2, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, T2, TRet>(name);

    protected ICallGateSubscriber<T1, T2, T3, TRet> Func<T1, T2, T3, TRet>(string name)
        => Svc.PluginInterface.GetIpcSubscriber<T1, T2, T3, TRet>(name);

    /// <summary>
    /// 値を返す IPC を呼ぶ。成功したら true。
    /// 戻り値が取れなかったことと、取れた値が期待通りでないことは別物なので、
    /// 呼び出し側は必ず戻り値の bool を見ること。
    /// </summary>
    protected bool TryInvoke<T>(string label, Func<T> call, out T value)
    {
        value = default!;

        if (!this.IsLoaded)
            return false;

        try
        {
            value = call();
            this.lastErrors.Remove(label);
            return true;
        }
        catch (IpcNotReadyError)
        {
            // 相手の起動順がこちらより後。時間が経てば直ることがある。
            this.Fail(label, "NotReady", "まだ IPC が登録されていません（相手の起動待ち）");
            return false;
        }
        catch (IpcTypeMismatchError ex)
        {
            // 相手が引数か戻り値の型を変えた。こちらの購読宣言を直す必要がある。
            this.Fail(label, "TypeMismatch",
                $"IPC の型が合いません。相手のソースを確認してください: {ex.Message}");
            return false;
        }
        catch (IpcLengthMismatchError ex)
        {
            // 引数の数が変わった。EzIPC の void は末尾に object を足す規約も疑う。
            this.Fail(label, "LengthMismatch", $"IPC の引数の数が合いません: {ex.Message}");
            return false;
        }
        catch (IpcValueNullError)
        {
            this.Fail(label, "ValueNull", "IPC が null を返しました");
            return false;
        }
        catch (Exception ex)
        {
            this.Fail(label, ex.GetType().Name,
                $"予期しない例外が発生しました: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>失敗を記録する。ログへ出しつつ、画面表示用にも残す。</summary>
    private void Fail(string label, string kind, string detail)
    {
        this.lastErrors[label] = detail;
        this.WarnThrottled($"{label}: {detail}", $"{label}:{kind}");
    }

    /// <summary>
    /// 値を返さない IPC を呼ぶ。
    /// true が返るのは「例外が飛ばなかった」ことだけを意味し、
    /// 相手の状態が実際に変わったことは意味しない。書いたら読み直して確かめる。
    /// </summary>
    protected bool TryAction(string label, Action call)
        => this.TryInvoke<bool>(label, () => { call(); return true; }, out _);

    /// <param name="message">出す文言。</param>
    /// <param name="kind">
    /// 間引きの単位。同じ kind の連続だけを抑える。省略時は文言そのものを単位にする。
    /// </param>
    protected void WarnThrottled(string message, string? kind = null)
    {
        var key = kind ?? message;
        var now = DateTime.UtcNow;

        if (this.lastWarnAt.TryGetValue(key, out var last) && now - last < WarnInterval)
            return;

        this.lastWarnAt[key] = now;
        Svc.Log.Warning($"[{this.InternalName}] {message}");
    }
}
