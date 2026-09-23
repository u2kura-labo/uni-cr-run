using UniCrCapture.Core;
using Xunit;

namespace UniCrCapture.Core.Tests;

public class JobIconTests
{
    /// <summary>アイコンを並べた作り物の画像。同じ番号なら同じ絵柄になる。</summary>
    private sealed class Icons : IPixelSource
    {
        private readonly int[] _kinds;
        public Icons(params int[] kinds) => _kinds = kinds;

        public int Width => 32;
        public int Height => 32 * _kinds.Length;

        public (byte R, byte G, byte B) GetPixel(int x, int y)
        {
            var row = y / 32;
            if (row >= _kinds.Length) return (0, 0, 0);
            // 絵柄ごとに違う縞模様をつくる（番号が違えば形も違う）
            var kind = _kinds[row];
            var v = ((x * (kind + 1) + (y % 32) * (kind + 2)) % 7) < 3 ? (byte)230 : (byte)30;
            return (v, v, v);
        }
    }

    private static PixelRect Row(int i) => new(0, i * 32, 32, 32);

    [Fact]
    public void TellsTheSameIconFromADifferentOne()
    {
        var screen = new Icons(1, 2, 1); // 1 番目と 3 番目が同じ絵柄
        var a = JobIcons.Signature(screen, Row(0))!;
        var b = JobIcons.Signature(screen, Row(1))!;
        var c = JobIcons.Signature(screen, Row(2))!;

        Assert.NotNull(a);
        Assert.Equal(1.0, JobIcons.Similarity(a, c), 3);          // 同じ絵柄
        Assert.True(JobIcons.Similarity(a, b) < JobIcons.SameJob, // 違う絵柄
            $"違う絵柄なのに似すぎている（{JobIcons.Similarity(a, b):F2}）");
    }

    [Fact]
    public void FindsTheJobOnceItsIconIsKnown()
    {
        var screen = new Icons(1, 2, 1);
        var known = new Dictionary<string, double[]> { ["PLD"] = JobIcons.Signature(screen, Row(0))! };

        // 1 番目を PLD として覚えたので、同じ絵柄の 3 番目も PLD と分かる
        Assert.Equal("PLD", JobIcons.Match(JobIcons.Signature(screen, Row(2))!, known));
        // 違う絵柄は、覚えていないので分からない（でたらめに答えない）
        Assert.Null(JobIcons.Match(JobIcons.Signature(screen, Row(1))!, known));
    }

    [Fact]
    public void GivesUpOnAFlatArea()
    {
        var flat = new FlatScreen();
        Assert.Null(JobIcons.Signature(flat, new PixelRect(0, 0, 32, 32)));
    }

    private sealed class FlatScreen : IPixelSource
    {
        public int Width => 32;
        public int Height => 32;
        public (byte R, byte G, byte B) GetPixel(int x, int y) => (80, 48, 47);
    }
}
