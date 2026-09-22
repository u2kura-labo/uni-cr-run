// 集計。入力はすべて normalizeMatch() 済みの試合。
import { roleGroup } from './jobs.js';

export const METRICS = [
  { key: 'k', label: 'キル', format: 'dec' },
  { key: 'd', label: 'デス', format: 'dec' },
  { key: 'a', label: 'アシスト', format: 'dec' },
  { key: 'dmg', label: '与ダメージ', format: 'big' },
  { key: 'taken', label: '被ダメージ', format: 'big' },
  { key: 'heal', label: '与ヒール', format: 'big' },
  { key: 'crystal', label: '移送時間', format: 'clock' },
];

const DAY = 24 * 60 * 60 * 1000;

// 自分のキャラクター（複数キャラで遊んでいる場合の見分け）
export function selfKey(m) {
  return `${m.self.name}@${m.self.world}`;
}

export function applyFilter(matches, { days, job, char, now = Date.now() }) {
  return matches.filter((m) => {
    if (char && selfKey(m) !== char) return false;
    if (days && m.time < now - days * DAY) return false;
    // job が '-' のときは「ジョブ不明」の試合
    if (job && (m.self.job ?? '-') !== job) return false;
    return true;
  });
}

function mean(values) {
  const xs = values.filter((v) => v != null);
  return xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null;
}

export function summary(matches) {
  const n = matches.length;
  const wins = matches.filter((m) => m.result === 'win').length;
  const avg = {};
  for (const { key } of METRICS) avg[key] = mean(matches.map((m) => m.self[key]));
  return { n, wins, winRate: n ? wins / n : null, avg };
}

// 古い順に並べて、直近 window 試合の勝率を1試合ずつ出す。
export function rollingWinRate(matches, window) {
  const sorted = [...matches].sort((a, b) => a.time - b.time);
  const out = [];
  let wins = 0;
  sorted.forEach((m, i) => {
    if (m.result === 'win') wins++;
    if (i >= window && sorted[i - window].result === 'win') wins--;
    const size = Math.min(i + 1, window);
    out.push({ index: i + 1, match: m, rate: wins / size, size });
  });
  return out;
}

// key(match) ごとにまとめた成績。試合数の多い順。
export function groupBy(matches, key) {
  const groups = new Map();
  for (const m of matches) {
    const k = key(m);
    if (!groups.has(k)) groups.set(k, []);
    groups.get(k).push(m);
  }
  return [...groups.entries()]
    .map(([k, ms]) => ({ key: k, ...summary(ms) }))
    .sort((a, b) => b.n - a.n || String(a.key).localeCompare(String(b.key)));
}

// ---------- ランク ----------

export const TIER_ORDER = ['ブロンズ', 'シルバー', 'ゴールド', 'プラチナ', 'ダイヤモンド', 'クリスタル'];

// "ダイヤモンド5★★★" → "ダイヤモンド"。読めなければ null。
export function tierOf(rankText) {
  if (typeof rankText !== 'string') return null;
  const hit = TIER_ORDER.find((t) => rankText.startsWith(t));
  return hit ?? null;
}

// 試合前のランクの段階ごとの成績。ランクの低い順。
export function byTier(matches) {
  const groups = groupBy(matches, (m) => tierOf(m.rank?.before) ?? '');
  const order = (k) => (k ? TIER_ORDER.indexOf(k) : TIER_ORDER.length);
  return groups.sort((a, b) => order(a.key) - order(b.key));
}

// ---------- パフォーマンス指数（試合の中での比較） ----------
//
// 1 試合の指数は「その試合で、同じ役割の人と比べてどうだったか」。
// 同じ試合の中で比べるので、試合の長さや荒れ具合（全員の与ダメが多い試合など）の影響を受けない。
// 同じ役割（両チーム合わせて）の人の平均を 100 として項目ごとに比べ、役割ごとの重みで平均する。

// 役割ごとの重み。移送時間は強さを表しにくいので使わない。アシスト・キルは少しだけ。
export const ROLE_WEIGHTS = {
  dps: { dmg: 0.5, d: 0.3, k: 0.1, a: 0.1 },
  healer: { heal: 0.35, d: 0.3, dmg: 0.25, a: 0.1 },
  tank: { dmg: 0.3, taken: 0.25, d: 0.25, k: 0.1, a: 0.1 },
  // ジョブが分からない人：役割で分けずに 10 人全員と比べる（参考値）
  unknown: { dmg: 0.45, d: 0.3, heal: 0.05, k: 0.1, a: 0.1 },
};
// 少ないほど良い項目
const LOWER_IS_BETTER = new Set(['d']);
const RATIO_MIN = 0.25;
const RATIO_MAX = 3;

export const ROLE_LABELS = { dps: 'DPS', healer: 'ヒーラー', tank: 'タンク', unknown: '全員' };

export function playerKey(p) {
  return `${p.name}@${p.world}`;
}

// スコア：指数を 0〜100 の点数にしたもの。50 = 同じ役割の平均くらい、100 = めっちゃ強い、0 = 弱い。
// 指数 60 以下（平均の 0.6 倍）を 0、140 以上（平均の 1.4 倍）を 100 として、その間をまっすぐつなぐ。
// 基準を固定しているので、試合をまたいで比べたり平均したりできる。
export const SCORE_SCALE = { floor: 60, ceil: 140 };

export function toScore(index) {
  if (index == null) return null;
  const { floor, ceil } = SCORE_SCALE;
  return Math.max(0, Math.min(100, ((index - floor) / (ceil - floor)) * 100));
}

// その試合の全員の指数・スコアと内訳。
// reliable：ジョブが分かっていて、同じ試合に同じ役割の人が自分のほかにもいる（= 公平に比べられた）
export function scoreMatch(m) {
  const roleOf = (p) => roleGroup(p.job) ?? 'unknown';
  const scored = m.players.map((p) => {
    const role = roleOf(p);
    const sameRole = role === 'unknown' ? [] : m.players.filter((q) => roleOf(q) === role);
    const reliable = sameRole.length >= 2;
    const peers = reliable ? sameRole : m.players;
    const weights = ROLE_WEIGHTS[reliable ? role : 'unknown'];
    const parts = [];
    for (const [key, w] of Object.entries(weights)) {
      const v = p[key];
      const b = mean(peers.map((q) => q[key]));
      if (v == null || b == null) continue;
      // 0 がありうる項目のために +1 してから比べる。ratio は「良さ」（1 より大きいほど良い）
      const r = LOWER_IS_BETTER.has(key) ? (b + 1) / (v + 1) : (v + 1) / (b + 1);
      parts.push({ key, weight: w, value: v, base: b, ratio: Math.min(RATIO_MAX, Math.max(RATIO_MIN, r)) });
    }
    const wsum = parts.reduce((acc, x) => acc + x.weight, 0);
    const index = wsum ? (parts.reduce((acc, x) => acc + x.weight * x.ratio, 0) / wsum) * 100 : null;
    return { player: p, role: reliable ? role : 'unknown', peers: peers.length, reliable, index, score: toScore(index), parts };
  });

  return scored;
}

export function selfScore(m) {
  return scoreMatch(m).find((x) => x.player.self);
}

// ---------- プレイヤーの記録（過去の試合の積み重ね） ----------

// 一緒のときの勝率は、試合数が少ないうちは偶然が大きいので、普段の勝率に寄せて補正する。
// 補正後 =（勝ち + 普段の勝率 × PRIOR）÷（試合数 + PRIOR）。試合数が増えるほど実際の値に近づく。
export const WINRATE_PRIOR = 4;

// 会った回数による信頼度
export function confidenceOf(n) {
  if (n >= 10) return { level: 3, label: '信頼度 高' };
  if (n >= 4) return { level: 2, label: '信頼度 中' };
  return { level: 1, label: '参考' };
}

function adjustedRate(wins, n, prior) {
  return n + WINRATE_PRIOR > 0 ? (wins + prior * WINRATE_PRIOR) / (n + WINRATE_PRIOR) : null;
}

// 自分以外のプレイヤーごとの、自分との対戦履歴と評価。
export function buildPlayerHistory(matches) {
  const history = new Map();
  const myWinRate = matches.length ? matches.filter((m) => m.result === 'win').length / matches.length : 0.5;
  for (const m of matches) {
    for (const s of scoreMatch(m)) {
      const p = s.player;
      if (p.self || !p.name) continue;
      const key = playerKey(p);
      if (!history.has(key)) history.set(key, { key, name: p.name, world: p.world, games: [] });
      history.get(key).games.push({
        match: m,
        player: p,
        index: s.index,
        score: s.score,
        reliable: s.reliable,
        detail: s,
        relation: p.team === m.self.team ? 'ally' : 'enemy',
      });
    }
  }
  for (const h of history.values()) {
    h.games.sort((a, b) => b.match.time - a.match.time);
    const ally = h.games.filter((g) => g.relation === 'ally');
    const enemy = h.games.filter((g) => g.relation === 'enemy');
    const allyWins = ally.filter((g) => g.match.result === 'win').length;
    const enemyWins = enemy.filter((g) => g.match.result === 'win').length;
    const jobs = new Map();
    for (const g of h.games) {
      const j = g.player.job ?? '';
      jobs.set(j, (jobs.get(j) ?? 0) + 1);
    }
    // 評価に使うのは、同じ役割の人と公平に比べられた試合だけ（なければ参考として全試合）
    const fair = h.games.filter((g) => g.reliable && g.index != null);
    const indexGames = fair.length ? fair : h.games.filter((g) => g.index != null);
    Object.assign(h, {
      n: h.games.length,
      allyN: ally.length,
      enemyN: enemy.length,
      myWinRate,
      // 実際の勝率（味方のとき：一緒に勝った割合 / 敵のとき：自分が勝った割合）
      allyWinRate: ally.length ? allyWins / ally.length : null,
      enemyWinRate: enemy.length ? enemyWins / enemy.length : null,
      // 補正した勝率と、普段との差（並べ替え・評価にはこちらを使う）
      allyAdjusted: ally.length ? adjustedRate(allyWins, ally.length, myWinRate) : null,
      enemyAdjusted: enemy.length ? adjustedRate(enemyWins, enemy.length, myWinRate) : null,
      avgScore: mean(indexGames.map((g) => g.score)),
      indexN: indexGames.length,
      indexFair: fair.length > 0,
      confidence: confidenceOf(indexGames.length),
      jobs: [...jobs.entries()].sort((a, b) => b[1] - a[1]).map(([job, n]) => ({ job: job || null, n })),
      tier: h.games[0].player.tier,
      avg: Object.fromEntries(METRICS.map(({ key }) => [key, mean(h.games.map((g) => g.player[key]))])),
    });
    h.allyLift = h.allyAdjusted != null ? h.allyAdjusted - myWinRate : null;
    h.enemyLift = h.enemyAdjusted != null ? h.enemyAdjusted - myWinRate : null;
  }
  return history;
}

// 自分と「同じジョブの他のプレイヤー」の比較。
// 同ジョブ平均は、自分がそのジョブで出た試合ごとに、そのジョブの他人の平均を当てはめて平均したもの。
// こうすると、複数のジョブを混ぜて見ても、ジョブの構成の違いで差が出ない。
export function peerComparison(matches) {
  const peers = new Map(); // job -> metric -> values[]
  for (const m of matches) {
    for (const p of m.players) {
      if (p.self || !p.job) continue;
      if (!peers.has(p.job)) peers.set(p.job, {});
      const byMetric = peers.get(p.job);
      for (const { key } of METRICS) {
        if (p[key] == null) continue;
        (byMetric[key] ??= []).push(p[key]);
      }
    }
  }
  const peerMean = new Map();
  for (const [job, byMetric] of peers) {
    const means = {};
    for (const { key } of METRICS) means[key] = mean(byMetric[key] ?? []);
    peerMean.set(job, { means, count: byMetric.k?.length ?? 0 });
  }

  let usedMatches = 0;
  let peerSamples = 0;
  const rows = METRICS.map((metric) => {
    const selfVals = [];
    const peerVals = [];
    for (const m of matches) {
      const pm = m.self.job && peerMean.get(m.self.job);
      if (!pm || pm.means[metric.key] == null || m.self[metric.key] == null) continue;
      selfVals.push(m.self[metric.key]);
      peerVals.push(pm.means[metric.key]);
    }
    const selfAvg = mean(selfVals);
    const peerAvg = mean(peerVals);
    return {
      ...metric,
      selfAvg,
      peerAvg,
      diff: selfAvg != null && peerAvg ? selfAvg / peerAvg - 1 : null,
      n: selfVals.length,
    };
  });

  for (const m of matches) {
    const pm = m.self.job && peerMean.get(m.self.job);
    if (pm) {
      usedMatches++;
      peerSamples += pm.count;
    }
  }
  return { rows, usedMatches, peerSamples };
}
