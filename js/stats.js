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

// ---------- スコア ----------
//
// 1 試合の成績（実力）：与ダメ・デス・キル・アシストが、読み込んだ全データの全プレイヤーのうち
// 同じ役割（DPS・タンク・ヒーラー）の中で、下から何 % の位置か（0〜100、50 が真ん中）。デスは少ないほど高い。
// その人のスコア：実力の平均・一緒のときの勝率・敵のときにこっちが負けた率 を合わせたもの。

export const PERF_WEIGHTS = { dmg: 0.4, d: 0.2, k: 0.2, a: 0.2 };
export const PLAYER_WEIGHTS = { perf: 0.5, ally: 0.25, enemy: 0.25 };
// 少ないほど良い項目
const LOWER_IS_BETTER = new Set(['d']);
// 役割の中の人数がこれより少ないときは、役割で分けずに全員の中の位置にする（参考値）
const MIN_ROLE_ROWS = 20;

export const ROLE_LABELS = { dps: 'DPS', healer: 'ヒーラー', tank: 'タンク', unknown: '全員' };

export function playerKey(p) {
  return `${p.name}@${p.world}`;
}

// 全試合・全プレイヤーの値を、役割ごと（と全員）に並べておく
export function buildDistribution(matches) {
  const groups = { dps: {}, healer: {}, tank: {}, unknown: {} };
  for (const m of matches) {
    for (const p of m.players) {
      const role = roleGroup(p.job, p.role);
      for (const key of Object.keys(PERF_WEIGHTS)) {
        if (p[key] == null) continue;
        (groups.unknown[key] ??= []).push(p[key]);
        if (role) (groups[role][key] ??= []).push(p[key]);
      }
    }
  }
  for (const g of Object.values(groups)) for (const key of Object.keys(g)) g[key].sort((x, y) => x - y);
  return groups;
}

// sorted の中で v が下から何 % の位置か（同じ値は半分ずつ数える）
function percentile(sorted, v) {
  if (!sorted?.length) return null;
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (sorted[mid] < v) lo = mid + 1; else hi = mid; }
  const below = lo;
  hi = sorted.length;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (sorted[mid] <= v) lo = mid + 1; else hi = mid; }
  const equal = lo - below;
  return ((below + equal / 2) / sorted.length) * 100;
}

// その試合の全員の成績（0〜100）と内訳。
// reliable：ジョブが分かっていて、その役割の記録が十分にある（= 同じ役割の中の位置を出せた）
export function scoreMatch(m, dist) {
  return m.players.map((p) => {
    const role = roleGroup(p.job, p.role);
    const reliable = Boolean(role && (dist[role].dmg?.length ?? 0) >= MIN_ROLE_ROWS);
    const group = dist[reliable ? role : 'unknown'];
    const parts = [];
    for (const [key, weight] of Object.entries(PERF_WEIGHTS)) {
      const pct = p[key] == null ? null : percentile(group[key], p[key]);
      if (pct == null) continue;
      parts.push({ key, weight, value: p[key], pct: LOWER_IS_BETTER.has(key) ? 100 - pct : pct });
    }
    const wsum = parts.reduce((acc, x) => acc + x.weight, 0);
    const score = wsum ? parts.reduce((acc, x) => acc + x.weight * x.pct, 0) / wsum : null;
    return { player: p, role: reliable ? role : 'unknown', reliable, score, parts, rows: group.dmg?.length ?? 0 };
  });
}

export function selfScore(m, dist) {
  return scoreMatch(m, dist).find((x) => x.player.self);
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
  return (wins + prior * WINRATE_PRIOR) / (n + WINRATE_PRIOR);
}

// 自分以外のプレイヤーごとの、自分との対戦履歴と評価。
export function buildPlayerHistory(matches, dist) {
  const history = new Map();
  const myWinRate = matches.length ? matches.filter((m) => m.result === 'win').length / matches.length : 0.5;
  for (const m of matches) {
    for (const s of scoreMatch(m, dist)) {
      const p = s.player;
      if (p.self || !p.name) continue;
      const key = playerKey(p);
      if (!history.has(key)) history.set(key, { key, name: p.name, world: p.world, games: [] });
      history.get(key).games.push({
        match: m,
        player: p,
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
    const enemyLosses = enemy.filter((g) => g.match.result === 'lose').length;
    const jobs = new Map();
    for (const g of h.games) {
      const j = g.player.job ?? '';
      jobs.set(j, (jobs.get(j) ?? 0) + 1);
    }
    // 実力：同じ役割の中で比べられた試合の成績の平均（なければ参考として全試合）
    const fair = h.games.filter((g) => g.reliable && g.score != null);
    const perfGames = fair.length ? fair : h.games.filter((g) => g.score != null);
    const perf = mean(perfGames.map((g) => g.score));
    // 一緒のときの勝率・敵のときにこっちが負けた率（補正後、0〜100）
    const allyScore = ally.length ? adjustedRate(allyWins, ally.length, myWinRate) * 100 : null;
    const enemyScore = enemy.length ? adjustedRate(enemyLosses, enemy.length, 1 - myWinRate) * 100 : null;
    // スコア：あるものだけで、重みを付けて平均する
    const parts = [
      [perf, PLAYER_WEIGHTS.perf],
      [allyScore, PLAYER_WEIGHTS.ally],
      [enemyScore, PLAYER_WEIGHTS.enemy],
    ].filter(([v]) => v != null);
    const wsum = parts.reduce((acc, [, w]) => acc + w, 0);
    Object.assign(h, {
      n: h.games.length,
      allyN: ally.length,
      enemyN: enemy.length,
      allyWins,
      enemyLosses,
      myWinRate,
      allyWinRate: ally.length ? allyWins / ally.length : null,
      enemyLossRate: enemy.length ? enemyLosses / enemy.length : null,
      allyScore,
      enemyScore,
      perf,
      perfN: perfGames.length,
      perfFair: fair.length > 0,
      score: wsum ? parts.reduce((acc, [v, w]) => acc + v * w, 0) / wsum : null,
      confidence: confidenceOf(h.games.length),
      jobs: [...jobs.entries()].sort((a, b) => b[1] - a[1]).map(([job, n]) => ({ job: job || null, n })),
      tier: h.games[0].player.tier,
      avg: Object.fromEntries(METRICS.map(({ key }) => [key, mean(h.games.map((g) => g.player[key]))])),
    });
  }
  return history;
}

// ---------- 顔ぶれからの勝率の見込み ----------

// この顔ぶれなら、どれくらい勝てそうだったか。
//
// 味方については「その人と組んだときの勝率」、敵については「その人が敵だったときに勝てた率」を、
// 会った回数で重みを付けて平均する。回数が少ない人は、値を出す時点で普段の勝率に寄せてあるので、
// 情報が無いほど「普段の勝率」に近づく。
//
// 大事なのは、その試合自体を数に入れないこと（勝った試合の味方は勝率が上がるので、
// そのまま使うと「勝った試合は勝てそうだった」と当たり前の答えしか出ない）。
export function expectedWinRate(m, history) {
  let sum = 0;
  let weight = 0;
  let base = 0.5;
  for (const p of m.players) {
    if (p.self) continue;
    const h = history.get(playerKey(p));
    if (!h) continue;
    base = h.myWinRate;
    const ally = p.team === m.self.team;
    // この試合ぶんを引く
    const n = (ally ? h.allyN : h.enemyN) - 1;
    if (n < 1) continue;
    const hits = ally
      ? h.allyWins - (m.result === 'win' ? 1 : 0)
      : h.enemyLosses - (m.result === 'lose' ? 1 : 0);
    const prior = ally ? h.myWinRate : 1 - h.myWinRate;
    const rate = (hits + prior * WINRATE_PRIOR) / (n + WINRATE_PRIOR);
    sum += (ally ? rate : 1 - rate) * n;
    weight += n;
  }
  return { rate: weight ? sum / weight : base, base, known: weight };
}

// 見込みと実際のくいちがい。勝って当たり前だったのか、拾ったのか、取りこぼしたのか。
export function luckOf(expected, result) {
  const won = result === 'win';
  const diff = (won ? 1 : 0) - expected.rate;
  if (expected.known < 3) {
    return { diff, label: '判定できません', note: '顔ぶれの情報がまだ足りません', kind: 'unknown' };
  }
  if (expected.rate >= 0.58) {
    return won
      ? { diff, label: '順当な勝ち', note: '勝ちやすい顔ぶれでした', kind: 'expected' }
      : { diff, label: '取りこぼし', note: '勝ちやすい顔ぶれなのに負けました', kind: 'bad' };
  }
  if (expected.rate <= 0.42) {
    return won
      ? { diff, label: '拾った勝ち', note: '負けやすい顔ぶれでしたが勝ちました', kind: 'good' }
      : { diff, label: '順当な負け', note: '負けやすい顔ぶれでした', kind: 'expected' };
  }
  return won
    ? { diff, label: '五分の勝ち', note: 'どちらに転んでもおかしくない顔ぶれでした', kind: 'even' }
    : { diff, label: '五分の負け', note: 'どちらに転んでもおかしくない顔ぶれでした', kind: 'even' };
}

// 全試合ぶんの「見込みと実際の差」。プラスなら顔ぶれの見込みより勝てている。
export function luckBalance(matches, history) {
  const rows = matches
    .map((m) => ({ m, expected: expectedWinRate(m, history) }))
    .filter((x) => x.expected.known >= 3);
  if (!rows.length) return null;
  const actual = rows.filter((x) => x.m.result === 'win').length / rows.length;
  const predicted = mean(rows.map((x) => x.expected.rate));
  return { n: rows.length, actual, predicted, diff: actual - predicted };
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
