using System.Text;

namespace UniCrCapture.Core;

/// <summary>1文字とその位置。Windows の OCR は日本語を1文字ずつの「語」に分けて返すことが多いので、
/// 見出しなどは語ではなく文字の並びで探す。</summary>
public readonly record struct Glyph(char C, double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
    public double Right => X + W;
}

/// <summary>見た目の1行（Y が重なる語の集まり）。</summary>
public sealed class VisualLine
{
    public List<OcrWord> Words { get; } = new();
    public List<Glyph> Glyphs { get; } = new();
    public double CenterY => Words.Count == 0 ? 0 : Words.Average(w => w.CenterY);
    public double Top => Words.Min(w => w.Y);
    public double Bottom => Words.Max(w => w.Bottom);
    /// <summary>空白を除いて正規化した文字列（Glyphs と同じ並び）。</summary>
    public string Text => new(Glyphs.Select(g => g.C).ToArray());
}

/// <summary>見つかった文字列の範囲。</summary>
public readonly record struct TextHit(double X, double Y, double Right, double Bottom)
{
    public double CenterX => (X + Right) / 2;
    public double CenterY => (Y + Bottom) / 2;
    public double Width => Right - X;
    public double Height => Bottom - Y;
}

public static class TextLayout
{
    /// <summary>OCR の読み違えやすい文字をそろえる（比べるときだけ使う）。</summary>
    public static char Normalize(char c)
    {
        // 全角英数字 → 半角
        if (c >= '！' && c <= '～') c = (char)(c - '！' + '!');
        return c switch
        {
            '一' or '－' or '―' or '‐' or '—' or '−' => 'ー',
            '：' => ':',
            '％' => '%',
            '口' => 'ロ',
            '力' => 'カ',
            '工' => 'エ',
            _ => c,
        };
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(Normalize(ch));
        }
        return sb.ToString();
    }

    /// <summary>語を文字に分ける。1語の幅を文字数で等分して、各文字のおおよその位置にする。</summary>
    public static IEnumerable<Glyph> ToGlyphs(OcrWord w)
    {
        var chars = Normalize(w.Text);
        if (chars.Length == 0) yield break;
        var cw = w.Width / chars.Length;
        for (var i = 0; i < chars.Length; i++)
            yield return new Glyph(chars[i], w.X + cw * i, w.Y, cw, w.Height);
    }

    /// <summary>語を見た目の行にまとめる。中心の Y が、文字の高さの半分以内なら同じ行。</summary>
    public static List<VisualLine> GroupLines(IEnumerable<OcrWord> words)
    {
        var sorted = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.CenterY).ToList();
        var lines = new List<VisualLine>();
        foreach (var w in sorted)
        {
            var line = lines.LastOrDefault();
            var tol = Math.Max(4, (line?.Words.Average(x => x.Height) ?? w.Height) * 0.5);
            if (line is null || Math.Abs(line.CenterY - w.CenterY) > tol)
            {
                line = new VisualLine();
                lines.Add(line);
            }
            line.Words.Add(w);
        }
        foreach (var line in lines)
        {
            line.Words.Sort((a, b) => a.X.CompareTo(b.X));
            foreach (var w in line.Words) line.Glyphs.AddRange(ToGlyphs(w));
        }
        return lines;
    }

    /// <summary>行の中から needle を探す。長い needle は1文字までの読み違えを許す。</summary>
    public static TextHit? Find(VisualLine line, string needle, double minX = double.MinValue, double maxX = double.MaxValue)
    {
        var target = Normalize(needle);
        var glyphs = line.Glyphs;
        var allowed = target.Length >= 4 ? 1 : 0;
        TextHit? best = null;
        var bestMiss = int.MaxValue;
        for (var i = 0; i + target.Length <= glyphs.Count; i++)
        {
            if (glyphs[i].CenterX < minX || glyphs[i + target.Length - 1].CenterX > maxX) continue;
            var miss = 0;
            for (var j = 0; j < target.Length && miss <= allowed; j++)
                if (glyphs[i + j].C != target[j]) miss++;
            if (miss > allowed || miss >= bestMiss) continue;
            bestMiss = miss;
            var first = glyphs[i];
            var last = glyphs[i + target.Length - 1];
            best = new TextHit(first.X, glyphs.Skip(i).Take(target.Length).Min(g => g.Y),
                last.Right, glyphs.Skip(i).Take(target.Length).Max(g => g.Y + g.H));
            if (miss == 0) break;
        }
        return best;
    }

    public static (VisualLine Line, TextHit Hit)? FindInLines(IEnumerable<VisualLine> lines, string needle)
    {
        foreach (var line in lines)
        {
            var hit = Find(line, needle);
            if (hit is not null) return (line, hit.Value);
        }
        return null;
    }

    /// <summary>数字として読む。OCR が数字と取り違えやすい文字を直して、数字以外を捨てる。</summary>
    public static string DigitsOnly(string s)
    {
        var sb = new StringBuilder();
        foreach (var raw in s)
        {
            var c = Normalize(raw);
            c = c switch
            {
                'O' or 'o' or 'D' or 'Q' => '0',
                'l' or 'I' or '|' or 'i' or '!' => '1',
                'Z' or 'z' => '2',
                'S' or 's' => '5',
                'B' => '8',
                'g' => '9',
                _ => c,
            };
            if (c is >= '0' and <= '9') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>文字列の近さ（編集距離）。</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
