// JSONL（1行 = 1試合）の読み込みと検証。形式の定義は README.md を参照。
import { JOBS } from './jobs.js';

export const SCHEMA_VERSION = 1;
export const TEAMS = ['astra', 'umbra'];
const ROLE_GROUPS = ['tank', 'healer', 'dps'];
const RESULTS = ['win', 'lose'];
const PLAYER_NUMBERS = ['k', 'd', 'a', 'dmg', 'taken', 'heal'];

// "6:55" / "1:02:03" / 秒数 → 秒。読めなければ null。
export function parseClock(v) {
  if (typeof v === 'number' && Number.isFinite(v) && v >= 0) return Math.round(v);
  if (typeof v !== 'string') return null;
  const parts = v.trim().split(':');
  if (parts.length < 2 || parts.length > 3 || !parts.every((p) => /^\d+$/.test(p))) return null;
  return parts.reduce((acc, p) => acc * 60 + Number(p), 0);
}

function isCount(v) {
  return Number.isInteger(v) && v >= 0;
}

function fail(msg) {
  throw new Error(msg);
}

// 保存してある生データ（JSON オブジェクト）を、画面で使う形に変換する。
// 形式が壊れていれば例外、答え合わせが合わないだけなら warnings に積む。
export function normalizeMatch(raw) {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) fail('オブジェクトではありません');
  if (raw.v !== SCHEMA_VERSION) fail(`未対応のバージョンです (v=${JSON.stringify(raw.v)})`);
  if (typeof raw.id !== 'string' || raw.id === '') fail('id がありません');

  const time = Date.parse(raw.ts);
  if (typeof raw.ts !== 'string' || Number.isNaN(time)) fail('ts が日時として読めません');

  const teams = {};
  for (const t of TEAMS) {
    const src = raw.teams?.[t];
    if (!src) fail(`teams.${t} がありません`);
    if (!RESULTS.includes(src.result)) fail(`teams.${t}.result は win / lose のどちらかです`);
    for (const key of ['k', 'd', 'a']) {
      if (!isCount(src[key])) fail(`teams.${t}.${key} が数値ではありません`);
    }
    teams[t] = {
      result: src.result,
      progress: typeof src.progress === 'number' ? src.progress : null,
      k: src.k,
      d: src.d,
      a: src.a,
    };
  }
  if (teams.astra.result === teams.umbra.result) fail('両チームの勝敗が同じになっています');

  if (!Array.isArray(raw.players) || raw.players.length === 0) fail('players がありません');
  const players = raw.players.map((p, i) => {
    const where = `players[${i}]`;
    if (!TEAMS.includes(p?.team)) fail(`${where}.team は astra / umbra のどちらかです`);
    for (const key of PLAYER_NUMBERS) {
      if (!isCount(p[key])) fail(`${where}.${key} が数値ではありません`);
    }
    const crystal = p.crystal == null ? null : parseClock(p.crystal);
    if (p.crystal != null && crystal === null) fail(`${where}.crystal が時間として読めません`);
    return {
      team: p.team,
      name: typeof p.name === 'string' ? p.name : '',
      world: typeof p.world === 'string' ? p.world : '',
      tier: typeof p.tier === 'string' ? p.tier : '',
      job: typeof p.job === 'string' && p.job !== '' ? p.job.toUpperCase() : null,
      // ジョブが分からなくても、アイコンの色からロールだけは分かることがある
      role: ROLE_GROUPS.includes(p.role) ? p.role : null,
      k: p.k,
      d: p.d,
      a: p.a,
      dmg: p.dmg,
      taken: p.taken,
      heal: p.heal,
      crystal,
      self: p.self === true,
    };
  });

  const selfIndex = players.findIndex((p) => p.self);
  if (selfIndex === -1) fail('自分の行（"self": true）がありません');
  if (players.filter((p) => p.self).length > 1) fail('"self": true が複数あります');

  const warnings = [];
  for (const t of TEAMS) {
    const members = players.filter((p) => p.team === t);
    for (const key of ['k', 'd', 'a']) {
      const sum = members.reduce((acc, p) => acc + p[key], 0);
      if (sum !== teams[t][key]) {
        warnings.push(`${t} の ${key.toUpperCase()} 合計が合いません（個人の合計 ${sum} / チーム ${teams[t][key]}）`);
      }
    }
  }
  for (const p of players) {
    if (p.job && !JOBS[p.job]) warnings.push(`知らないジョブです: ${p.job}`);
  }

  const self = players[selfIndex];
  return {
    id: raw.id,
    ts: raw.ts,
    time,
    map: typeof raw.map === 'string' && raw.map !== '' ? raw.map : null,
    duration: raw.duration == null ? null : parseClock(raw.duration),
    rank: raw.rank && typeof raw.rank === 'object'
      ? { before: raw.rank.before ?? null, after: raw.rank.after ?? null }
      : null,
    teams,
    players,
    self,
    result: teams[self.team].result,
    warnings,
  };
}

// JSONL テキストを読む。空行は飛ばし、壊れた行は行番号つきで errors に入れる。
// 返す records は { id, raw }（IndexedDB にはこの形で保存する）。
export function parseJsonl(text) {
  const records = [];
  const errors = [];
  const seen = new Set();
  let duplicatesInFile = 0;

  text.split(/\r?\n/).forEach((line, i) => {
    if (line.trim() === '') return;
    try {
      const raw = JSON.parse(line);
      const match = normalizeMatch(raw);
      if (seen.has(match.id)) {
        duplicatesInFile++;
        return;
      }
      seen.add(match.id);
      records.push({ id: match.id, raw });
    } catch (err) {
      errors.push({ line: i + 1, message: err instanceof SyntaxError ? 'JSON として読めません' : err.message });
    }
  });

  return { records, errors, duplicatesInFile };
}
