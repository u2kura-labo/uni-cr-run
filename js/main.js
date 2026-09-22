import { parseJsonl, normalizeMatch, TEAMS } from './schema.js';
import { loadAll, addRecords, clearAll } from './store.js';
import { MatchIndex } from './dedupe.js';
import {
  applyFilter, summary, rollingWinRate, groupBy, peerComparison,
  buildBaselines, buildPlayerHistory, scoreMatch, performanceIndex, playerKey, byTier, tierOf,
} from './stats.js';
import { lineChart, barChart, divergingChart } from './charts.js';
import { jobName, roleGroup } from './jobs.js';
import { h, s as svgEl } from './dom.js';
import * as f from './format.js';

const ROLLING_WINDOW = 10;
const PAGE_SIZE = 30;
const TEAM_NAMES = { astra: 'アストラ', umbra: 'アンブラ' };
const RESULT_NAMES = { win: '勝ち', lose: '負け' };

const state = {
  matches: [],
  broken: 0,
  filter: { days: 0, job: '' },
  listLimit: PAGE_SIZE,
  openMatch: null,
  baselines: null,
  history: new Map(),
  playerQuery: '',
  playerSort: 'n',
};

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
  // 指数の基準と対戦履歴は、絞り込みに関係なく全試合から作る
  state.baselines = buildBaselines(matches);
  state.history = buildPlayerHistory(matches, state.baselines);
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
  $('#count').textContent = hasData ? `保存済み ${state.matches.length} 試合` : '';
  if (!hasData) return;

  renderFilters();
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

function renderFilters() {
  const jobs = groupBy(state.matches, (m) => m.self.job ?? '');
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
  return h('div', { class: 'kpi' },
    h('div', { class: 'kpi-label' }, label),
    h('div', { class: 'kpi-value' }, value),
    sub ? h('div', { class: 'kpi-sub' }, sub) : null,
  );
}

// 勝率のリング（メーター）。50% の位置に目盛り
function winRing(rate) {
  const r = 48;
  const c = 2 * Math.PI * r;
  const v = Math.max(0, Math.min(1, rate ?? 0));
  return svgEl('svg', { class: 'ring', viewBox: '0 0 112 112', 'aria-hidden': 'true' },
    svgEl('circle', { cx: 56, cy: 56, r, fill: 'none', 'stroke-width': 10, class: 'track' }),
    svgEl('circle', {
      cx: 56, cy: 56, r, fill: 'none', 'stroke-width': 10, class: 'fill',
      'stroke-dasharray': `${c * v} ${c}`, transform: 'rotate(-90 56 56)',
    }),
    svgEl('line', { x1: 56, y1: 112 - 2, x2: 56, y2: 112 - 16, class: 'mid', 'stroke-width': 2 }),
  );
}

const TIER_COLORS = {
  ブロンズ: ['#e0a070', '#8a4b22'],
  シルバー: ['#eef2f7', '#8a96a8'],
  ゴールド: ['#ffe08a', '#b7861b'],
  プラチナ: ['#b8fff0', '#3aa596'],
  ダイヤモンド: ['#c7ecff', '#3987e5'],
  クリスタル: ['#f0dcff', '#8a5cf0'],
};

function emblem(tier) {
  const [light, dark] = TIER_COLORS[tier] ?? ['#c3cad6', '#6d7b91'];
  const id = `emb-${Math.abs([...(tier ?? '')].reduce((a, ch) => a * 31 + ch.charCodeAt(0), 7)) % 1e6}`;
  return svgEl('svg', { class: 'emblem', viewBox: '0 0 48 48', 'aria-hidden': 'true' },
    svgEl('defs', {}, svgEl('linearGradient', { id, x1: 0, y1: 0, x2: 0, y2: 1 },
      svgEl('stop', { offset: 0, 'stop-color': light }),
      svgEl('stop', { offset: 1, 'stop-color': dark }),
    )),
    svgEl('path', { d: 'M24 2 44 14v20L24 46 4 34V14Z', fill: `url(#${id})` }),
    svgEl('path', { d: 'M24 10 34 24 24 38 14 24Z', fill: '#fff', opacity: 0.35 }),
  );
}

function renderHero(matches) {
  const sum = summary(matches);
  const recent = matches.slice(0, 10);
  let streak = 0;
  for (const m of matches) {
    if (m.result !== matches[0].result) break;
    streak++;
  }
  const recentWins = recent.filter((m) => m.result === 'win').length;
  const rankMatch = matches.find((m) => m.rank?.after);
  const rank = rankMatch?.rank.after ?? null;
  const tier = tierOf(rank);
  const firstRank = [...matches].reverse().find((m) => m.rank?.before)?.rank.before ?? null;
  const [whole, frac] = f.pct(sum.winRate, 1).replace('%', '').split('.');

  $('#hero').replaceChildren(
    h('div', { class: 'hero-main' },
      winRing(sum.winRate),
      h('div', {},
        h('div', { class: 'eyebrow' }, 'Win rate'),
        h('div', { class: 'hero-rate' }, whole, frac != null ? h('small', {}, `.${frac}%`) : null),
        h('div', { class: 'hero-sub' }, h('strong', {}, `${sum.wins}W ${sum.n - sum.wins}L`), `・${sum.n} 試合`),
      ),
    ),
    h('div', { class: 'form' },
      h('div', { class: 'eyebrow' }, 'Recent form'),
      h('div', { class: 'form-strip', role: 'list', 'aria-label': '直近 10 試合（新しい順）' },
        recent.map((m) => h('span', {
          class: `form-pip ${m.result}`,
          role: 'listitem',
          title: `${f.dateTime(m.time)}・${RESULT_NAMES[m.result]}・${jobName(m.self.job)}`,
        }, m.result === 'win' ? 'W' : 'L')),
      ),
      h('div', { class: 'form-note' },
        h('span', { class: 'streak' }, `${streak} ${matches[0].result === 'win' ? '連勝中' : '連敗中'}`),
        `・直近 ${recent.length} 試合で ${recentWins} 勝`,
      ),
    ),
    h('div', { class: 'rank-card' },
      emblem(tier),
      h('div', {},
        h('div', { class: 'eyebrow' }, 'Rank'),
        rank
          ? h('div', {}, h('span', { class: 'rank-tier' }, tier ?? rank), tier ? h('span', { class: 'rank-stage' }, ` ${rank.slice(tier.length)}`) : null)
          : h('div', { class: 'rank-tier' }, '–'),
        firstRank && firstRank !== rank ? h('div', { class: 'rank-from' }, `期間の最初：${firstRank}`) : null,
      ),
    ),
  );
}

function renderKpis(matches) {
  renderHero(matches);
  const sum = summary(matches);
  $('#kpis').replaceChildren(
    kpi('平均 K / D / A', `${f.dec(sum.avg.k)} / ${f.dec(sum.avg.d)} / ${f.dec(sum.avg.a)}`),
    kpi('平均 与ダメージ', f.big(sum.avg.dmg)),
    kpi('平均 与ヒール', f.big(sum.avg.heal)),
    kpi('平均 移送時間', f.clock(sum.avg.crystal)),
    kpi('平均 指数', f.int(avgIndex(matches)), '同ジョブ平均 = 100'),
  );
}

// ジョブ名に役割の色の印を付ける
function jobChip(code) {
  const role = roleGroup(code);
  return h('span', { class: role ? `job ${role}` : 'job' }, jobName(code));
}

function avgIndex(matches) {
  const xs = matches.map((m) => performanceIndex(m.self, state.baselines)).filter((v) => v != null);
  return xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null;
}

function renderTrend(matches) {
  // 試合数が区間に満たない最初のほうは値が跳ねるので、区間がそろってから描く
  const all = rollingWinRate(matches, ROLLING_WINDOW);
  const rolling = all.length > ROLLING_WINDOW ? all.filter((r) => r.size === ROLLING_WINDOW) : all;
  const points = rolling.map((r) => ({
    x: r.index,
    y: r.rate,
    tip: [
      { value: f.pct(r.rate), label: `直近 ${r.size} 試合の勝率`, key: 'line-key' },
      { value: `${r.index} 試合目`, label: `${f.dateTime(r.match.time)}・${RESULT_NAMES[r.match.result]}` },
    ],
  }));
  lineChart($('#trend-chart'), points, {
    ref: 0.5,
    endLabel: f.pct(rolling.at(-1)?.rate),
    xLabel: '試合',
    ariaLabel: `直近 ${ROLLING_WINDOW} 試合の勝率の推移`,
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
  return h('span', { class: `badge ${result}` }, h('span', { class: 'badge-icon', 'aria-hidden': 'true' }, result === 'win' ? '▲' : '▼'), RESULT_NAMES[result]);
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

function indexCell(value) {
  if (value == null) return '–';
  const cls = value >= 110 ? 'idx high' : value < 90 ? 'idx low' : 'idx';
  return h('span', { class: cls }, f.int(value));
}

function playerButton(p) {
  if (p.self || !p.name) return p.name;
  return h('button', { type: 'button', class: 'player-link', onclick: (e) => { e.stopPropagation(); openPlayer(playerKey(p)); } }, p.name);
}

// 過去の対戦：この試合も含めた、会った回数と平均指数
function pastCell(p) {
  if (p.self) return '';
  const hist = state.history.get(playerKey(p));
  if (!hist || hist.n <= 1) return h('span', { class: 'muted' }, '初');
  return `${hist.n} 戦・${f.int(hist.avgIndex)}`;
}

function scoreboard(m) {
  const meta = [
    m.duration != null ? `経過時間 ${f.clock(m.duration)}` : null,
    m.rank?.before || m.rank?.after ? `${m.rank.before ?? '?'} → ${m.rank.after ?? '?'}` : null,
  ].filter(Boolean).join('・');
  const scored = new Map(scoreMatch(m, state.baselines).map((x) => [x.player, x]));
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
            ...['K', 'D', 'A', '与ダメ', '被ダメ', '与ヒール', '移送', '指数'].map((c) => h('th', { class: 'num' }, c)),
            h('th', {}, ''),
            h('th', { class: 'num' }, '過去の対戦'),
          )),
          h('tbody', {}, m.players.filter((p) => p.team === t).map((p) => {
            const sc = scored.get(p);
            return h('tr', { class: p.self ? 'self' : '' },
              h('td', {}, jobChip(p.job)),
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
              h('td', { class: 'num' }, indexCell(sc?.index)),
              h('td', {},
                sc?.mvp ? h('span', { class: 'tag mvp' }, 'MVP') : null,
                sc?.worstInTeam ? h('span', { class: 'tag worst' }, '戦犯候補') : null,
              ),
              h('td', { class: 'num' }, pastCell(p)),
            );
          })),
        )),
      );
    }),
    h('p', { class: 'muted note' }, '指数：同じジョブの平均を 100 としたときの成績（K・A・与ダメ・与ヒール・移送は多いほど、D は少ないほど高い）。戦犯候補はチーム内で指数が一番低い人。'),
  );
}

// ---------- プレイヤー ----------

function relationText(g) {
  return g.relation === 'ally' ? '味方' : '敵';
}

// 並べ替え。少ない試合数の差は偶然が大きいので、差で並べるときは 3 試合以上の人を先にする
const enough = (n) => (n >= 3 ? 1 : 0);
const PLAYER_SORTS = {
  n: { cmp: (a, b) => b.n - a.n || (b.avgIndex ?? 0) - (a.avgIndex ?? 0) },
  allyBest: { cmp: (a, b) => enough(b.allyN) - enough(a.allyN) || (b.allyLift ?? -9) - (a.allyLift ?? -9) },
  allyWorst: { cmp: (a, b) => enough(b.allyN) - enough(a.allyN) || (a.allyLift ?? 9) - (b.allyLift ?? 9) },
  index: { cmp: (a, b) => enough(b.n) - enough(a.n) || (b.avgIndex ?? 0) - (a.avgIndex ?? 0) },
  indexLow: { cmp: (a, b) => enough(b.n) - enough(a.n) || (a.avgIndex ?? 999) - (b.avgIndex ?? 999) },
};

// 勝率の差（ポイント）。+ は普段より勝っている
function liftCell(v) {
  if (v == null) return '–';
  const pt = Math.round(v * 100);
  const cls = pt >= 10 ? 'lift up' : pt <= -10 ? 'lift down' : 'lift';
  return h('span', { class: cls }, `${pt > 0 ? '+' : ''}${pt} pt`);
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
      h('th', { class: 'num' }, '会った回数'), h('th', { class: 'num' }, '味方のときの勝率'), h('th', { class: 'num' }, '普段との差'),
      h('th', { class: 'num' }, '敵のときの自分の勝率'), h('th', { class: 'num' }, '普段との差'),
      h('th', { class: 'num' }, '平均指数'), h('th', { class: 'num' }, '戦犯候補'), h('th', { class: 'num' }, 'MVP'),
    )),
    h('tbody', {}, shown.map((p) => h('tr', { class: 'clickable', tabindex: 0, onclick: () => openPlayer(p.key), onkeydown: (e) => { if (e.key === 'Enter') openPlayer(p.key); } },
      h('td', {}, p.name),
      h('td', {}, p.world),
      h('td', {}, p.jobs.slice(0, 2).map((j) => jobName(j.job)).join('、')),
      h('td', { class: 'num' }, p.n),
      h('td', { class: 'num' }, p.allyN ? `${f.pct(p.allyWinRate)}（${p.allyN}）` : '–'),
      h('td', { class: 'num' }, liftCell(p.allyLift)),
      h('td', { class: 'num' }, p.enemyN ? `${f.pct(p.enemyWinRate)}（${p.enemyN}）` : '–'),
      h('td', { class: 'num' }, liftCell(p.enemyLift)),
      h('td', { class: 'num' }, indexCell(p.avgIndex)),
      h('td', { class: 'num' }, p.worstCount || '–'),
      h('td', { class: 'num' }, p.mvpCount || '–'),
    ))),
  )), list.length > shown.length ? h('p', { class: 'muted' }, `ほか ${list.length - shown.length} 人。名前で検索してください。`) : null);
}

function liftText(v) {
  if (v == null) return '–';
  const pt = Math.round(v * 100);
  return `${pt > 0 ? '+' : ''}${pt} pt`;
}

function openPlayer(key) {
  const p = state.history.get(key);
  if (!p) return;
  const dlg = $('#player-dialog');
  const verdict = p.n < 3
    ? '会った回数が少ないので、まだ判断できません。'
    : p.avgIndex >= 110 ? '同じジョブの平均よりかなり強めです。'
    : p.avgIndex >= 100 ? '同じジョブの平均より少し上です。'
    : p.avgIndex >= 90 ? '同じジョブの平均より少し下です。'
    : '同じジョブの平均よりかなり弱めです。';

  dlg.querySelector('.dialog-body').replaceChildren(
    h('div', { class: 'dialog-head' },
      h('div', {},
        h('h2', {}, p.name),
        h('p', { class: 'muted' }, `${p.world}・${p.tier || '階級不明'}・${p.jobs.map((j) => `${jobName(j.job)} ${j.n}`).join('、')}`),
      ),
      h('button', { type: 'button', class: 'icon', 'aria-label': '閉じる', onclick: () => dlg.close() }, '×'),
    ),
    h('div', { class: 'kpis small' },
      kpi('平均指数', f.int(p.avgIndex), verdict),
      kpi('会った回数', `${p.n} 回`, `味方 ${p.allyN}・敵 ${p.enemyN}`),
      kpi('味方のときの勝率', p.allyN ? f.pct(p.allyWinRate) : '–',
        p.allyN ? `${p.allyN} 試合・普段（${f.pct(p.myWinRate)}）より ${liftText(p.allyLift)}` : '味方になったことなし'),
      kpi('敵のときの自分の勝率', p.enemyN ? f.pct(p.enemyWinRate) : '–',
        p.enemyN ? `${p.enemyN} 試合・普段より ${liftText(p.enemyLift)}` : '敵になったことなし'),
      kpi('平均 K / D / A', `${f.dec(p.avg.k)} / ${f.dec(p.avg.d)} / ${f.dec(p.avg.a)}`),
      kpi('戦犯候補 / MVP', `${p.worstCount} / ${p.mvpCount}`, `${p.n} 試合中`),
    ),
    h('h3', {}, '出会った試合'),
    h('div', { class: 'table-scroll' }, h('table', { class: 'data compact' },
      h('thead', {}, h('tr', {},
        h('th', {}, '日時'), h('th', {}, '関係'), h('th', {}, '自分の結果'), h('th', {}, 'ジョブ'), h('th', {}, 'マップ'),
        h('th', { class: 'num' }, 'K / D / A'), h('th', { class: 'num' }, '与ダメ'), h('th', { class: 'num' }, '与ヒール'),
        h('th', { class: 'num' }, '指数'), h('th', {}, ''),
      )),
      h('tbody', {}, p.games.map((g) => h('tr', { class: 'clickable', tabindex: 0, onclick: () => showMatch(g.match.id) },
        h('td', {}, f.dateTime(g.match.time)),
        h('td', {}, relationText(g)),
        h('td', {}, resultBadge(g.match.result)),
        h('td', {}, jobChip(g.player.job)),
        h('td', {}, g.match.map ?? '不明'),
        h('td', { class: 'num' }, `${g.player.k} / ${g.player.d} / ${g.player.a}`),
        h('td', { class: 'num' }, f.big(g.player.dmg)),
        h('td', { class: 'num' }, f.big(g.player.heal)),
        h('td', { class: 'num' }, indexCell(g.index)),
        h('td', {},
          g.mvp ? h('span', { class: 'tag mvp' }, 'MVP') : null,
          g.worstInTeam ? h('span', { class: 'tag worst' }, '戦犯候補') : null,
        ),
      ))),
    )),
    h('p', { class: 'muted note' }, '行をクリックすると、試合一覧でその試合を開きます。'),
  );
  if (!dlg.open) dlg.showModal();
}

// プレイヤー詳細から試合一覧の該当試合へ
function showMatch(id) {
  $('#player-dialog').close();
  state.filter = { days: 0, job: '' };
  state.openMatch = id;
  const idx = state.matches.findIndex((m) => m.id === id);
  state.listLimit = Math.max(PAGE_SIZE, Math.ceil((idx + 1) / PAGE_SIZE) * PAGE_SIZE);
  render();
  document.querySelector('.match-row.open')?.scrollIntoView({ block: 'center' });
}

// ---------- 操作 ----------

function setupTheme() {
  // ダークが基本。切り替えたらこのブラウザに覚えておく
  const root = document.documentElement;
  let saved = null;
  try { saved = localStorage.getItem('theme'); } catch { /* 保存できなくても動く */ }
  root.dataset.theme = saved === 'light' ? 'light' : 'dark';
  $('#theme').addEventListener('click', () => {
    root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem('theme', root.dataset.theme); } catch { /* 同上 */ }
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
setupPlayers();
setupResize();
reload()
  .then(() => {
    // ?demo：保存データが空なら、サンプルを読み込んだ状態で開く（見た目の確認用）
    if (new URLSearchParams(location.search).has('demo') && state.matches.length === 0) return importSample();
  })
  .catch((err) => showNotice(`保存データを開けませんでした：${err.message}`));
