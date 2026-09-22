// 他のプレイヤーの名前の暗号化（キャプチャツールの NameCipher と同じ形式）を、自分の鍵で元に戻す。
// 形式："enc:" + Base64URL(nonce 12 バイト ‖ 暗号文 ‖ タグ 16 バイト)、AES-256-GCM。平文は "名前@ワールド"。
// 鍵はこのブラウザの中だけに保存する（どこにも送らない）。
const KEY_PREFIX = 'CRK1:';
export const ENC_PREFIX = 'enc:';

function fromBase64Url(s) {
  const t = s.replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(t + '='.repeat((4 - (t.length % 4)) % 4));
  return Uint8Array.from(bin, (c) => c.charCodeAt(0));
}

export function isEncrypted(name) {
  return typeof name === 'string' && name.startsWith(ENC_PREFIX);
}

// 鍵の文字列（"CRK1:..."）を読む。{ key, id, text } を返す。形式が違えば例外
export async function importKey(text) {
  const t = text.trim();
  if (!t.startsWith(KEY_PREFIX)) throw new Error('鍵の形式が違います（CRK1: で始まる文字列です）。');
  const raw = fromBase64Url(t.slice(KEY_PREFIX.length));
  if (raw.length !== 32) throw new Error('鍵の長さが違います。');
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', raw));
  const hex = [...hash.slice(0, 4)].map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
  const key = await crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['decrypt']);
  return { key, id: `${hex.slice(0, 4)}-${hex.slice(4, 8)}`, text: t };
}

// 暗号化された名前を元に戻す。{ name, world } を返す。鍵が違えば例外
export async function decryptName(key, token) {
  const all = fromBase64Url(token.slice(ENC_PREFIX.length));
  const iv = all.slice(0, 12);
  const data = all.slice(12);
  const plain = new TextDecoder().decode(await crypto.subtle.decrypt({ name: 'AES-GCM', iv, tagLength: 128 }, key, data));
  const at = plain.lastIndexOf('@');
  return at < 0 ? { name: plain, world: '' } : { name: plain.slice(0, at), world: plain.slice(at + 1) };
}

// 鍵がないときの呼び名。同じ人はいつも同じ暗号文なので、同じ呼び名になる
export function pseudonym(token) {
  return `プレイヤー #${token.slice(ENC_PREFIX.length, ENC_PREFIX.length + 5).toUpperCase()}`;
}
