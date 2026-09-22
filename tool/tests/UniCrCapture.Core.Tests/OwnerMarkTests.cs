using UniCrCapture.Core;
using Xunit;

namespace UniCrCapture.Core.Tests;

public class OwnerMarkTests
{
    [Fact]
    public void EncodesTheSelfCharacterAsBase64()
    {
        var screen = FakeScreen.Build();
        var match = ResultParser.Parse(screen.Words, screen, new ParseOptions { SelfName = "Kinako Mochi" }).Match!;
        match.Owner = OwnerMark.For(match);

        Assert.Equal("b3duZXI9S2luYWtvIE1vY2hpQFRpYW1hdA==", match.Owner); // "owner=Kinako Mochi@Tiamat"
        Assert.Equal("Kinako Mochi@Tiamat", OwnerMark.Read(match.Owner));
        Assert.DoesNotContain("Kinako", match.Owner);
        Assert.Contains("\"o\":\"b3duZXI9", JsonlStore.Serialize(match));
    }
}
