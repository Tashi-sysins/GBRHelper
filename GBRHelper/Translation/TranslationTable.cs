using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GBRHelper.Translation;

/// <summary>
/// 英語 → 日本語の訳の表（機能①「GBR の日本語表示」の辞書）。ゲームに触らない純粋な部品。
///
/// 【辞書の書き方】（GBRHelper/Translation/GbrJa.tsv）
///   英語&lt;TAB&gt;日本語[&lt;TAB&gt;種別]
///   ・「# 」（# と空白）で始まる行、「#」だけの行、空行は読まない。
///     GBR のデバッグタブには「#Alarm Groups」のように # で始まる英語があるため、# の直後が空白でなければ中身として読む。
///   ・\n は改行、\t はタブ、\\ は \ そのもの（GBR の説明文は複数行のため）。
///   ・英語に {0} {1} … を含む行は「型」。{n} の所はどんな文字列にも合い、訳の同じ {n} の所へそのまま入る
///     （例：「Enabled: {0}」→「有効: {0}」）。{Alarm} のような数字でない波括弧は普通の文字として扱う。
///   ・3 列目（種別）は試験だけが読む。GBR の DLL に文字列として入っていない（列挙の名前・大文字にした見出し等）
///     ことを示す。照合には使わない。
///
/// 【照合の順】
///   1. 完全一致
///   2. 前後の空白を除いて一致（空白は元のまま残す。「 empty;」のような断片のため）
///   3. 型（英語の固定部分が長いものから順に試す）
/// 訳した結果は覚えておき、同じ文字列は二度目から表を引かない（毎フレーム同じ文字列が来るため）。
/// 覚えておく数には上限があり、超えたら一度捨てる（デバッグ画面の時刻など、毎回変わる文字列で膨らまないように）。
/// </summary>
public sealed class TranslationTable
{
    /// <summary>覚えておく訳の上限。超えたら全部捨てて覚え直す。</summary>
    public const int MemoLimit = 20000;

    /// <summary>この長さを超える文字列は訳さない（長い文章の照合で時間を使わないため）。</summary>
    public const int MaxLength = 4096;

    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> exact = new(StringComparer.Ordinal);
    private readonly List<Template> templates = new();
    private readonly HashSet<string> outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> memo = new(StringComparer.Ordinal);

    private TranslationTable()
    {
    }

    /// <summary>完全一致の項目数。</summary>
    public int ExactCount => this.exact.Count;

    /// <summary>型の項目数。</summary>
    public int TemplateCount => this.templates.Count;

    /// <summary>読み込んだ項目（試験で辞書の中身を確かめるため）。</summary>
    public IReadOnlyList<Entry> Entries { get; private set; } = Array.Empty<Entry>();

    /// <summary>辞書の 1 行。</summary>
    public sealed record Entry(int Line, string English, string Japanese, string Kind)
    {
        /// <summary>英語に {n} を含む（型）か。</summary>
        public bool IsTemplate => Placeholder.IsMatch(this.English);
    }

    /// <summary>
    /// 辞書の文字列を読む。書き方の誤り（列の不足・重複・{n} の食い違い等）は例外にする。
    /// 誤った辞書で黙って動くと、どこが訳されないのか分からなくなるため。
    /// </summary>
    public static TranslationTable Parse(string text)
    {
        var table = new TranslationTable();
        var entries = new List<Entry>();
        var errors = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i];

            if (IsComment(line))
                continue;

            var cols = line.Split('\t');
            if (cols.Length is < 2 or > 3)
            {
                errors.Add($"{lineNo} 行目：列の数が {cols.Length} です（英語・日本語・種別（任意）の 2〜3 列）");
                continue;
            }

            string en, ja;
            try
            {
                en = Unescape(cols[0]);
                ja = Unescape(cols[1]);
            }
            catch (FormatException ex)
            {
                errors.Add($"{lineNo} 行目：{ex.Message}");
                continue;
            }

            var kind = cols.Length == 3 ? cols[2].Trim() : string.Empty;

            if (en.Trim().Length == 0 || ja.Trim().Length == 0)
            {
                errors.Add($"{lineNo} 行目：英語か日本語が空です");
                continue;
            }

            var entry = new Entry(lineNo, en, ja, kind);

            if (entry.IsTemplate)
            {
                if (!SamePlaceholders(en, ja))
                {
                    errors.Add($"{lineNo} 行目：{{n}} の組が英語と日本語で違います（{en} → {ja}）");
                    continue;
                }

                if (FixedLength(en) < 3)
                {
                    // 「{0}: {1}」のような型は何にでも合ってしまう。訳の取り違えを防ぐため受け付けない。
                    errors.Add($"{lineNo} 行目：型の固定部分が短すぎます（{en}）");
                    continue;
                }

                if (table.templates.Exists(t => t.English == en))
                {
                    errors.Add($"{lineNo} 行目：同じ英語が 2 回あります（{en}）");
                    continue;
                }

                table.templates.Add(new Template(en, ja));
            }
            else
            {
                if (!SamePrintfSpecifiers(en, ja))
                {
                    errors.Add($"{lineNo} 行目：% の書式指定が英語と日本語で違います（{en} → {ja}）");
                    continue;
                }

                if (!table.exact.TryAdd(en, ja))
                {
                    errors.Add($"{lineNo} 行目：同じ英語が 2 回あります（{en}）");
                    continue;
                }
            }

            table.outputs.Add(ja);
            entries.Add(entry);
        }

        if (errors.Count > 0)
            throw new FormatException("訳の辞書に誤りがあります：\n" + string.Join("\n", errors));

        // 固定部分が長い型から試す（短い型が長い型の一部に合ってしまうのを防ぐ）。
        table.templates.Sort((a, b) => b.FixedLength.CompareTo(a.FixedLength));
        table.Entries = entries;
        return table;
    }

    /// <summary>
    /// 訳す。訳が無ければ null。
    /// 自分の訳した結果（日本語）がもう一度来ても訳さない（二重に訳さない）。
    /// </summary>
    public string? Translate(string text)
    {
        if (text.Length == 0 || text.Length > MaxLength)
            return null;

        if (this.memo.TryGetValue(text, out var cached))
            return cached;

        var result = this.Lookup(text);

        if (this.memo.Count >= MemoLimit)
            this.memo.Clear();

        this.memo[text] = result;

        // 訳した結果がもう一度来ても訳さない（型の訳は「自分の訳」の一覧に入っていないため、ここで覚える）。
        // 結果が辞書の英語そのものと同じなら、それは訳すべき英語なので覚えない。
        if (result is not null && !this.exact.ContainsKey(result))
            this.memo.TryAdd(result, null);

        return result;
    }

    private string? Lookup(string text)
    {
        if (this.exact.TryGetValue(text, out var hit))
            return hit;

        if (this.outputs.Contains(text))
            return null;

        // 前後の空白を除いて一致（空白は元のまま残す）。
        var start = 0;
        var end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        if (start > 0 || end < text.Length)
        {
            if (end > start && this.exact.TryGetValue(text[start..end], out var inner))
                return string.Concat(text.AsSpan(0, start), inner, text.AsSpan(end));
        }

        foreach (var t in this.templates)
        {
            if (t.TryApply(text) is { } applied)
                return applied;
        }

        return null;
    }

    /// <summary>読まない行（空行・「# 」で始まる行・「#」だけの行）か。</summary>
    internal static bool IsComment(string line)
        => line.Length == 0 || line == "#" || line.StartsWith("# ", StringComparison.Ordinal);

    /// <summary>辞書の書き方の \n \t \\ を元の文字に戻す。</summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0)
            return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (i + 1 >= s.Length)
                throw new FormatException("行の最後が \\ で終わっています");

            var n = s[++i];
            sb.Append(n switch
            {
                'n' => '\n',
                't' => '\t',
                '\\' => '\\',
                _ => throw new FormatException($"\\{n} は使えません（使えるのは \\n \\t \\\\ だけ）"),
            });
        }

        return sb.ToString();
    }

    /// <summary>英語と日本語で {n} の組（番号の集まり）が同じか。</summary>
    internal static bool SamePlaceholders(string en, string ja)
    {
        static SortedSet<int> Numbers(string s)
        {
            var set = new SortedSet<int>();
            foreach (Match m in Placeholder.Matches(s))
                set.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return set;
        }

        return Numbers(en).SetEquals(Numbers(ja));
    }

    /// <summary>
    /// 英語と日本語で printf の書式指定（%d %2.3f %% 等）の並びが同じか。
    /// 数値欄の表示形式（"%2.3f Seconds" 等）を訳すとき、指定を壊すと GBR の表示が崩れるため。
    /// </summary>
    internal static bool SamePrintfSpecifiers(string en, string ja)
    {
        static List<string> Specs(string s)
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(s, @"%[-+ #0]*\d*(?:\.\d+)?(?:hh|h|ll|l|I64|I32)?[diuoxXfFeEgGaAcspn%]"))
                list.Add(m.Value);
            return list;
        }

        var a = Specs(en);
        var b = Specs(ja);
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    private static int FixedLength(string template)
        => Placeholder.Replace(template, string.Empty).Trim().Length;

    /// <summary>{n} を含む型。</summary>
    private sealed class Template
    {
        private readonly Regex pattern;
        private readonly int[] order;

        public Template(string english, string japanese)
        {
            this.English = english;
            this.Japanese = japanese;
            this.FixedLength = TranslationTable.FixedLength(english);

            // 固定部分はそのまま、{n} の所はどんな文字列にも合う形にする。
            // 最後の {n} 以外は「最短で合う」形にして、後ろの固定部分が先に食われないようにする。
            var parts = Placeholder.Split(english);
            var sb = new StringBuilder("^");
            var nums = new List<int>();
            var matches = Placeholder.Matches(english);
            for (var i = 0; i < matches.Count; i++)
            {
                sb.Append(Regex.Escape(parts[i * 2]));
                sb.Append(i == matches.Count - 1 ? "(.*)" : "(.*?)");
                nums.Add(int.Parse(matches[i].Groups[1].Value, CultureInfo.InvariantCulture));
            }

            sb.Append(Regex.Escape(parts[^1]));
            sb.Append('$');
            this.pattern = new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
            this.order = nums.ToArray();
        }

        public string English { get; }

        public string Japanese { get; }

        public int FixedLength { get; }

        public string? TryApply(string text)
        {
            var m = this.pattern.Match(text);
            if (!m.Success)
                return null;

            var values = new Dictionary<int, string>();
            for (var i = 0; i < this.order.Length; i++)
                values[this.order[i]] = m.Groups[i + 1].Value;

            return Placeholder.Replace(this.Japanese, x =>
                values.TryGetValue(int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture), out var v) ? v : x.Value);
        }
    }
}
