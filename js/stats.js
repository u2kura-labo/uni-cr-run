// 集計。入力はすべて normalizeMatch() 済みの試合。

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

export function applyFilter(matches, { days, job, now = Date.now() }) {
  return matches.filter((m) => {
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

// ---------- パフォーマンス指数とプレイヤー履歴 ----------

// 指数に使う項目。dir: 1 = 多いほど良い、-1 = 少ないほど良い。
// 被ダメはタンクだと多くて当然なので使わない。
export const INDEX_METRICS = [
  { key: 'k', dir: 1 },
  { key: 'd', dir: -1 },
  { key: 'a', dir: 1 },
  { key: 'dmg', dir: 1 },
  { key: 'heal', dir: 1 },
  { key: 'crystal', dir: 1 },
];
const RATIO_MIN = 0.25;
const RATIO_MAX = 3;

export function playerKey(p) {
  return `${p.name}@${p.world}`;
}

// 全試合・全プレイヤーから、ジョブごとの平均（と、ジョブ不明用の全体平均）を出す。
export function buildBaselines(matches) {
  const byJob = new Map();
  const all = {};
  for (const m of matches) {
    for (const p of m.players) {
      const job = p.job ?? null;
      if (job && !byJob.has(job)) byJob.set(job, {});
      for (const { key } of INDEX_METRICS) {
        if (p[key] == null) continue;
        (all[key] ??= []).push(p[key]);
        if (job) (byJob.get(job)[key] ??= []).push(p[key]);
      }
    }
  }
  const toMeans = (lists) => Object.fromEntries(INDEX_METRICS.map(({ key }) => [key, mean(lists[key] ?? [])]));
  const jobs = new Map([...byJob].map(([job, lists]) => [job, { means: toMeans(lists), n: lists.k?.length ?? 0 }]));
  return { jobs, all: toMeans(all) };
}

// 同じジョブの平均を 100 としたときの成績。サンプルが少ないジョブ（5人未満）は全体平均と比べる。
export function performanceIndex(p, baselines) {
  const job = p.job && baselines.jobs.get(p.job);
  const base = job && job.n >= 5 ? job.means : baselines.all;
  const ratios = [];
  for (const { key, dir } of INDEX_METRICS) {
    const v = p[key];
    const b = base[key];
    if (v == null || b == null) continue;
    // D と移送時間は 0 がありうるので、+1 してから比べる
    const r = dir > 0 ? (v + 1) / (b + 1) : (b + 1) / (v + 1);
    ratios.push(Math.min(RATIO_MAX, Math.max(RATIO_MIN, r)));
  }
  return ratios.length ? (ratios.reduce((a, c) => a + c, 0) / ratios.length) * 100 : null;
}

// 試合ごとの指数と、その試合のチーム内最下位・試合の最高を付ける。
export function scoreMatch(m, baselines) {
  const scored = m.players.map((p) => ({ player: p, index: performanceIndex(p, baselines) }));
  const valid = scored.filter((s) => s.index != null);
  const mvp = valid.reduce((best, s) => (!best || s.index > best.index ? s : best), null);
  const worst = {};
  for (const team of ['astra', 'umbra']) {
    worst[team] = valid.filter((s) => s.player.team === team)
      .reduce((low, s) => (!low || s.index < low.index ? s : low), null);
  }
  return scored.map((s) => ({
    ...s,
    mvp: s === mvp,
    worstInTeam: s === worst[s.player.team],
  }));
}

// 自分以外のプレイヤーごとの、自分との対戦履歴。
export function buildPlayerHistory(matches, baselines) {
  const history = new Map();
  const myWinRate = matches.length ? matches.filter((m) => m.result === 'win').length / matches.length : null;
  for (const m of matches) {
    const scored = scoreMatch(m, baselines);
    for (const s of scored) {
      const p = s.player;
      if (p.self || !p.name) continue;
      const key = playerKey(p);
      if (!history.has(key)) history.set(key, { key, name: p.name, world: p.world, games: [] });
      history.get(key).games.push({
        match: m,
        player: p,
        index: s.index,
        mvp: s.mvp,
        worstInTeam: s.worstInTeam,
        relation: p.team === m.self.team ? 'ally' : 'enemy',
      });
    }
  }
  for (const h of history.values()) {
    h.games.sort((a, b) => b.match.time - a.match.time);
    const ally = h.games.filter((g) => g.relation === 'ally');
    const enemy = h.games.filter((g) => g.relation === 'enemy');
    const jobs = new Map();
    for (const g of h.games) {
      const j = g.player.job ?? '';
      jobs.set(j, (jobs.get(j) ?? 0) + 1);
    }
    Object.assign(h, {
      n: h.games.length,
      allyN: ally.length,
      enemyN: enemy.length,
      // 味方のとき：一緒に勝った割合 / 敵のとき：自分が勝った割合
      allyWinRate: ally.length ? ally.filter((g) => g.match.result === 'win').length / ally.length : null,
      enemyWinRate: enemy.length ? enemy.filter((g) => g.match.result === 'win').length / enemy.length : null,
      myWinRate,
      avgIndex: mean(h.games.map((g) => g.index)),
      worstCount: h.games.filter((g) => g.worstInTeam).length,
      mvpCount: h.games.filter((g) => g.mvp).length,
      jobs: [...jobs.entries()].sort((a, b) => b[1] - a[1]).map(([job, n]) => ({ job: job || null, n })),
      tier: h.games[0].player.tier,
      avg: Object.fromEntries(METRICS.map(({ key }) => [key, mean(h.games.map((g) => g.player[key]))])),
    });
    // 自分の普段の勝率との差（ポイント）。味方のときは一緒に勝ちやすいか、敵のときは勝ちにくい相手か
    h.allyLift = h.allyWinRate != null && myWinRate != null ? h.allyWinRate - myWinRate : null;
    h.enemyLift = h.enemyWinRate != null && myWinRate != null ? h.enemyWinRate - myWinRate : null;
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
