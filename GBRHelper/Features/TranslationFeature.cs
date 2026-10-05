using System;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using GBRHelper.Translation;
using GBRHelper.Ui;

namespace GBRHelper.Features;

/// <summary>
/// 機能①：GBR の日本語表示。
/// GBR のメイン画面（全タブと上の見出し）の英語を、日本語で表示する。
/// 要望：右側は「有効」のチェックだけにする（説明の文は出さない）。
/// 訳の無かった英語を集める機能と、状態の表示は「デバッグ」（DebugFeature。左上の「機能」を 5 回続けて押すと出る）へ移した。
///
/// 【仕組み】GbrTextHooks（ImGui の公開関数を横取りして、GBR の画面の中の文字だけを差し替える）。
///   GBR 本体にも GBR の設定ファイルにも触れない。ラベルは ImGui の ID が元と同じになるように組み立てるので、
///   タブの並びや開いていた見出しなどは訳しても変わらない（ImGuiLabel）。GBR のタブの並びは毎フレーム確かめて戻す（TabOrderRepair）。
///
/// 【なぜ毎フレーム差し替えるか】ImGui は毎フレーム画面を描き直す作りで、GBR は毎フレーム英語の文字を渡してくる。
///   「一度訳して置いておく」場所が無いので、描かれるたびに差し替える。ただし辞書を引くのは文言ごとに一度だけで、
///   結果を覚えて使い回す（TranslationTable の memo・GbrTextHooks の labels）。
///
/// 【辞書】GBRHelper/Translation/GbrJa.tsv（プラグインに埋め込む。設定フォルダへは書き出さない
///   ＝書き出した古い辞書が優先されて直した訳が効かない、という罠を避けるため）。
///
/// 【フックはいつ置くか】ImGui を描くスレッドの上（OnDraw）で置き・外す。
///   ImGui の関数が実行されている最中に、別のスレッドからその関数の先頭を書き換えないため。
/// </summary>
public sealed class TranslationFeature(Configuration config) : IFeature, IDisposable
{
    public const string FeatureName = "GBR の日本語表示";

    /// <summary>埋め込んだ辞書の名前（GBRHelper.csproj の LogicalName と同じ）。</summary>
    public const string DictionaryResource = "GBRHelper.Translation.GbrJa.tsv";

    private TranslationTable? table;
    private GbrTextHooks? hooks;
    private string error = string.Empty;
    private bool failed;

    public string Name => FeatureName;

    public string Description => "GBR のメイン画面の英語を、日本語で表示します。";

    /// <summary>負なので左の一覧で「ベンチャー回収」の上に出る（MainWindow.DrawLeftPane）。</summary>
    public int SortOrder => -100;

    /// <summary>左ペインのチェックは出さない（「有効」は右側に置く）。Tick は無いので常に true でよい。</summary>
    public bool Enabled
    {
        get => true;
        set { }
    }

    public bool HideEnableToggle => true;

    /// <summary>フックが置かれていて訳している最中か。</summary>
    public bool Active => this.hooks is not null;

    /// <summary>デバッグ（DebugFeature）に出す：置いたフック。無ければ null。</summary>
    public GbrTextHooks? Hooks => this.hooks;

    /// <summary>デバッグに出す：読んだ辞書。まだなら null。</summary>
    public TranslationTable? Table => this.table;

    /// <summary>デバッグに出す：開始できなかった理由（無ければ空）。</summary>
    public string Error => this.error;

    /// <summary>訳の無かった英語を集めるか（デバッグで切り替える）。フックを置き直しても引き継ぐ。</summary>
    public bool Collect
    {
        get => this.collect;
        set
        {
            this.collect = value;
            if (this.hooks is not null)
                this.hooks.CollectUntranslated = value;
        }
    }

    private bool collect;

    /// <summary>
    /// 描画のたびに呼ぶ（Plugin が UiBuilder.Draw の先頭で呼ぶ）。「有効」なら、まだ置いていなければフックを置く。無効ならフックを外す。
    /// 置けなかったときは、「有効」を入れ直すまで置き直さない（毎フレーム失敗し続けないため）。
    /// </summary>
    public void OnDraw()
    {
        if (!config.TranslationOn)
        {
            if (this.hooks is not null)
            {
                this.hooks.Dispose();
                this.hooks = null;
                Svc.Log.Information("[GBRHelper] GBR の日本語表示を止めました");
            }

            return;
        }

        if (this.hooks is null && !this.failed)
            this.Install();
    }

    private void Install()
    {
        try
        {
            this.table ??= LoadDictionary();
            this.hooks = GbrTextHooks.Install(this.table);
            this.hooks.CollectUntranslated = this.collect;
            this.error = string.Empty;
            Svc.Log.Information($"[GBRHelper] GBR の日本語表示を開始しました（フック {this.hooks.Installed}/{this.hooks.Requested}・辞書 {this.table.ExactCount}+{this.table.TemplateCount} 件）");
        }
        catch (Exception ex)
        {
            this.hooks = null;
            this.failed = true;
            this.error = $"{ex.GetType().Name}: {ex.Message}";
            Svc.Log.Error($"[GBRHelper] GBR の日本語表示を開始できませんでした: {ex}");
        }
    }

    /// <summary>プラグインに埋め込んだ辞書を読む。</summary>
    public static TranslationTable LoadDictionary()
    {
        using var stream = typeof(TranslationFeature).Assembly.GetManifestResourceStream(DictionaryResource)
                           ?? throw new FileNotFoundException($"埋め込みの辞書 {DictionaryResource} が見つかりません");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return TranslationTable.Parse(reader.ReadToEnd());
    }

    public void DrawRight()
    {
        // 要望：「有効」のチェックだけ（説明の文は出さない）。
        var on = config.TranslationOn;
        if (ImGui.Checkbox("有効##translationOn", ref on))
        {
            config.TranslationOn = on;
            config.Save();
            this.failed = false; // 入れ直したら、前に失敗していても置き直す
        }

        // 開始できなかったときだけ、その理由を出す（何も出ないと、訳されない理由が分からないため）。
        if (on && this.failed)
            ImGui.TextColored(ImGuiColors.DalamudRed, "開始できませんでした：" + this.error);
    }

    public void Dispose()
    {
        this.hooks?.Dispose();
        this.hooks = null;
    }
}
