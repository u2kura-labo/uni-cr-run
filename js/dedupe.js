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

// 10 人の顔ぶれ（チーム・名前・ワールド）
function rosterKey(m) {
  return sortedPlayers(m).map((p) => `${p.team}/${p.name}@${p.world}`).join('|');
}

// 顔ぶれ + 全員の数字。これが同じなら確実に同じ試合
export function contentKey(m) {
  return sortedPlayers(m)
    .map((p) => `${p.team}/${p.name}@${p.world}:${NUMBERS.map((k) => p[k]).join(',')}`)
    .join('|');
}

// 顔ぶれが同じで、時刻が近く、数字の 9 割以上が一致（読み取りミスが1〜2か所あっても同じとみなす）
function nearlySame(a, b) {
  if (Math.abs(a.time - b.time) > NEAR_TIME_MS) return false;
  const pa = sortedPlayers(a);
  const pb = sortedPlayers(b);
  if (pa.length !== pb.length) return false;
  let same = 0;
  let total = 0;
  pa.forEach((p, i) => {
    for (const k of NUMBERS) {
      total++;
      if (p[k] === pb[i][k]) same++;
    }
  });
  return total > 0 && same / total >= NEAR_MATCH_RATIO;
}

// すでにある試合の索引。add() で増やしながら、find() で同じ試合を探す
export class MatchIndex {
  constructor(matches = []) {
    this.ids = new Set();
    this.contents = new Set();
    this.rosters = new Map(); // rosterKey -> matches[]
    for (const m of matches) this.add(m);
  }

  add(m) {
    this.ids.add(m.id);
    this.contents.add(contentKey(m));
    const r = rosterKey(m);
    if (!this.rosters.has(r)) this.rosters.set(r, []);
    this.rosters.get(r).push(m);
  }

  // 同じ試合があれば、その理由を返す（'id' / 'content' / 'near'）。なければ null
  find(m) {
    if (this.ids.has(m.id)) return 'id';
    if (this.contents.has(contentKey(m))) return 'content';
    const sameRoster = this.rosters.get(rosterKey(m)) ?? [];
    if (sameRoster.some((other) => nearlySame(m, other))) return 'near';
    return null;
  }
}
