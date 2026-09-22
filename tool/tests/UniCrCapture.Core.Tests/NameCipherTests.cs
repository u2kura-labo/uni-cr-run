using System.Text;
using UniCrCapture.Core;
using Xunit;

namespace UniCrCapture.Core.Tests;

public class NameCipherTests
{
    // ビューア（js/crypto.js）のテストと同じ鍵・同じ結果になることを確かめるための固定の鍵
    private const string FixedKey = "CRK1:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string Vector = "enc:pewLDq2qxRwZyW5qAX5TyDeOe3KvETkwfnSga5wSl7XSBUVQji5m7f4_QGvZPIDT";

    [Fact]
    public void EncryptsAndDecryptsNames()
    {
        var c = NameCipher.Create();
        var token = c.Encrypt("Ne'lu Starwind@Ridill");
        Assert.StartsWith("enc:", token);
        Assert.DoesNotContain("Starwind", token);
        Assert.Equal("Ne'lu Starwind@Ridill", c.Decrypt(token));
    }

    [Fact]
    public void SameNameGivesSameTokenSoPlayersCanStillBeGrouped()
    {
        var c = NameCipher.Parse(FixedKey);
        Assert.Equal(c.Encrypt("Maple Custard@Garuda"), c.Encrypt("Maple Custard@Garuda"));
        Assert.NotEqual(c.Encrypt("Maple Custard@Garuda"), c.Encrypt("Maple Custard@Ridill"));
        // 別の鍵なら別の文字列（名前から逆引きできない）
        Assert.NotEqual(c.Encrypt("Maple Custard@Garuda"), NameCipher.Create().Encrypt("Maple Custard@Garuda"));
    }

    [Fact]
    public void WrongKeyCannotDecrypt()
    {
        var token = NameCipher.Create().Encrypt("Maple Custard@Garuda");
        Assert.ThrowsAny<Exception>(() => NameCipher.Create().Decrypt(token));
    }

    [Fact]
    public void KeyRoundTripsThroughItsText()
    {
        var c = NameCipher.Create();
        var again = NameCipher.Parse(c.Export());
        Assert.Equal(c.KeyId, again.KeyId);
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}$", c.KeyId);
        Assert.Throws<FormatException>(() => NameCipher.Parse("hello"));
    }

    [Fact]
    public void ProtectHidesOthersButKeepsSelfAndAddsOwnerMark()
    {
        var screen = FakeScreen.Build();
        var match = ResultParser.Parse(screen.Words, screen, new ParseOptions { SelfName = "Kinako Mochi" }).Match!;
        var id = match.Id;
        var c = NameCipher.Parse(FixedKey);
        c.Protect(match);

        Assert.Equal(id, match.Id); // id は平文のときのまま
        var self = match.Players.Single(p => p.Self);
        Assert.Equal("Kinako Mochi", self.Name);
        Assert.Equal("Tiamat", self.World);
        foreach (var p in match.Players.Where(p => !p.Self))
        {
            Assert.StartsWith("enc:", p.Name);
            Assert.Equal("", p.World);
        }
        Assert.Equal("Maple Custard@Garuda", c.Decrypt(match.Players[0].Name));

        var owner = Encoding.UTF8.GetString(Convert.FromBase64String(match.Owner!));
        Assert.Equal($"owner=Kinako Mochi@Tiamat;key={c.KeyId}", owner);
        Assert.DoesNotContain("Maple", JsonlStore.Serialize(match));
    }

    [Fact]
    public void FixedVectorForTheViewer()
    {
        // js/crypto.js の確認に使う値（この値が変わったら、ビューア側も合わせて確かめる）
        var c = NameCipher.Parse(FixedKey);
        Assert.Equal("630D-CD29", c.KeyId);
        Assert.Equal(Vector, c.Encrypt("Maple Custard@Garuda"));
        Assert.Equal("Maple Custard@Garuda", c.Decrypt(c.Encrypt("Maple Custard@Garuda")));
    }
}
