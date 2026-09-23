namespace UniCrCapture.Core;

/// <summary>
/// ジョブのアイコンの絵柄を見分ける。
///
/// リザルト画面のアイコンは、いつも同じ絵が同じ大きさで描かれるので、
/// 16×16 の明るさの並びにして比べるだけで足りる。
/// 実際のスクリーンショットで測ったところ、同じジョブどうしは 1.00、
/// 違うジョブは最大 0.28 で、はっきり分かれた。
///
/// どのアイコンがどのジョブかは分からないので、
/// 「自分の行（設定でジョブが分かっている）」から覚えるか、
/// 画像を読み込んで教えてもらう。
/// </summary>
public static class JobIcons
{
    /// <summary>並びの細かさ。16×16 = 256 個。</summary>
    public const int Size = 16;

    /// <summary>これより似ていれば同じジョブとみなす。1.00 と 0.28 の間なので、真ん中より上に置く。</summary>
    public const double SameJob = 0.7;

    /// <summary>
    /// アイコンの見た目を、明るさの並びにする。
    /// 明るさの平均と散らばりをそろえるので、画面の明るさが多少違っても比べられる。
    /// 読めない（のっぺりしている）ときは null。
    /// </summary>
    public static double[]? Signature(IPixelSource? pixels, PixelRect area)
    {
        if (pixels is null || area.Width < Size || area.Height < Size) return null;
        var values = new double[Size * Size];
        for (var gy = 0; gy < Size; gy++)
        for (var gx = 0; gx < Size; gx++)
        {
            var x = (int)(area.X + (gx + 0.5) * area.Width / Size);
            var y = (int)(area.Y + (gy + 0.5) * area.Height / Size);
            if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) return null;
            var (r, g, b) = pixels.GetPixel(x, y);
            values[gy * Size + gx] = 0.299 * r + 0.587 * g + 0.114 * b;
        }

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Length;
        var sd = Math.Sqrt(variance);
        if (sd < 3) return null; // 一様な色（アイコンが無い）
        for (var i = 0; i < values.Length; i++) values[i] = (values[i] - mean) / sd;
        return values;
    }

    /// <summary>2つの並びの似ている度合い（1.00 が同じ、0 前後が無関係）。</summary>
    public static double Similarity(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count || a.Count == 0) return 0;
        double sum = 0;
        for (var i = 0; i < a.Count; i++) sum += a[i] * b[i];
        return sum / a.Count;
    }

    /// <summary>覚えているアイコンの中から、いちばん近いジョブを返す（届かなければ null）。</summary>
    public static string? Match(IReadOnlyList<double> signature, IReadOnlyDictionary<string, double[]> known,
        double threshold = SameJob)
    {
        string? best = null;
        var bestScore = threshold;
        foreach (var (job, other) in known)
        {
            var score = Similarity(signature, other);
            if (score <= bestScore) continue;
            bestScore = score;
            best = job;
        }
        return best;
    }
}
