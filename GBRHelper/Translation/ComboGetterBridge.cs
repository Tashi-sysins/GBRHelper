using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GBRHelper.Translation;

/// <summary>
/// 候補を関数（getter）で渡す ImGui の選択欄（igCombo_FnBoolPtr）で、候補の文字を差し替えるための橋渡し。
///
/// 【なぜ要るか】
///   imgui 1.88 の Combo は、getter から選択中の文字と各候補の文字を取り出し、C++ の内部で BeginCombo・Selectable を呼んで描く。
///   内部の呼び出しは公開関数を通らないので、公開関数のフックでは候補が訳せない。
///   そこで本物の getter を TranslatingGetter で包む。本物が返した文字を ITextSwap に渡し、差し替えの場所が返れば out_text を書き換える。
///   候補の部品の ID は候補の番号（PushID(i)）の下で作られ、選択は番号で決まるので、文字を差し替えても選択は変わらない。
///
/// 【寿命】
///   ・本物の getter とその data は Context に入れ、選択欄の呼び出しの間だけスタックの上に置く（Invoke の中）。
///   ・差し替えの文字の場所は ITextSwap が責任を持って生かしておく（ImGui は getter から戻ったあとも、その場所を読む）。
///   ・いま差し替えを担当する ITextSwap は、同じスレッドの、選択欄の呼び出しの間だけ覚える（入れ子になっても元に戻す）。
/// ゲームに触らないので、試験では導入済みの cimgui.dll の本物の Combo に通して確かめる。
/// </summary>
public static unsafe class ComboGetterBridge
{
    /// <summary>getter が返した文字を差し替える役。</summary>
    public interface ITextSwap
    {
        /// <summary>
        /// text（UTF-8・終端 0）を差し替えるなら、差し替えの文字（UTF-8・終端 0）の場所を返して true。
        /// 返す場所は、少なくとも選択欄の呼び出しが終わるまで動かず、消えないこと。
        /// ここで例外を出さないこと（出ても受け止めるが、その文字は差し替えない）。
        /// </summary>
        bool TrySwap(nint text, out nint replacement);
    }

    /// <summary>本物の getter とその data。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Context
    {
        public nint Getter;
        public nint Data;
    }

    [ThreadStatic]
    private static ITextSwap? current;

    /// <summary>包んだ getter の場所（形は imgui 1.88 の bool items_getter(void* data, int idx, const char** out_text)）。</summary>
    public static nint WrappedGetter => (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, byte>)&TranslatingGetter;

    /// <summary>
    /// 包んだ getter と、本物の getter・data を入れた Context の場所を call に渡して、選択欄を描かせる。
    /// call の中で、本物の選択欄の関数を (getter, data) の代わりに (wrappedGetter, contextPointer) で呼ぶこと。
    /// </summary>
    public static byte Invoke(ITextSwap swap, nint getter, nint data, Func<nint, nint, byte> call)
    {
        var context = new Context { Getter = getter, Data = data };
        var previous = current;
        current = swap;
        try
        {
            return call(WrappedGetter, (nint)(&context));
        }
        finally
        {
            current = previous;
        }
    }

    /// <summary>
    /// 包んだ getter。本物の getter を呼び、返った文字を差し替える。ここから例外を外へ出さない（ゲームが落ちるため）。
    /// 本物の getter の戻り値（false＝その番号の候補が無い）はそのまま返し、false のときは差し替えない。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte TranslatingGetter(nint data, int index, nint outText)
    {
        var context = (Context*)data;
        var result = ((delegate* unmanaged[Cdecl]<nint, int, nint, byte>)context->Getter)(context->Data, index, outText);

        try
        {
            if (result != 0 && outText != 0 && current is { } swap)
            {
                var text = *(nint*)outText;
                if (text != 0 && swap.TrySwap(text, out var replacement) && replacement != 0)
                    *(nint*)outText = replacement;
            }
        }
        catch
        {
            // 差し替えの役の例外は受け止め、元の文字のまま返す。
        }

        return result;
    }
}
