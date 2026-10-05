using System;
using System.Text;

namespace GBRHelper.Translation;

/// <summary>
/// ImGui のラベル（ボタン・チェックボックス・タブ等の文字）を訳すときの組み立て方。ゲームに触らない純粋な部品。
///
/// 【ImGui のラベルの決まり】（imgui 1.88。imgui.cpp:1820 ImHashStr・3368 ImGuiWindow::GetID）
///   ・「##」より後ろは画面に出ない（同じ文字のボタンを区別するための印）。
///   ・部品の ID は ImHashStr(ラベル全体, 種)。種は呼んだ時点の窓の ID の積み重ねの一番上（IDStack.back()）。
///     「###」があると、そこで種に戻ってから続きを計算する。タブの ID も同じ（imgui_widgets.cpp:7767 TabBarCalcTabID）。
///
/// 【訳し方】ラベルを「訳###（つなぎ1文字＋4バイト）」にする。
///   ・画面には「訳」だけが出る（「##」より後ろは出ない）。
///   ・末尾の4バイトは、ImHashStr の結果が「元のラベルの ID」とちょうど同じになるように計算して決める。
///     ImHashStr は CRC32 なので、最後に4バイトを足すと結果を任意の値に合わせられる（CRC の性質）。
///   ・そのため、訳しても部品の ID は元と完全に同じ。タブの並び・開いていた見出し・選択欄の状態などは訳の有無で変わらない。
///     （以前は「訳###元のラベル」としていたため ID が変わり、GBR のタブの並びが崩れた）
///   折りたたみの見出し（TreeNode）は Dalamud が ID を先に元のラベルから作り、表示用の文字だけを別に渡すので、この組み立ては使わない。
/// </summary>
public static class ImGuiLabel
{
    /// <summary>ラベルを「画面に出る部分」と「## から後ろ」に分ける。</summary>
    public static (string Visible, string Hidden) Split(string label)
    {
        var i = label.IndexOf("##", StringComparison.Ordinal);
        return i < 0 ? (label, string.Empty) : (label[..i], label[i..]);
    }

    /// <summary>
    /// ラベルを訳す。返すのは UTF-8 のラベル（終端 0 付き）。画面には訳が出て、ID は seed の下で元のラベルと同じ。
    /// 訳が無い・画面に出る部分が空・ID を合わせられなかったときは null（元のまま使う）。
    /// </summary>
    /// <param name="seed">呼んだ時点の窓の ID の積み重ねの一番上（ImGuiWindow.IDStack.back()）。</param>
    public static byte[]? Translate(string label, uint seed, Func<string, string?> translate)
    {
        var (visible, _) = Split(label);
        if (visible.Length == 0)
            return null;

        var translated = translate(visible);
        if (translated is null || translated == visible)
            return null;

        // 訳に「#」が並ぶと ImGui が ID の印と取り違える。辞書の側で避けているが、念のため崩しておく。
        translated = translated.Replace("##", "#\u200B#", StringComparison.Ordinal);

        var target = ImHashStr(Encoding.UTF8.GetBytes(label), seed);
        var forged = KeepId(Encoding.UTF8.GetBytes(translated + "###"), target, seed);
        if (forged is null)
            return null;

        var result = new byte[forged.Length + 1];
        forged.CopyTo(result, 0);
        return result;
    }

    /// <summary>
    /// prefix（「訳###」）のあとに、つなぎ1文字と4バイトを足して、ImHashStr(結果, seed) が target になるラベルを作る。
    /// 4バイトに 0（文字列の終わり）や「#」（ID の印）が出たら、つなぎの文字を変えて計算し直す。作れなければ null。
    /// 作ったラベルは、ImGui と同じ計算で target になることを確かめてから返す。
    /// </summary>
    internal static byte[]? KeepId(ReadOnlySpan<byte> prefix, uint target, uint seed)
    {
        Span<int> index = stackalloc int[4];
        for (var filler = (byte)'A'; filler <= (byte)'Z'; filler++)
        {
            var buf = new byte[prefix.Length + 1 + 4];
            prefix.CopyTo(buf);
            buf[prefix.Length] = filler;

            // つなぎの文字までの CRC の途中の値（つなぎは「#」でないので、この先の4バイトは「###」の判定に関わらない）。
            var state = CrcState(buf.AsSpan(0, prefix.Length + 1), seed);

            // 終わりの値 ~target から逆にたどって、4バイトそれぞれが引く表の番号を決める（CRC の表の上位1バイトは全部違う）。
            var c = ~target;
            for (var k = 3; k >= 0; k--)
            {
                index[k] = TopByteToIndex[c >> 24];
                c = (c ^ Crc32[index[k]]) << 8;
            }

            // 前から、表の番号になるように4バイトを決める。
            var crc = state;
            var ok = true;
            for (var k = 0; k < 4; k++)
            {
                var b = (byte)((crc ^ (uint)index[k]) & 0xFF);
                if (b is 0 or (byte)'#')
                {
                    ok = false;
                    break;
                }

                buf[prefix.Length + 1 + k] = b;
                crc = (crc >> 8) ^ Crc32[index[k]];
            }

            if (ok && ImHashStr(buf, seed) == target)
                return buf;
        }

        return null;
    }

    /// <summary>
    /// ID を持たない文字（説明文・表のセル等）を訳す。「##」を含むものは訳さない
    /// （そのまま描かれる文字に「##」が入っていることは GBR の画面では無く、入っていたら印の可能性があるため）。
    /// </summary>
    public static string? TranslatePlain(string text, Func<string, string?> translate)
    {
        if (text.Length == 0 || text.Contains("##", StringComparison.Ordinal))
            return null;

        var translated = translate(text);
        return translated is null || translated == text ? null : translated;
    }

    /// <summary>
    /// 文字の幅を測る呼び出し（CalcTextSize）の文字を訳す。
    /// GBR は英語の幅を測って部品を並べるので、描く文字を訳すなら測る文字も同じ訳にしないと列がずれる。
    /// 「## から後ろを隠す」指定のときは、画面に出る部分だけを訳して後ろはそのまま残す（隠れるので幅は変わらない）。
    /// </summary>
    public static string? TranslateMeasured(string text, bool hideAfterDoubleHash, Func<string, string?> translate)
    {
        var (visible, hidden) = Split(text);
        if (hidden.Length > 0 && !hideAfterDoubleHash)
            return null;

        if (visible.Length == 0)
            return null;

        var translated = translate(visible);
        if (translated is null || translated == visible)
            return null;

        return hidden.Length > 0 ? translated + hidden : translated;
    }

    /// <summary>ImGui の ImHashStr と同じ計算（終端 0 までの文字列として。imgui.cpp:1820-1846）。</summary>
    public static uint ImHashStr(ReadOnlySpan<byte> data, uint seed)
        => ~CrcState(data, seed);

    /// <summary>ImHashStr の、最後に反転する前の値（「###」の所で種に戻す）。</summary>
    private static uint CrcState(ReadOnlySpan<byte> data, uint seed)
    {
        var start = ~seed;
        var crc = start;
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c == (byte)'#' && i + 2 < data.Length && data[i + 1] == (byte)'#' && data[i + 2] == (byte)'#')
                crc = start;
            crc = (crc >> 8) ^ Crc32[(crc & 0xFF) ^ c];
        }

        return crc;
    }

    private static readonly uint[] Crc32 = BuildTable();

    /// <summary>CRC の表の上位1バイト → 表の番号（256 個とも違うので逆に引ける）。</summary>
    private static readonly int[] TopByteToIndex = BuildReverse();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }

    private static int[] BuildReverse()
    {
        var reverse = new int[256];
        for (var i = 0; i < 256; i++)
            reverse[Crc32[i] >> 24] = i;
        return reverse;
    }
}
