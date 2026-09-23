// 同じ試合の二重取り込みを防ぐ。
// id が違っても（同じリザルト画面を2回撮った、ツールを作り直して id の作り方が変わった など）
// 中身で同じ試合だと分かれば飛ばす。入力は normalizeMatch() 済みの試合。

const NUMBERS = ['k', 'd', 'a', 'dmg', 'taken', 'heal'];
const NEAR_TIME_MS = 30 * 60 * 1000;
const NEAR_MATCH_RATIO = 0.9;

function sortedPlayers(m) {
  return [...m.players].sort((a, b) =>
    a.team.localeCompare(b.team) || a.name.localeCompare(b.name) || a.world.localeCompare(b.world));
}

// 顔ぶれ + 全員の数字。これが同じなら確実に同じ試合
export function contentKey(m) {
  return sortedPlayers(m)
    .map((p) => `${p.team}/${p.name}@${p.world}:${NUMBERS.map((k) => p[k]).join(',')}`)
    .join('|');
}

// 同じ試合かどうかは「大きい数字」だけで見る。
//
// 名前もチームも使わない。同じリザルト画面を 2 回撮っても、名前が 1 文字違って読めたり、
// チームの割り当てが逆になったりすることがあり、それだけで別の試合になってしまうため。
// 与ダメ・被ダメ・与ヒールは 5〜6 桁あるので、10 人ぶんが揃って一致するのは同じ試合のときだけ。
const BIG = ['dmg', 'taken', 'heal'];

function bigKeys(m) {
  return new Set(m.players.map((p) => BIG.map((k) => p[k]).join(',')));
}

// 時刻が近く、大きい数字の 9 割以上が一致（読み取りミスが 1 人ぶんあっても同じとみなす）
function nearlySame(a, b) {
  if (Math.abs(a.time - b.time) > NEAR_TIME_MS) return false;
  const ka = bigKeys(a);
  const kb = bigKeys(b);
  const n = Math.max(ka.size, kb.size);
  if (!n) return false;
  let hit = 0;
  for (const k of ka) if (kb.has(k)) hit++;
  return hit / n >= NEAR_MATCH_RATIO;
}

// 時刻でおおまかに区切って、近い試合だけを比べる（全部と比べると試合数が増えたとき遅い）
const BUCKET_MS = NEAR_TIME_MS;
const bucketOf = (m) => Math.floor(m.time / BUCKET_MS);

// すでにある試合の索引。add() で増やしながら、find() で同じ試合を探す
export class MatchIndex {
  constructor(matches = []) {
    this.ids = new Set();
    this.contents = new Set();
    this.buckets = new Map(); // 時刻の区切り -> matches[]
    for (const m of matches) this.add(m);
  }

  add(m) {
    this.ids.add(m.id);
    this.contents.add(contentKey(m));
    const b = bucketOf(m);
    if (!this.buckets.has(b)) this.buckets.set(b, []);
    this.buckets.get(b).push(m);
  }

  // 同じ試合があれば、その理由を返す（'id' / 'content' / 'near'）。なければ null
  find(m) {
    if (this.ids.has(m.id)) return 'id';
    if (this.contents.has(contentKey(m))) return 'content';
    const b = bucketOf(m);
    for (const near of [b - 1, b, b + 1]) {
      if ((this.buckets.get(near) ?? []).some((other) => nearlySame(m, other))) return 'near';
    }
    return null;
  }
}
