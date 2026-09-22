using System.Security.Cryptography;
using System.Text;

namespace UniCrCapture.Core;

/// <summary>
/// 他のプレイヤーの名前を、自分の鍵で暗号化する。JSONL が人の手に渡っても、名前が読めないように。
///
/// - 形式："enc:" + Base64URL(nonce 12 バイト ‖ 暗号文 ‖ タグ 16 バイト)、AES-256-GCM
/// - nonce は HMAC-SHA256(鍵, 平文) の先頭 12 バイト。同じ人はいつも同じ暗号文になるので、
///   鍵がなくても「同じ人か」は見分けられ、重複の判定も今までどおり効く（名前そのものは分からない）
/// - ビューア（js/crypto.js）は WebCrypto で同じ形式を復号する
/// </summary>
public sealed class NameCipher
{
    public const string Prefix = "enc:";
    private const string KeyPrefix = "CRK1:";
    private readonly byte[] _key;

    private NameCipher(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("鍵は 32 バイトです。", nameof(key));
        _key = key;
    }

    public static NameCipher Create() => new(RandomNumberGenerator.GetBytes(32));

    /// <summary>鍵の文字列（"CRK1:..."）から作る。鍵のファイルの中身もこの形。</summary>
    public static NameCipher Parse(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith(KeyPrefix, StringComparison.Ordinal)) throw new FormatException("鍵の形式が違います（CRK1: で始まる文字列です）。");
        return new NameCipher(FromBase64Url(t[KeyPrefix.Length..]));
    }

    public string Export() => KeyPrefix + ToBase64Url(_key);

    /// <summary>鍵の番号（例：3F2A-91C0）。鍵の中身は分からないが、どの鍵かは見分けられる。</summary>
    public string KeyId
    {
        get
        {
            var h = Convert.ToHexString(SHA256.HashData(_key));
            return $"{h[..4]}-{h[4..8]}";
        }
    }

    public string Encrypt(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = HMACSHA256.HashData(_key, data)[..12];
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + ToBase64Url([.. nonce, .. cipher, .. tag]);
    }

    public string Decrypt(string token)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return token;
        var all = FromBase64Url(token[Prefix.Length..]);
        var nonce = all[..12];
        var tag = all[^16..];
        var cipher = all[12..^16];
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(_key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// 自分以外のプレイヤーの名前とワールドを暗号化し、作成者の識別子（o）を付ける。
    /// 名前とワールドは "名前@ワールド" として 1 つにまとめて name に入れ、world は空にする。
    /// id は暗号化の前（平文）で作ったものをそのまま使う。
    /// </summary>
    public void Protect(MatchRecord match)
    {
        var self = match.Players.FirstOrDefault(p => p.Self);
        foreach (var p in match.Players)
        {
            if (p.Self || p.Name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            p.Name = Encrypt($"{p.Name}@{p.World}");
            p.World = "";
        }
        match.Owner = OwnerMark(self is null ? "" : $"{self.Name}@{self.World}");
    }

    /// <summary>作成者の識別子。Base64 にしただけ（隠す目的ではなく、詳しい人が見れば分かる印）。</summary>
    public string OwnerMark(string owner) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner={owner};key={KeyId}"));

    private static string ToBase64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }
}
