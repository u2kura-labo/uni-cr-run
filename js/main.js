import { parseJsonl, normalizeMatch, TEAMS } from './schema.js';
import { loadAll, addRecords, clearAll } from './store.js';
import { MatchIndex } from './dedupe.js';
import {
  applyFilter, summary, groupBy, peerComparison,
  buildPlayerHistory, buildDistribution, scoreMatch, selfScore, playerKey, selfKey, byTier,
  PERF_WEIGHTS, PLAYER_WEIGHTS, ROLE_LABELS, WINRATE_PRIOR,
} from './stats.js';
import { lineChart, barChart, divergingChart, showTooltip, hideTooltip } from './charts.js';
import { jobName, roleGroup } from './jobs.js';
import { h } from './dom.js';
import * as f from './format.js';

const PAGE_SIZE = 30;
const TEAM_NAMES = { astra: 'アストラ', umbra: 'アンブラ' };
const RESULT_NAMES = { win: '勝ち', lose: '負け' };

const state = {
  matches: [],
  broken: 0,
  filter: { days: 0, job: '', char: '' },
  listLimit: PAGE_SIZE,
  openMatch: null,
  history: new Map(),
  playerQuery: '',
  playerSort: 'n',
  tab: 'overview',
};

const TABS = ['overview', 'analysis', 'matches', 'players'];

const $ = (sel) => document.querySelector(sel);

// ---------- 読み込み ----------

async function reload() {
  const stored = await loadAll();
  // 先に取り込んだものを残す（以前の版で二重に入ったものがあっても、表示は1つにする）
  stored.sort((a, b) => (a.importedAt ?? 0) - (b.importedAt ?? 0));
  const matches = [];
  const index = new MatchIndex();
  let broken = 0;
  for (const rec of stored) {
    let m;
    try {
      m = normalizeMatch(rec.raw);
    } catch {
      broken++;
      continue;
    }
    if (index.find(m)) continue;
    index.add(m);
    matches.push(m);
  }
  matches.sort((a, b) => b.time - a.time);
  state.matches = matches;
  state.broken = broken;
  state.historyCache = new Map();
  // 成績の位置は、読み込んだ全データの全プレイヤーの中で出す
  state.dist = buildDistribution(matches);
  pickDefaultChar();
  render();
}

async function importText(text, sourceName) {
  const { records, errors, duplicatesInFile } = parseJsonl(text);
  // 取り込み済みの試合、同じファイル内の重複を、id と中身の両方で飛ばす
  const index = new MatchIndex(state.matches);
  const fresh = [];
  const skipped = { id: duplicatesInFile, content: 0, near: 0 };
  for (const rec of records) {
    const m = normalizeMatch(rec.raw);
    const reason = index.find(m);
    if (reason) {
      skipped[reason]++;
      continue;
    }
    index.add(m);
    fresh.push(rec);
  }
  const { added, duplicates } = await addRecords(fresh);
  skipped.id += duplicates;

  const parts = [`${sourceName}：${added} 試合を追加しました`];
  if (skipped.id) parts.push(`${skipped.id} 試合は取り込み済みでした`);
  if (skipped.content) parts.push(`${skipped.content} 試合は中身が同じ試合がすでにあったので飛ばしました`);
  if (skipped.near) parts.push(`${skipped.near} 試合はほぼ同じ試合（同じ顔ぶれ・近い時刻・数字の 9 割以上が一致）がすでにあったので飛ばしました`);
  if (errors.length) parts.push(`${errors.length} 行は読めませんでした`);
  showNotice(parts.join('。') + '。', errors);
  await reload();
}

async function importFiles(files) {
  for (const file of files) {
    try {
      await importText(await file.text(), file.name);
    } catch (err) {
      showNotice(`${file.name} を読み込めませんでした：${err.message}`);
    }
  }
}

// このブラウザの全試合を 1 つの JSONL にして保存する（古い順）。別の PC・ブラウザでそのまま読み込める
async function exportAll() {
  const stored = await loadAll();
  const lines = stored
    .map((rec) => rec.raw)
    .sort((a, b) => Date.parse(a.ts) - Date.parse(b.ts))
    .map((raw) => JSON.stringify(raw));
  const blob = new Blob([lines.join('\n') + '\n'], { type: 'application/x-ndjson' });
  const url = URL.createObjectURL(blob);
  const d = new Date();
  const stamp = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const a = h('a', { href: url, download: `conflict-record-${stamp}.jsonl` });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
  showNotice(`${lines.length} 試合をエクスポートしました。別の PC・ブラウザでは「JSONL を読み込む」でこのファイルを読み込んでください。`);
}

async function importSample() {
  const res = await fetch('sample/matches.sample.jsonl');
  if (!res.ok) {
    showNotice('サンプルデータを読み込めませんでした。');
    return;
  }
  await importText(await res.text(), 'サンプルデータ');
}

function showNotice(message, errors = []) {
  const box = $('#notice');
  const children = [h('p', {}, message)];
  if (errors.length) {
    children.push(h('details', {},
      h('summary', {}, '読めなかった行'),
      h('ul', {}, errors.slice(0, 50).map((e) => h('li', {}, `${e.line} 行目：${e.message}`))),
      errors.length > 50 ? h('p', { class: 'muted' }, `ほか ${errors.length - 50} 行`) : null,
    ));
  }
  children.push(h('button', { class: 'link', type: 'button', onclick: () => { box.hidden = true; } }, '閉じる'));
  box.replaceChildren(...children);
  box.hidden = false;
}

// ---------- 描画 ----------

function render() {
  const hasData = state.matches.length > 0;
  $('#empty').hidden = hasData;
  $('#dashboard').hidden = !hasData;
  $('#clear').disabled = !hasData;
  $('#export').disabled = !hasData;
  $('#count').textContent = hasData ? `保存済み ${state.matches.length} 試合` : '';
  if (!hasData) return;

  renderFilters();
  renderCharHead();
  // 対戦履歴は、期間・ジョブの絞り込みに関係なく、選んでいるキャラの全試合から作る
  state.history = historyFor(state.filter.char);
  const filtered = applyFilter(state.matches, state.filter);
  $('#filtered-empty').hidden = filtered.length > 0;
  $('#filtered').hidden = filtered.length === 0;
  if (!filtered.length) return;

  renderKpis(filtered);
  renderTrend(filtered);
  renderGroups(filtered);
  renderPeers(filtered);
  renderMatchList(filtered);
  renderPlayers();
}

// ---------- 自分のキャラクター ----------

// 記録に出てくる自分のキャラ（名前＋ワールド）。最近遊んだ順
function charList() {
  const chars = new Map();
  for (const m of state.matches) {
    const key = selfKey(m);
    if (!chars.has(key)) chars.set(key, { key, name: m.self.name, world: m.self.world, n: 0, last: m });
    chars.get(key).n++;
  }
  return [...chars.values()];
}

// 選んでいたキャラを復元する。なければ一番最近遊んだキャラ
function pickDefaultChar() {
  const chars = charList();
  let saved = null;
  try { saved = localStorage.getItem('char'); } catch { /* 覚えられなくても動く */ }
  if (saved === '' && chars.length > 1) state.filter.char = '';
  else if (saved && chars.some((c) => c.key === saved)) state.filter.char = saved;
  else state.filter.char = chars[0]?.key ?? '';
}

function historyFor(char) {
  const key = char || '*';
  if (!state.historyCache.has(key)) {
    state.historyCache.set(key, buildPlayerHistory(applyFilter(state.matches, { char }), state.dist));
  }
  return state.historyCache.get(key);
}

// タブの上に、選んでいるキャラの名前を出す
function renderCharHead() {
  const chars = charList();
  const c = chars.find((x) => x.key === state.filter.char);
  const box = $('#char-head');
  if (!c) {
    box.replaceChildren(
      h('div', { class: 'char-name' }, 'すべてのキャラ'),
      h('div', { class: 'char-meta' }, `${chars.length} キャラ・${state.matches.length} 試合`),
    );
    return;
  }
  const rank = c.last.rank?.after;
  box.replaceChildren(
    h('div', { class: 'char-name' }, c.name),
    h('div', { class: 'char-meta' }, [c.world, rank, `${c.n} 試合`, `最終 ${f.dateTime(c.last.time)}`].filter(Boolean).join('・')),
  );
}

function renderFilters() {
  const chars = charList();
  const charSelect = $('#filter-char');
  charSelect.replaceChildren(
    ...(chars.length > 1 ? [h('option', { value: '' }, `すべてのキャラ（${state.matches.length}）`)] : []),
    ...chars.map((c) => h('option', { value: c.key }, `${c.name}（${c.world}・${c.n}）`)),
  );
  charSelect.value = state.filter.char;
  charSelect.disabled = chars.length <= 1;

  const jobs = groupBy(applyFilter(state.matches, { char: state.filter.char }), (m) => m.self.job ?? '');
  const select = $('#filter-job');
  const current = state.filter.job;
  select.replaceChildren(
    h('option', { value: '' }, 'すべてのジョブ'),
    ...jobs.map((g) => h('option', { value: g.key || '-' }, `${jobName(g.key || null)}（${g.n}）`)),
  );
  if (current && !jobs.some((g) => (g.key || '-') === current)) state.filter.job = '';
  select.value = state.filter.job;
  $('#filter-days').value = String(state.filter.days);
}

function kpi(label, value, sub) {
  return stat(label, value, sub);
}

// 大きめの数字 1 つ（ラベル・値・補足）
function stat(label, value, sub, size = '') {
  return h('div', { class: `stat ${size}` },
    h('div', { class: 'stat-label' }, label),
    h('div', { class: 'stat-value' }, value),
    sub ? h('div', { class: 'stat-sub' }, sub) : null,
  );
}

function renderHero(matches) {
  const sum = summary(matches);
  const recent = matches.slice(0, 5);
  let streak = 0;
  for (const m of matches) {
    if (m.result !== matches[0].result) break;
    streak++;
  }
  const recentWins = recent.filter((m) => m.result === 'win').length;
  const rank = matches.find((m) => m.rank?.after)?.rank.after ?? null;
  const firstRank = [...matches].reverse().find((m) => m.rank?.before)?.rank.before ?? null;

  $('#hero').replaceChildren(
    stat('勝率', f.pct(sum.winRate, 1), `${sum.wins} 勝 ${sum.n - sum.wins} 敗・${sum.n} 試合`, 'lg'),
    stat('スコア', f.int(avgScore(matches)), '同じ役割の中の位置（50 = 真ん中）', 'lg'),
    stat('ランク', rank ?? '–', firstRank && firstRank !== rank ? `期間の最初 ${firstRank}` : ''),
    h('div', { class: 'stat' },
      h('div', { class: 'stat-label' }, '直近 5 試合'),
      h('div', { class: 'form-strip', role: 'list', 'aria-label': '直近 5 試合（左が新しい）' },
        recent.map((m) => h('span', {
          class: `form-pip ${m.result}`,
          role: 'listitem',
          title: `${f.dateTime(m.time)}・${RESULT_NAMES[m.result]}・${jobName(m.self.job)}`,
        }, m.result === 'win' ? '勝' : '負')),
      ),
      h('div', { class: 'stat-sub' }, `${recentWins} 勝・${streak} ${matches[0].result === 'win' ? '連勝中' : '連敗中'}`),
    ),
  );
}

function renderKpis(matches) {
  renderHero(matches);
  const sum = summary(matches);
  $('#kpis').replaceChildren(
    stat('平均 K / D / A', `${f.dec(sum.avg.k)} / ${f.dec(sum.avg.d)} / ${f.dec(sum.avg.a)}`),
    stat('平均 与ダメージ', f.big(sum.avg.dmg)),
    stat('平均 与ヒール', f.big(sum.avg.heal)),
    stat('平均 移送時間', f.clock(sum.avg.crystal)),
  );
}

// ジョブ名に役割の色の印を付ける
function jobChip(code, recorded = null) {
  const role = roleGroup(code, recorded);
  return h('span', { class: role ? `job ${role}` : 'job' }, jobName(code, recorded));
}

function avgScore(matches) {
  const scores = matches.map((m) => selfScore(m, state.dist)).filter((x) => x?.score != null);
  const fair = scores.filter((x) => x.reliable);
  const xs = (fair.length ? fair : scores).map((x) => x.score);
  return xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null;
}

// 直近 10 試合の勝率の推移：1 試合ごとに「ここまでの勝率」を打つ（最後の点が直近 10 試合の勝率）
const TREND_MATCHES = 10;

function renderTrend(matches) {
  const recent = [...matches].sort((a, b) => a.time - b.time).slice(-TREND_MATCHES);
  let wins = 0;
  const points = recent.map((m, i) => {
    if (m.result === 'win') wins++;
    const n = i + 1;
    return {
      x: n,
      y: wins / n,
      cls: m.result,
      tip: [
        { value: f.pct(wins / n), label: `${n} 試合目までの勝率（${wins} 勝 ${n - wins} 敗）`, key: 'line-key' },
        { value: RESULT_NAMES[m.result], label: `${f.dateTime(m.time)}・${jobName(m.self.job)}${m.map ? `・${m.map}` : ''}` },
      ],
    };
  });
  lineChart($('#trend-chart'), points, {
    ref: 0.5,
    dots: true,
    endLabel: f.pct(points.at(-1)?.y),
    xLabel: '試合',
    ariaLabel: `直近 ${recent.length} 試合の勝率の推移`,
  });
}

function groupTable(groups, keyHeader, keyLabel) {
  return h('table', { class: 'data' },
    h('thead', {}, h('tr', {},
      h('th', {}, keyHeader),
      ...['試合', '勝率', 'K', 'D', 'A', '与ダメ', '与ヒール'].map((t) => h('th', { class: 'num' }, t)),
    )),
    h('tbody', {}, groups.map((g) => h('tr', {},
      h('td', {}, keyLabel(g.key)),
      h('td', { class: 'num' }, f.int(g.n)),
      h('td', { class: 'num' }, f.pct(g.winRate)),
      h('td', { class: 'num' }, f.dec(g.avg.k)),
      h('td', { class: 'num' }, f.dec(g.avg.d)),
      h('td', { class: 'num' }, f.dec(g.avg.a)),
      h('td', { class: 'num' }, f.big(g.avg.dmg)),
      h('td', { class: 'num' }, f.big(g.avg.heal)),
    ))),
  );
}

function winRateBars(container, groups, keyLabel, what) {
  barChart(container, groups.map((g) => ({
    label: keyLabel(g.key),
    value: g.winRate,
    valueLabel: `${f.pct(g.winRate)}・${g.n}試合`,
    tip: [
      { value: f.pct(g.winRate), label: '勝率' },
      { value: `${g.wins} 勝 ${g.n - g.wins} 敗`, label: keyLabel(g.key) },
    ],
  })), {
    max: 1,
    ref: 0.5,
    ticks: [0, 0.5, 1].map((v) => ({ value: v, label: `${v * 100}%` })),
    ariaLabel: `${what}の勝率`,
  });
}

function renderGroups(matches) {
  const jobLabel = (k) => jobName(k || null);
  const mapLabel = (k) => k || '不明';
  const jobs = groupBy(matches, (m) => m.self.job ?? '');
  const maps = groupBy(matches, (m) => m.map ?? '');
  winRateBars($('#job-chart'), jobs, jobLabel, 'ジョブ別');
  $('#job-table').replaceChildren(groupTable(jobs, 'ジョブ', jobLabel));
  winRateBars($('#map-chart'), maps, mapLabel, 'マップ別');
  $('#map-table').replaceChildren(groupTable(maps, 'マップ', mapLabel));
  const tierLabel = (k) => k || '不明';
  const tiers = byTier(matches);
  winRateBars($('#tier-chart'), tiers, tierLabel, 'ランク別');
  $('#tier-table').replaceChildren(groupTable(tiers, 'ランク', tierLabel));
}

function renderPeers(matches) {
  const { rows, usedMatches, peerSamples } = peerComparison(matches);
  $('#peer-note').textContent = usedMatches
    ? `自分の ${usedMatches} 試合を、同じジョブの他のプレイヤー（のべ ${peerSamples} 人）の平均と比べています。`
    : '同じジョブの他のプレイヤーのデータがまだありません（ジョブが記録された試合が必要です）。';
  const usable = usedMatches > 0;
  $('#peer-chart').hidden = !usable;
  $('#peer-table').hidden = !usable;
  if (!usable) return;

  divergingChart($('#peer-chart'), rows.map((r) => ({
    label: r.label,
    value: r.diff,
    valueLabel: f.signedPct(r.diff),
    tip: [
      { value: f.metric(r.selfAvg, r.format), label: '自分の平均' },
      { value: f.metric(r.peerAvg, r.format), label: '同ジョブ平均' },
    ],
  })), { ariaLabel: '同じジョブの平均との差' });

  $('#peer-table').replaceChildren(h('table', { class: 'data' },
    h('thead', {}, h('tr', {},
      h('th', {}, '項目'),
      h('th', { class: 'num' }, '自分の平均'),
      h('th', { class: 'num' }, '同ジョブ平均'),
      h('th', { class: 'num' }, '差'),
    )),
    h('tbody', {}, rows.map((r) => h('tr', {},
      h('td', {}, r.label),
      h('td', { class: 'num' }, f.metric(r.selfAvg, r.format)),
      h('td', { class: 'num' }, f.metric(r.peerAvg, r.format)),
      h('td', { class: 'num' }, f.signedPct(r.diff)),
    ))),
  ));
}

function resultBadge(result) {
  return h('span', { class: `badge ${result}` }, RESULT_NAMES[result]);
}

function renderMatchList(matches) {
  const shown = matches.slice(0, state.listLimit);
  const body = h('tbody');
  for (const m of shown) {
    const open = state.openMatch === m.id;
    const toggle = () => {
      state.openMatch = open ? null : m.id;
      renderMatchList(matches);
    };
    body.append(h('tr', {
      class: `match-row ${m.result}${open ? ' open' : ''}`,
      tabindex: 0,
      'aria-expanded': String(open),
      onclick: toggle,
      onkeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } },
    },
      h('td', {}, f.dateTime(m.time)),
      h('td', {}, resultBadge(m.result)),
      h('td', {}, jobChip(m.self.job)),
      h('td', {}, m.map ?? '不明'),
      h('td', { class: 'num' }, `${m.self.k} / ${m.self.d} / ${m.self.a}`),
      h('td', { class: 'num' }, f.big(m.self.dmg)),
      h('td', { class: 'num' }, f.big(m.self.heal)),
      h('td', { class: 'num' }, f.clock(m.self.crystal)),
      h('td', {}, m.warnings.length ? h('span', { class: 'warn', title: m.warnings.join('\n') }, '要確認') : ''),
    ));
    if (open) body.append(h('tr', { class: 'detail' }, h('td', { colspan: 9 }, scoreboard(m))));
  }

  const table = h('table', { class: 'data matches' },
    h('thead', {}, h('tr', {},
      h('th', {}, '日時'), h('th', {}, '結果'), h('th', {}, 'ジョブ'), h('th', {}, 'マップ'),
      h('th', { class: 'num' }, 'K / D / A'), h('th', { class: 'num' }, '与ダメ'),
      h('th', { class: 'num' }, '与ヒール'), h('th', { class: 'num' }, '移送'), h('th', {}, ''),
    )),
    body,
  );
  const more = matches.length > shown.length
    ? h('button', { type: 'button', class: 'secondary', onclick: () => { state.listLimit += PAGE_SIZE; renderMatchList(matches); } },
        `もっと見る（残り ${matches.length - shown.length} 試合）`)
    : null;
  $('#match-list').replaceChildren(h('div', { class: 'table-scroll' }, table), more);
}

// スコアのマス。detail（その試合の内訳）があれば、クリック・カーソルで内訳を出す
function scoreCell(score, reliable = true, detail = null) {
  if (score == null) return '–';
  // 50 を中心に 5 段階で色を付ける（高いほど緑、低いほどオレンジ）
  const band = score >= 75 ? 'hi2' : score >= 60 ? 'hi1' : score > 40 ? 'mid' : score > 25 ? 'lo1' : 'lo2';
  const cls = `score ${band}${reliable ? '' : ' ref'}`;
  const text = `${f.int(score)}${reliable ? '' : '*'}`;
  if (!detail) {
    return h('span', { class: cls, title: reliable ? '' : 'ジョブが分からないため、10 人全員と比べた参考値' }, text);
  }
  const show = (e) => {
    e?.stopPropagation();
    const rect = e?.currentTarget?.getBoundingClientRect?.();
    if (rect) showTooltip(rect.left, rect.bottom, scoreReasonRows(detail));
  };
  return h('span', {
    class: `${cls} clickable-score`, tabindex: 0, role: 'button', 'aria-label': `スコア ${text} の内訳`,
    onclick: show, onmouseenter: show, onfocus: show, onmouseleave: hideTooltip, onblur: hideTooltip,
  }, text);
}

function playerButton(p) {
  if (p.self || !p.name) return p.name;
  return h('button', { type: 'button', class: 'player-link', onclick: (e) => { e.stopPropagation(); openPlayer(playerKey(p)); } }, p.name);
}

// 過去の対戦：この試合も含めた、会った回数とスコア（平均）
function pastCell(p) {
  if (p.self) return '';
  const hist = state.history.get(playerKey(p));
  if (!hist || hist.n <= 1) return h('span', { class: 'muted' }, '初');
  return h('span', { title: `${hist.confidence.label}・スコア（実力・一緒のときの勝率・敵のときに負けた率）` }, `${hist.n} 戦・${f.int(hist.score)}`);
}

// ---------- スコアの内訳 ----------

const PART_LABELS = { k: 'K', d: 'D', a: 'A', dmg: '与ダメ', taken: '被ダメ', heal: '与ヒール', crystal: '移送' };

function weightText(weights, labels) {
  return Object.entries(weights).map(([k, w]) => `${labels[k]} ${Math.round(w * 100)}%`).join('・');
}

const PERF_LABELS = { dmg: '与ダメ', d: 'デス（少ないほど良い）', k: 'キル', a: 'アシスト' };
const PLAYER_LABELS = { perf: '実力', ally: '一緒のときの勝率', enemy: '敵のときにこっちが負けた率' };

function scoreRuleText() {
  return `試合のスコア：与ダメ・デス・キル・アシストが、読み込んだ全データの同じ役割（DPS・タンク・ヒーラー）の中で下から何 % の位置か（0〜100、50 = 真ん中）。重み：${weightText(PERF_WEIGHTS, PERF_LABELS)}。* はジョブが分からず全員の中で比べた参考値。`;
}

// 1 項目を「同じ役割の中でどのくらいの位置か」の文にする
function partText(x) {
  const label = PERF_LABELS[x.key].replace('（少ないほど良い）', '');
  const value = x.key === 'dmg' ? f.big(x.value) : x.value;
  const place = x.pct >= 50 ? `上位 ${Math.max(1, Math.round(100 - x.pct))}%` : `下位 ${Math.max(1, Math.round(x.pct))}%`;
  return `${label} ${value}（${place}）`;
}

// スコアの内訳：比べた相手と、項目ごとの位置
function scoreReasonRows(sc) {
  const sorted = [...sc.parts].sort((a, b) => b.pct - a.pct);
  const highs = sorted.filter((x) => x.pct >= 65);
  const lows = sorted.filter((x) => x.pct <= 35).reverse();
  return [
    { value: `スコア ${f.int(sc.score)}`, label: sc.reliable ? `全データの${ROLE_LABELS[sc.role]}（${sc.rows} 件）の中の位置` : `ジョブ不明のため、全員（${sc.rows} 件）の中の位置（参考）` },
    highs.length ? { value: '良かった', label: highs.map(partText).join('、') } : null,
    lows.length ? { value: '低かった', label: lows.map(partText).join('、') } : null,
    !highs.length && !lows.length ? { value: '真ん中くらい', label: sorted.map(partText).join('、') } : null,
  ].filter(Boolean);
}

function scoreboard(m) {
  const meta = [
    m.duration != null ? `経過時間 ${f.clock(m.duration)}` : null,
    m.rank?.before || m.rank?.after ? `${m.rank.before ?? '?'} → ${m.rank.after ?? '?'}` : null,
  ].filter(Boolean).join('・');
  const scored = new Map(scoreMatch(m, state.dist).map((x) => [x.player, x]));
  return h('div', { class: 'scoreboard' },
    meta ? h('p', { class: 'muted' }, meta) : null,
    m.warnings.length ? h('ul', { class: 'warn-list' }, m.warnings.map((w) => h('li', {}, w))) : null,
    ...TEAMS.map((t) => {
      const team = m.teams[t];
      const mine = t === m.self.team;
      return h('div', { class: 'team' },
        h('div', { class: `team-head ${t}` },
          h('span', { class: `team-key ${t}`, 'aria-hidden': 'true' }),
          h('strong', {}, `チーム・${TEAM_NAMES[t]}`),
          h('span', { class: 'muted' }, mine ? '（味方）' : '（敵）'),
          resultBadge(team.result),
          h('span', { class: 'muted' }, `${team.progress != null ? `進行度 ${team.progress}%・` : ''}K ${team.k} / D ${team.d} / A ${team.a}`),
        ),
        h('div', { class: 'table-scroll' }, h('table', { class: 'data compact' },
          h('thead', {}, h('tr', {},
            h('th', {}, 'ジョブ'), h('th', {}, 'キャラクター'), h('th', {}, 'ワールド'), h('th', {}, '階級'),
            ...['K', 'D', 'A', '与ダメ', '被ダメ', '与ヒール', '移送', 'スコア'].map((c) => h('th', { class: 'num' }, c)),
            h('th', { class: 'num' }, '過去の対戦'),
          )),
          h('tbody', {}, m.players.filter((p) => p.team === t).map((p) => {
            const sc = scored.get(p);
            return h('tr', { class: p.self ? 'self' : '' },
              h("td", {}, jobChip(p.job, p.role)),
              h('td', {}, playerButton(p), p.self ? h('span', { class: 'you' }, '自分') : null),
              h('td', {}, p.world),
              h('td', {}, p.tier),
              h('td', { class: 'num' }, p.k),
              h('td', { class: 'num' }, p.d),
              h('td', { class: 'num' }, p.a),
              h('td', { class: 'num' }, f.int(p.dmg)),
              h('td', { class: 'num' }, f.int(p.taken)),
              h('td', { class: 'num' }, f.int(p.heal)),
              h('td', { class: 'num' }, f.clock(p.crystal)),
              h('td', { class: 'num' }, scoreCell(sc?.score, sc?.reliable, sc)),
              h('td', { class: 'num' }, pastCell(p)),
            );
          })),
        )),
      );
    }),
    h('p', { class: 'muted note' }, scoreRuleText(), h('br'), 'スコアをクリックすると、内訳（何が良くて何が低かったか）を表示します。'),
  );
}

// ---------- プレイヤー ----------

function relationText(g) {
  return g.relation === 'ally' ? '味方' : '敵';
}

// 並べ替え。少ない試合数の差は偶然が大きいので、差で並べるときは 3 試合以上の人を先にする
const enough = (n) => (n >= 3 ? 1 : 0);
const trusted = (p) => p.confidence.level;
const PLAYER_SORTS = {
  n: { cmp: (a, b) => b.n - a.n || (b.score ?? 0) - (a.score ?? 0) },
  allyBest: { cmp: (a, b) => enough(b.allyN) - enough(a.allyN) || (b.allyScore ?? -1) - (a.allyScore ?? -1) },
  allyWorst: { cmp: (a, b) => enough(b.allyN) - enough(a.allyN) || (a.allyScore ?? 999) - (b.allyScore ?? 999) },
  index: { cmp: (a, b) => Math.min(trusted(b), 2) - Math.min(trusted(a), 2) || (b.score ?? 0) - (a.score ?? 0) },
  indexLow: { cmp: (a, b) => Math.min(trusted(b), 2) - Math.min(trusted(a), 2) || (a.score ?? 999) - (b.score ?? 999) },
};


// スコアの決め方（プレイヤー欄の下に出す）
function indexExplanation() {
  return h('details', { class: 'explain' },
    h('summary', {}, 'スコアの決め方'),
    h('p', {}, scoreRuleText()),
    h('p', {}, `プレイヤーのスコア：${weightText(PLAYER_WEIGHTS, PLAYER_LABELS)} を合わせたもの（0〜100）。実力はその人の試合のスコアの平均（ジョブが分かっている試合だけ）。味方・敵のどちらかになったことしかない人は、あるものだけで出します。`),
    h('p', {}, `勝率は、試合数が少ないうちは普段の勝率に寄せて補正します（${WINRATE_PRIOR} 試合分）。3 試合で 3 連勝しても大きな値にはならず、試合数が増えるほど実際の値に近づきます。会った回数が多いほど信頼できます（参考：3 回以下 / 中：4〜9 / 高：10 以上）。`),
  );
}

function renderPlayers() {
  const q = state.playerQuery.trim().toLowerCase();
  const all = [...state.history.values()];
  const list = (q
    ? all.filter((p) => p.name.toLowerCase().includes(q) || p.world.toLowerCase().includes(q))
    : all.filter((p) => p.n >= 2))
    .sort(PLAYER_SORTS[state.playerSort].cmp);
  const shown = list.slice(0, 50);

  $('#players-note').textContent = q
    ? `「${state.playerQuery}」に一致：${list.length} 人`
    : `2 回以上会ったプレイヤー：${list.length} 人（全 ${all.length} 人）`;

  if (!shown.length) {
    $('#players-list').replaceChildren(h('p', { class: 'muted' }, q ? '見つかりませんでした。' : 'まだ 2 回以上会ったプレイヤーはいません。'));
    return;
  }
  $('#players-list').replaceChildren(h('div', { class: 'table-scroll' }, h('table', { class: 'data players' },
    h('thead', {}, h('tr', {},
      h('th', {}, 'キャラクター'), h('th', {}, 'ワールド'), h('th', {}, 'よく使うジョブ'),
      h('th', { class: 'num' }, '会った回数'), h('th', { class: 'num' }, '一緒のときの勝率'),
      h('th', { class: 'num' }, '敵のときに負けた率'),
      h('th', { class: 'num', title: '試合のスコアの平均' }, '実力'),
      h('th', { class: 'num', title: '実力・一緒のときの勝率・敵のときに負けた率を合わせたもの' }, 'スコア'),
    )),
    h('tbody', {}, shown.map((p) => h('tr', { class: 'clickable', tabindex: 0, onclick: () => openPlayer(p.key), onkeydown: (e) => { if (e.key === 'Enter') openPlayer(p.key); } },
      h('td', {}, p.name),
      h('td', {}, p.world),
      h('td', {}, p.jobs.slice(0, 2).map((j) => jobName(j.job)).join('、')),
      h('td', { class: 'num' }, p.n),
      h('td', { class: 'num' }, p.allyN ? `${f.pct(p.allyWinRate)}（${p.allyN}）` : '–'),
      h('td', { class: 'num' }, p.enemyN ? `${f.pct(p.enemyLossRate)}（${p.enemyN}）` : '–'),
      h('td', { class: 'num' }, f.int(p.perf), p.perfFair ? '' : '*'),
      h('td', { class: 'num' }, scoreCell(p.score, p.perfFair), h('span', { class: `conf c${p.confidence.level}`, title: `${p.confidence.label}（${p.n} 回）` })),
    ))),
  )), list.length > shown.length ? h('p', { class: 'muted' }, `ほか ${list.length - shown.length} 人。名前で検索してください。`) : null, indexExplanation());
}


function openPlayer(key) {
  const p = state.history.get(key);
  if (!p) return;
  const dlg = $('#player-dialog');
  const verdict = p.confidence.level === 1
    ? `${p.n} 回だけなので、まだ参考程度です。`
    : (p.score >= 75 ? 'かなり強めです。'
      : p.score >= 55 ? '平均より少し上です。'
      : p.score >= 45 ? '平均くらいです。'
      : p.score >= 25 ? '平均より少し下です。'
      : 'かなり弱めです。') + `（${p.confidence.label}・${p.n} 回）`;

  dlg.querySelector('.dialog-body').replaceChildren(
    h('div', { class: 'dialog-head' },
      h('div', {},
        h('h2', {}, p.name),
        h('p', { class: 'muted' }, `${p.world}・${p.tier || '階級不明'}・${p.jobs.map((j) => `${jobName(j.job)} ${j.n}`).join('、')}`),
      ),
      h('button', { type: 'button', class: 'icon', 'aria-label': '閉じる', onclick: () => dlg.close() }, '×'),
    ),
    h('div', { class: 'stats small' },
      kpi('スコア', f.int(p.score), verdict),
      kpi('実力', f.int(p.perf), `試合のスコアの平均（${p.perfN} 試合）${p.perfFair ? '' : '・参考値'}`),
      kpi('会った回数', `${p.n} 回`, `味方 ${p.allyN}・敵 ${p.enemyN}`),
      kpi('一緒のときの勝率', p.allyN ? f.pct(p.allyWinRate) : '–',
        p.allyN ? `${p.allyN} 試合（普段の勝率 ${f.pct(p.myWinRate)}）` : '味方になったことなし'),
      kpi('敵のときに負けた率', p.enemyN ? f.pct(p.enemyLossRate) : '–',
        p.enemyN ? `${p.enemyN} 試合（普段の負けた率 ${f.pct(1 - p.myWinRate)}）` : '敵になったことなし'),
      kpi('平均 K / D / A', `${f.dec(p.avg.k)} / ${f.dec(p.avg.d)} / ${f.dec(p.avg.a)}`),
    ),
    h('h3', {}, '出会った試合'),
    h('div', { class: 'table-scroll' }, h('table', { class: 'data compact' },
      h('thead', {}, h('tr', {},
        h('th', {}, '日時'), h('th', {}, '関係'), h('th', {}, '自分の結果'), h('th', {}, 'ジョブ'), h('th', {}, 'マップ'),
        h('th', { class: 'num' }, 'K / D / A'), h('th', { class: 'num' }, '与ダメ'), h('th', { class: 'num' }, '与ヒール'),
        h('th', { class: 'num' }, 'スコア'),
      )),
      h('tbody', {}, p.games.map((g) => h('tr', { class: 'clickable', tabindex: 0, onclick: () => showMatch(g.match.id) },
        h('td', {}, f.dateTime(g.match.time)),
        h('td', {}, relationText(g)),
        h('td', {}, resultBadge(g.match.result)),
        h("td", {}, jobChip(g.player.job, g.player.role)),
        h('td', {}, g.match.map ?? '不明'),
        h('td', { class: 'num' }, `${g.player.k} / ${g.player.d} / ${g.player.a}`),
        h('td', { class: 'num' }, f.big(g.player.dmg)),
        h('td', { class: 'num' }, f.big(g.player.heal)),
        h('td', { class: 'num' }, scoreCell(g.score, g.reliable, g.detail)),
      ))),
    )),
    h('p', { class: 'muted note' }, '行をクリックすると、試合一覧でその試合を開きます。'),
  );
  if (!dlg.open) dlg.showModal();
}

// プレイヤー詳細から試合一覧の該当試合へ
function showMatch(id) {
  $('#player-dialog').close();
  state.filter = { ...state.filter, days: 0, job: '' };
  state.openMatch = id;
  setTab('matches', { push: true, rerender: false });
  const idx = state.matches.findIndex((m) => m.id === id);
  state.listLimit = Math.max(PAGE_SIZE, Math.ceil((idx + 1) / PAGE_SIZE) * PAGE_SIZE);
  render();
  document.querySelector('.match-row.open')?.scrollIntoView({ block: 'center' });
}

// ---------- 操作 ----------

const THEMES = ['light', 'dark', 'mint'];

function setupTheme() {
  // ホワイト・ブラック・ミントの 3 種類。選んだものはこのブラウザに覚えておく
  const root = document.documentElement;
  let saved = null;
  try { saved = localStorage.getItem('theme'); } catch { /* 保存できなくても動く */ }
  root.dataset.theme = THEMES.includes(saved) ? saved : 'light';
  const select = $('#theme');
  select.value = root.dataset.theme;
  select.addEventListener('change', () => {
    root.dataset.theme = select.value;
    try { localStorage.setItem('theme', select.value); } catch { /* 同上 */ }
    render();
  });
}

function setupImport() {
  const input = $('#file');
  input.addEventListener('change', async () => {
    await importFiles([...input.files]);
    input.value = '';
  });
  for (const btn of document.querySelectorAll('[data-pick-file]')) {
    btn.addEventListener('click', () => input.click());
  }

  let depth = 0;
  const overlay = $('#drop-overlay');
  window.addEventListener('dragenter', (e) => {
    if (!e.dataTransfer?.types.includes('Files')) return;
    depth++;
    overlay.hidden = false;
  });
  window.addEventListener('dragleave', () => {
    depth = Math.max(0, depth - 1);
    if (depth === 0) overlay.hidden = true;
  });
  window.addEventListener('dragover', (e) => e.preventDefault());
  window.addEventListener('drop', async (e) => {
    e.preventDefault();
    depth = 0;
    overlay.hidden = true;
    const files = [...(e.dataTransfer?.files ?? [])];
    if (files.length) await importFiles(files);
  });

  $('#sample').addEventListener('click', importSample);

  $('#export').addEventListener('click', exportAll);

  $('#clear').addEventListener('click', async () => {
    if (!confirm('このブラウザに保存した試合データをすべて削除します。元の JSONL ファイルは消えません。よろしいですか？')) return;
    await clearAll();
    state.openMatch = null;
    state.listLimit = PAGE_SIZE;
    showNotice('保存していたデータを削除しました。');
    await reload();
  });
}

function setupFilters() {
  $('#filter-char').addEventListener('change', (e) => {
    state.filter.char = e.target.value;
    try { localStorage.setItem('char', state.filter.char); } catch { /* 覚えられなくても動く */ }
    state.listLimit = PAGE_SIZE;
    state.openMatch = null;
    render();
  });
  $('#filter-days').addEventListener('change', (e) => {
    state.filter.days = Number(e.target.value);
    state.listLimit = PAGE_SIZE;
    render();
  });
  $('#filter-job').addEventListener('change', (e) => {
    state.filter.job = e.target.value;
    state.listLimit = PAGE_SIZE;
    render();
  });
}

// ---------- タブ ----------

// タブを切り替える。選んだタブは URL（#matches など）とこのブラウザに覚えておく
function setTab(name, { push = true, rerender = true } = {}) {
  if (!TABS.includes(name)) name = 'overview';
  state.tab = name;
  for (const btn of document.querySelectorAll('.tab')) {
    const on = btn.dataset.tab === name;
    btn.setAttribute('aria-selected', String(on));
    btn.tabIndex = on ? 0 : -1;
  }
  for (const panel of document.querySelectorAll('.tab-panel')) panel.hidden = panel.dataset.tab !== name;
  try { localStorage.setItem('tab', name); } catch { /* 覚えられなくても動く */ }
  const hash = `#${name}`;
  if (location.hash !== hash) {
    if (push) history.pushState(null, '', hash);
    else history.replaceState(null, '', hash);
  }
  // 隠れていたタブのグラフは幅が 0 で描かれているので、表示してから描き直す
  if (rerender) render();
}

function setupTabs() {
  const tabs = [...document.querySelectorAll('.tab')];
  for (const btn of tabs) {
    btn.addEventListener('click', () => setTab(btn.dataset.tab));
    btn.addEventListener('keydown', (e) => {
      const i = tabs.indexOf(btn);
      const next = e.key === 'ArrowRight' ? tabs[(i + 1) % tabs.length] : e.key === 'ArrowLeft' ? tabs[(i - 1 + tabs.length) % tabs.length] : null;
      if (!next) return;
      e.preventDefault();
      next.focus();
      setTab(next.dataset.tab);
    });
  }
  window.addEventListener('popstate', () => setTab(location.hash.slice(1), { push: false }));

  let saved = null;
  try { saved = localStorage.getItem('tab'); } catch { /* 同上 */ }
  const fromUrl = location.hash.slice(1);
  setTab(TABS.includes(fromUrl) ? fromUrl : saved, { push: false, rerender: false });
}

function setupPlayers() {
  $('#player-sort').addEventListener('change', (e) => {
    state.playerSort = e.target.value;
    renderPlayers();
  });
  $('#player-search').addEventListener('input', (e) => {
    state.playerQuery = e.target.value;
    renderPlayers();
  });
  const dlg = $('#player-dialog');
  // 背景をクリックしたら閉じる
  dlg.addEventListener('click', (e) => { if (e.target === dlg) dlg.close(); });
}

function setupResize() {
  let last = 0;
  new ResizeObserver(([entry]) => {
    const w = Math.round(entry.contentRect.width);
    if (w === last) return;
    last = w;
    if (state.matches.length) render();
  }).observe($('main'));
}

setupTheme();
setupImport();
setupFilters();
setupTabs();
setupPlayers();
setupResize();
reload()
  .then(() => {
    // ?demo：保存データが空なら、サンプルを読み込んだ状態で開く（見た目の確認用）
    if (new URLSearchParams(location.search).has('demo') && state.matches.length === 0) return importSample();
  })
  .catch((err) => showNotice(`保存データを開けませんでした：${err.message}`));
