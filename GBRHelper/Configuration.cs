using System;
using Dalamud.Configuration;

namespace GBRHelper;

/// <summary>キャラクター別ファイルへ保存する設定。旧共有ファイルは移行時に読むだけ。</summary>
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>いまの設定の版。新しく作った設定はこの版になる。</summary>
    public const int CurrentVersion = 2;

    public System.Collections.Generic.Dictionary<ulong, Features.GatherProfile> GatherProfiles { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, Features.ProfileListMemory> GatherListMemory { get; set; } = new();
    public System.Collections.Generic.HashSet<string> GatherListReset { get; set; } = new();

    /// <summary>
    /// GBR の採集中に「Auto-Gatherに追加」で、無効の状態で先に足したリストの名前（設定の記録の鍵。2026-10-05 14〜16時の版）。
    /// GBR が止まって書き直すときに有効にする。無効なのはこちらが決めたことなので、利用者が無効にした印（GatherListMemory の Enabled）としては覚えない。
    /// 自動採集中は「Auto-Gatherに追加」を押せないようにしたので、新しく印は付かない（前の版で付いた印のために残す）。
    /// </summary>
    public System.Collections.Generic.HashSet<string> GatherListAddedDisabled { get; set; } = new();
    public bool GatherProfilesMigrated { get; set; }


    /// <summary>霊砂：キャラクターごとの、欲しい霊砂の選択と数（2026-10-05〜）。</summary>
    public System.Collections.Generic.Dictionary<ulong, Features.SandChoice> SandChoices { get; set; } = new();

    /// <summary>クリスタル・クラスター：キャラクターごとの、欲しい属性の選択と数（キーは属性のクリスタルの品番。2026-10-05〜）。</summary>
    public System.Collections.Generic.Dictionary<ulong, Features.SandChoice> CrystalChoices { get; set; } = new();

    // 霊砂（旧「時限素材・霊砂」）の処理の異常終了後、利用者が明示的に設定を戻すための記録。
    // 名前に Timed が残っているのは、名前を変えると前回の復元の記録が読めなくなるため。
    public string TimedRecoveryTag { get; set; } = "";
    public System.Collections.Generic.Dictionary<string, string> TimedOriginalSettings { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, string> TimedWrittenSettings { get; set; } = new();

    /// <summary>
    /// 設定の版。古いファイルを読むと、そのファイルに書かれた版で上書きされる
    /// （書かれた値で移行が要るかを判断する）。
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    // ------------------------------------------------------------------
    // 帰還先（版 2）

    /// <summary>
    /// 帰還先の宿屋の部屋のエリア番号（177 など）。0 なら未設定。
    ///
    /// エリア番号 0 の区域は存在しないので、0 を未設定に使ってよい。
    /// 飛べるかどうかは実行時にアクセス済みの一覧で確かめる（ここに入っている値を信じきらない）。
    /// </summary>
    public uint InnTerritoryId { get; set; }

    /// <summary>
    /// どの宿屋へ行くかを Lifestream に任せるか。
    /// Lifestream の設定の「Preferred inn」→ 近くのエーテライトの都市 → テレポ代が一番安い都市、の順で選ばれる。
    /// </summary>
    public bool InnAuto { get; set; }

    /// <summary>
    /// 帰還先の宿屋の名前。画面に出すためだけに持つ。
    /// 判定には使わない（名前はパッチや言語で変わるため）。
    /// </summary>
    public string InnName { get; set; } = string.Empty;

    // ------------------------------------------------------------------
    // 旧設定（版 1）。版 2 への移行で読むだけ。消さない・書き換えない。
    //
    // 名前を変えたり消したりすると、古い設定ファイルの値が読めなくなり、
    // 利用者が選んでいた都市が分からなくなる（設定の名前を変えて値が消える罠）。

    /// <summary>【版 1】帰還先エーテライトの行 ID。0 なら未設定だった。</summary>
    public uint HomeAetheryteId { get; set; }

    /// <summary>【版 1】帰還先エーテライトの枝番。</summary>
    public byte HomeAetheryteSubIndex { get; set; }

    /// <summary>【版 1】帰還先の名前（表示用）。</summary>
    public string HomeAetheryteName { get; set; } = string.Empty;

    // ------------------------------------------------------------------

    /// <summary>
    /// ベンチャー回収を使うか（要望：GBR の ON/OFF に連動して勝手に動くのではなく、ベンチャー回収の画面の「有効」で決める）。
    /// ON で、かつ GBR の自動採集が動いているときだけ回収に行く（決定「GBR の採集中だけ回収」）。
    /// 既定は ON（同日 14時の指示）。13:25 の版は既定 OFF の「VentureRelayEnabled」で保存していたので、名前を変えて既定 ON を効かせる。
    /// </summary>
    public bool VentureRelayOn { get; set; } = true;

    /// <summary>13:25 の版の「有効」（既定 OFF）。もう読まない（設定ファイルとの互換のために残す）。</summary>
    public bool VentureRelayEnabled { get; set; }

    /// <summary>
    /// 回収が終わったあと、自動採集を再開するか。
    /// 切ると「回収したらそのまま止まる」動きになる。
    /// </summary>
    public bool ResumeAfterCollect { get; set; } = true;

    /// <summary>
    /// 回収に失敗したときも自動採集を再開するか。
    ///
    /// 既定は true。失敗の多くは「呼び鈴が見つからない」「経路が引けない」で、
    /// 採集そのものは続けられる。止めてしまうと離席中に丸ごと無駄になる。
    /// </summary>
    public bool ResumeAfterFailure { get; set; } = true;

    /// <summary>
    /// 一度失敗したあと、次に回収を試すまで待つ時間（分）。
    ///
    /// 失敗した直後に再挑戦すると、同じ理由で失敗し続けて
    /// 採集と往復を繰り返すことになる。間隔を空ける。
    /// </summary>
    public int RetryCooldownMinutes { get; set; } = 30;

    /// <summary>設定画面をゲーム起動時に開くか。</summary>
    public bool OpenOnStartup { get; set; }

    // ------------------------------------------------------------------
    // GBRHelper の機能ごとの有効/無効（左ペインのチェックボックス）
    //
    // 【ベンチャー回収】はこのプラグインの元々の機能で、GBR の自動採集と連動して ON/OFF が
    // 決まる（左ペインのチェックボックスは出さない）ため、ここには独立したフラグを置かない。

    /// <summary>
    /// 機能②：Lv帯別の未採取素材を Auto-Gather に追加する機能を有効にするか。
    /// 2026-10-05 から機能②は左ペインのチェックを出さない（常に使える）ため、この値はもう読まない。
    /// 旧設定ファイルとの互換のために残している。
    /// </summary>
    public bool UnvisitedFeatureEnabled { get; set; }

    // 機能②の「ベンチャーで依頼できる品だけにする」設定（UnlockVentureOnly）は 2026-10-05 に外した。
    // 決定「復興用なども含めない」により、常にベンチャーで依頼できる品だけを対象にする
    // （UnvisitedFeature.VentureRequestOnly）。古い設定ファイルに残った値は読み込み時に無視される。

    /// <summary>機能③：時限素材をワンクリックで Auto-Gather に追加する機能を有効にするか。もう読まない（旧設定ファイルとの互換のために残している）。</summary>
    public bool TimedFeatureEnabled { get; set; }

    /// <summary>機能④：Lv帯別の全素材 999 補充を Auto-Gather に追加する機能を有効にするか。</summary>
    public bool Stock999FeatureEnabled { get; set; }

    /// <summary>
    /// 機能①：GBR ウィンドウの英語を日本語に差し替える機能を有効にするか。
    /// 2026-10-05 から機能①は常に ONなので、この値はもう読まない。旧設定ファイルとの互換のために残している。
    /// </summary>
    public bool TranslationFeatureEnabled { get; set; }

    /// <summary>
    /// GBR の日本語表示の「有効」（要望：右側の「GBR の日本語表示」の下にチェックを置く）。既定は ON。
    /// 以前の TranslationFeatureEnabled（左ペインのチェック・既定 OFF）とは別の名前にして、初期値 ON を確実に効かせる。
    /// </summary>
    public bool TranslationOn { get; set; } = true;

    /// <summary>最後に右ペインに出していた機能の名前（起動時にそれを選び直す）。</summary>
    public string LastSelectedFeature { get; set; } = string.Empty;

    [NonSerialized]
    private Action? save;
    public void SetSave(Action action) => save = action;
    public void Save() => save?.Invoke();

    // 参照を保持している機能へ、同じ設定オブジェクトのまま本人の内容を渡す。
    public void CopyFrom(Configuration source)
    {
        foreach (var property in typeof(Configuration).GetProperties())
            if (property.CanWrite) property.SetValue(this, property.GetValue(source));
    }

    public System.Collections.Generic.Dictionary<int, string> ArtisanListNames { get; set; } = new();
    public System.Collections.Generic.Dictionary<int, string> ArtisanPendingNames { get; set; } = new();
}
