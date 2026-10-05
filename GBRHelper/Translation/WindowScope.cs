namespace GBRHelper.Translation;

/// <summary>
/// いま描いている呼び出しが、訳す対象の窓（GBR のメイン画面）の中かを判定する。ゲームに触らない純粋な部品
/// （窓の積み重ねの読み方は呼び出し側が渡す。試験では偽物の積み重ねを渡す）。
///
/// 【判定のしかた】いま Begin されている窓の積み重ね（ImGuiContext.CurrentWindowStack）に、対象の窓が入っていれば「中」。
///   GBR の画面の文字は、Dalamud が GBR の窓を Begin してから End するまでの間（Window.Draw の中）に描かれる。
///   その間に開かれる子窓・吹き出し・選択欄の一覧・ポップアップは、すべて GBR の窓の上に積まれる。
///   GBR の窓を End したあとに描かれるもの（ほかのプラグインの窓・その吹き出し）は、積み重ねに GBR の窓が無い。
///
/// 【以前のやり方をやめた理由】
///   以前は窓の親のつながり（ImGuiWindow.ParentWindowInBeginStack）をたどっていた。ところがこの値は、
///   その窓がそのフレームで最初に Begin されたときにしか更新されない。吹き出しの窓（##Tooltip_00）は同じフレームの中で
///   ほかのプラグインにも使い回されるため、GBR の吹き出しのあとに描かれたほかのプラグインの吹き出しを「GBR の中」と、
///   逆の順では GBR の吹き出しを「外」と取り違えた（導入済みの cimgui 1.88 で再現）。
///   積み重ねは Begin と End で毎回正しく積み降ろしされるので、この取り違えが起きない。
///
/// 翻訳を ON にしている間は、すべてのプラグインの文字を描く呼び出しのたびにこの判定が走る。
/// そのため積み重ねの読み方はデリゲートではなく構造体の型引数で受け取り、呼び出しの手間を省いている。
/// </summary>
public static class WindowScope
{
    /// <summary>見る段数の上限（壊れた値で長く回らないため）。普通の積み重ねは数段。</summary>
    public const int MaxDepth = 64;

    /// <summary>窓の積み重ねの読み方。</summary>
    public interface IStack
    {
        /// <summary>積み重ねの段数。</summary>
        int Count { get; }

        /// <summary>下から index 段目の窓の ID（窓が無ければ 0）。</summary>
        uint IdAt(int index);
    }

    /// <summary>
    /// 積み重ねに ID が targetId の窓があれば true。上（最後に Begin した窓）から見る。
    /// </summary>
    public static bool IsInside<TStack>(TStack stack, uint targetId)
        where TStack : struct, IStack
    {
        var count = stack.Count;
        if (count <= 0)
            return false;

        var lowest = count > MaxDepth ? count - MaxDepth : 0;
        for (var i = count - 1; i >= lowest; i--)
        {
            if (stack.IdAt(i) == targetId)
                return true;
        }

        return false;
    }
}
