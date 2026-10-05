using System;

namespace GBRHelper.Ui;

/// <summary>
/// GBRHelper の 1 機能。左ペインに 1 行（チェックボックス）として並び、
/// 選ばれた機能が右ペインに自分の設定・状態を描く。
///
/// BundleOfTweaks の TweakSystem の考え方をごく薄くなぞったもの。
/// Tweak は「ゲームにパッチを当てる」部品だが、ここではもっと緩く
/// 「機能のオン・オフと、設定の表示」だけを束ねる。
///
/// 【設計の注意】
/// - DrawRight は毎フレーム呼ばれる。重い処理はキャッシュする。
/// - Enabled の切り替えで副作用（例：Tick を回し始める）が要るものは
///   OnEnabled / OnDisabled で明示する。コンストラクタで予約した副作用は使わない。
/// - 本当にゲームに触る機能は、Enabled でも「ゲームにいない」「他プラグイン未導入」等の
///   理由で動かないことがある。そのとき DrawRight にその理由を日本語で出す。
/// </summary>
public interface IFeature
{
    /// <summary>左ペインに出す名前（日本語・短く）。</summary>
    string Name { get; }

    /// <summary>右ペインの先頭に出す1〜2行の説明。</summary>
    string Description { get; }

    /// <summary>
    /// 他の機能より先に試したいものを上に並べる。小さいほど上。
    /// 既存の「ベンチャー回収」は 0。採取補助系は 100 番台。翻訳は 500。記録・検証は 900 番台。
    /// </summary>
    int SortOrder { get; }

    /// <summary>有効か。設定で保存される。</summary>
    bool Enabled { get; set; }

    /// <summary>
    /// 左ペインのチェックボックスだけで切り替えずに、右ペインで動作させるタイプの機能
    /// （「ベンチャー回収」など、GBR と連動して ON/OFF が決まる機能）は true を返す。
    /// true のときは左ペインのチェックボックスを隠し、見出しだけ出す。
    /// </summary>
    bool HideEnableToggle => false;

    /// <summary>
    /// 右ペインの先頭に機能名と説明（Name・Description）を出すか。
    /// 右ペインの左上から自分の部品を並べたい機能は false にする（説明は左ペインのマウスオーバーで出る）。
    /// </summary>
    bool ShowHeader => true;

    /// <summary>有効になった瞬間。1回だけ呼ばれる。</summary>
    void OnEnabled() { }

    /// <summary>無効になった瞬間。1回だけ呼ばれる。</summary>
    void OnDisabled() { }

    /// <summary>右ペイン。選ばれているときだけ呼ばれる。</summary>
    void DrawRight();

    /// <summary>
    /// 毎フレーム呼ばれる。Enabled=false のときは呼ばれない。
    /// 画面を描くフレームでなくても呼ばれる（UI の描画は DrawRight に書く）。
    /// </summary>
    void Tick() { }
}
