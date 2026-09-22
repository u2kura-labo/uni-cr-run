// SVG で描く小さなグラフ。色は CSS 変数（--series-1 など）を参照するので、ダークモードでもそのまま切り替わる。
// どのグラフの値も、すぐ下の表か直接ラベルで読めるようにしてあり、ツールチップは補助。
import { h, s } from './dom.js';

let tooltipEl = null;

function tooltip() {
  if (!tooltipEl) {
    tooltipEl = h('div', { class: 'tooltip', role: 'status', hidden: true });
    document.body.append(tooltipEl);
  }
  return tooltipEl;
}

// rows: [{ value, label }]（value が強調、label が補足）
export function showTooltip(x, y, rows) {
  const el = tooltip();
  el.replaceChildren(...rows.map((r) => h('div', { class: 'tooltip-row' },
    r.key ? h('span', { class: `tooltip-key ${r.key}` }) : null,
    h('strong', {}, r.value),
    r.label ? h('span', { class: 'tooltip-label' }, r.label) : null,
  )));
  el.hidden = false;
  const pad = 12;
  const { width, height } = el.getBoundingClientRect();
  let left = x + pad;
  let top = y + pad;
  if (left + width > window.innerWidth - 8) left = x - width - pad;
  if (top + height > window.innerHeight - 8) top = y - height - pad;
  el.style.left = `${Math.max(8, left)}px`;
  el.style.top = `${Math.max(8, top)}px`;
}

export function hideTooltip() {
  if (tooltipEl) tooltipEl.hidden = true;
}

function niceSteps(max, count = 4) {
  if (max <= 0) return [0];
  const raw = max / count;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((f) => f * mag).find((st) => st >= raw);
  const out = [];
  for (let v = 0; v <= max + step * 0.001; v += step) out.push(v);
  return out;
}

// 1系列の折れ線。y は 0〜1 の割合。
// points: [{ x, y, tip: rows[] }], opts: { ref, refLabel, endLabel, xLabel }
export function lineChart(container, points, opts = {}) {
  const width = Math.max(container.clientWidth, 280);
  const height = 220;
  const m = { top: 16, right: 44, bottom: 28, left: 40 };
  const iw = width - m.left - m.right;
  const ih = height - m.top - m.bottom;
  const xMin = points[0]?.x ?? 1;
  const xMax = Math.max(points.at(-1)?.x ?? 2, xMin + 1);
  const x = (v) => m.left + ((v - xMin) / (xMax - xMin)) * iw;
  const y = (v) => m.top + (1 - v) * ih;

  const svg = s('svg', { width, height, viewBox: `0 0 ${width} ${height}`, class: 'chart', role: 'img', 'aria-label': opts.ariaLabel ?? '' });

  for (const t of [0, 0.25, 0.5, 0.75, 1]) {
    svg.append(
      s('line', { x1: m.left, x2: m.left + iw, y1: y(t), y2: y(t), class: t === 0 ? 'axis' : 'grid' }),
      s('text', { x: m.left - 8, y: y(t), class: 'tick', 'text-anchor': 'end', 'dominant-baseline': 'middle' }, `${t * 100}%`),
    );
  }
  if (opts.ref != null) {
    svg.append(s('line', { x1: m.left, x2: m.left + iw, y1: y(opts.ref), y2: y(opts.ref), class: 'ref' }));
  }

  // x 軸の目盛り（試合番号）
  for (const t of niceSteps(xMax, Math.min(5, xMax)).filter((v) => v >= xMin)) {
    svg.append(s('text', { x: x(t), y: height - 8, class: 'tick', 'text-anchor': 'middle' }, String(t)));
  }
  svg.append(s('text', { x: m.left + iw, y: height - 8, class: 'tick', 'text-anchor': 'end', dx: 40 }, opts.xLabel ?? ''));

  if (points.length > 0) {
    const d = points.map((p, i) => `${i ? 'L' : 'M'}${x(p.x).toFixed(1)},${y(p.y).toFixed(1)}`).join('');
    svg.append(
      s('path', { d: `${d}L${x(points.at(-1).x)},${y(0)}L${x(points[0].x)},${y(0)}Z`, class: 'area' }),
      s('path', { d, class: 'line' }),
    );
    // opts.dots：各点に印を付ける（点ごとの class で色を変えられる）
    if (opts.dots) {
      for (const p of points) svg.append(s('circle', { cx: x(p.x), cy: y(p.y), r: 4, class: `dot ${p.cls ?? ''}` }));
    }
    const last = points.at(-1);
    svg.append(
      s('circle', { cx: x(last.x), cy: y(last.y), r: 4, class: `dot ${last.cls ?? ''}` }),
      s('text', { x: x(last.x) + 8, y: y(last.y), class: 'end-label', 'dominant-baseline': 'middle' }, opts.endLabel ?? ''),
    );
  }

  // クロスヘア：ポインタに一番近い試合に吸い付く
  const cross = s('line', { y1: m.top, y2: m.top + ih, class: 'crosshair', visibility: 'hidden' });
  const focusDot = s('circle', { r: 4, class: 'dot', visibility: 'hidden' });
  const hit = s('rect', { x: m.left, y: m.top, width: iw, height: ih, fill: 'transparent', tabindex: 0 });
  svg.append(cross, focusDot, hit);

  let focusIndex = points.length - 1;
  function focusAt(i, clientX, clientY) {
    if (!points.length) return;
    focusIndex = Math.max(0, Math.min(points.length - 1, i));
    const p = points[focusIndex];
    cross.setAttribute('x1', x(p.x));
    cross.setAttribute('x2', x(p.x));
    focusDot.setAttribute('cx', x(p.x));
    focusDot.setAttribute('cy', y(p.y));
    cross.setAttribute('visibility', 'visible');
    focusDot.setAttribute('visibility', 'visible');
    showTooltip(clientX, clientY, p.tip);
  }
  function blur() {
    cross.setAttribute('visibility', 'hidden');
    focusDot.setAttribute('visibility', 'hidden');
    hideTooltip();
  }
  hit.addEventListener('pointermove', (e) => {
    const rect = svg.getBoundingClientRect();
    const px = e.clientX - rect.left;
    const xv = xMin + ((px - m.left) / iw) * (xMax - xMin);
    const i = Math.round(xv - xMin);
    focusAt(i, e.clientX, e.clientY);
  });
  hit.addEventListener('pointerleave', blur);
  hit.addEventListener('blur', blur);
  hit.addEventListener('focus', () => focusFromKeyboard(focusIndex));
  hit.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowLeft') focusFromKeyboard(focusIndex - 1);
    else if (e.key === 'ArrowRight') focusFromKeyboard(focusIndex + 1);
    else return;
    e.preventDefault();
  });
  function focusFromKeyboard(i) {
    const rect = svg.getBoundingClientRect();
    const p = points[Math.max(0, Math.min(points.length - 1, i))];
    if (p) focusAt(i, rect.left + x(p.x), rect.top + y(p.y));
  }

  container.replaceChildren(svg);
}

// 横棒（値は 0〜max）。rows: [{ label, value, valueLabel, tip }]
// opts.ref があれば、その位置に基準線を引く。
export function barChart(container, rows, opts = {}) {
  const width = Math.max(container.clientWidth, 280);
  const band = 32;
  const barH = 16;
  const labelW = Math.min(150, Math.round(width * 0.34));
  const m = { top: 8, right: 96, bottom: 22, left: labelW };
  const iw = width - m.left - m.right;
  const height = m.top + rows.length * band + m.bottom;
  const max = opts.max ?? Math.max(...rows.map((r) => r.value), 1);
  const x = (v) => m.left + (v / max) * iw;

  const svg = s('svg', { width, height, viewBox: `0 0 ${width} ${height}`, class: 'chart', role: 'img', 'aria-label': opts.ariaLabel ?? '' });
  for (const t of opts.ticks ?? []) {
    svg.append(
      s('line', { x1: x(t.value), x2: x(t.value), y1: m.top, y2: height - m.bottom, class: 'grid' }),
      s('text', { x: x(t.value), y: height - 6, class: 'tick', 'text-anchor': 'middle' }, t.label),
    );
  }
  svg.append(s('line', { x1: m.left, x2: m.left, y1: m.top, y2: height - m.bottom, class: 'axis' }));
  // 基準線は棒と数字の下に描く（数字に線が重ならないように）
  if (opts.ref != null) {
    svg.append(s('line', { x1: x(opts.ref), x2: x(opts.ref), y1: m.top, y2: height - m.bottom, class: 'ref' }));
  }

  rows.forEach((r, i) => {
    const cy = m.top + i * band + band / 2;
    const w = Math.max(0, x(r.value) - m.left);
    const g = s('g', { class: 'bar-row', tabindex: 0 });
    g.append(
      s('rect', { x: 0, y: cy - band / 2, width, height: band, fill: 'transparent' }),
      s('text', { x: m.left - 10, y: cy, class: 'cat-label', 'text-anchor': 'end', 'dominant-baseline': 'middle' }, fitLabel(r.label, labelW - 12)),
      s('path', { d: roundedBar(m.left, cy - barH / 2, w, barH), class: 'bar' }),
      s('text', { x: m.left + w + 6, y: cy, class: 'value-label', 'dominant-baseline': 'middle' }, r.valueLabel),
    );
    const show = (e) => {
      const rect = g.getBoundingClientRect();
      showTooltip(e?.clientX ?? rect.left + labelW + w, e?.clientY ?? rect.top + band / 2, r.tip);
    };
    g.addEventListener('pointermove', show);
    g.addEventListener('pointerleave', hideTooltip);
    g.addEventListener('focus', () => show());
    g.addEventListener('blur', hideTooltip);
    svg.append(g);
  });
  container.replaceChildren(svg);
}

// 0 を中心に左右へ伸びる横棒。rows: [{ label, value(-1〜), valueLabel, tip }]
export function divergingChart(container, rows, opts = {}) {
  const width = Math.max(container.clientWidth, 280);
  const band = 32;
  const barH = 16;
  const labelW = Math.min(110, Math.round(width * 0.26));
  const m = { top: 8, right: 64, bottom: 22, left: labelW + 56 };
  const iw = width - m.left - m.right;
  const height = m.top + rows.length * band + m.bottom;
  const lim = Math.max(0.1, ...rows.map((r) => Math.abs(r.value ?? 0))) * 1.05;
  const x = (v) => m.left + ((v + lim) / (2 * lim)) * iw;

  const svg = s('svg', { width, height, viewBox: `0 0 ${width} ${height}`, class: 'chart', role: 'img', 'aria-label': opts.ariaLabel ?? '' });
  svg.append(
    s('line', { x1: x(0), x2: x(0), y1: m.top, y2: height - m.bottom, class: 'axis' }),
    s('text', { x: x(0), y: height - 6, class: 'tick', 'text-anchor': 'middle' }, '同ジョブ平均'),
  );

  rows.forEach((r, i) => {
    const cy = m.top + i * band + band / 2;
    const g = s('g', { class: 'bar-row', tabindex: 0 });
    g.append(
      s('rect', { x: 0, y: cy - band / 2, width, height: band, fill: 'transparent' }),
      s('text', { x: labelW, y: cy, class: 'cat-label', 'text-anchor': 'end', 'dominant-baseline': 'middle' }, r.label),
    );
    if (r.value != null) {
      const x0 = x(0);
      const x1 = x(r.value);
      const up = r.value >= 0;
      g.append(
        s('path', { d: up ? roundedBar(x0, cy - barH / 2, x1 - x0, barH) : roundedBarLeft(x1, cy - barH / 2, x0 - x1, barH), class: up ? 'bar pos' : 'bar neg' }),
        s('text', { x: up ? x1 + 6 : x1 - 6, y: cy, class: 'value-label', 'text-anchor': up ? 'start' : 'end', 'dominant-baseline': 'middle' }, r.valueLabel),
      );
    } else {
      g.append(s('text', { x: x(0) + 6, y: cy, class: 'value-label muted', 'dominant-baseline': 'middle' }, 'データなし'));
    }
    const show = (e) => {
      const rect = g.getBoundingClientRect();
      showTooltip(e?.clientX ?? rect.left + x(r.value ?? 0), e?.clientY ?? rect.top + band / 2, r.tip);
    };
    g.addEventListener('pointermove', show);
    g.addEventListener('pointerleave', hideTooltip);
    g.addEventListener('focus', () => show());
    g.addEventListener('blur', hideTooltip);
    svg.append(g);
  });
  container.replaceChildren(svg);
}

// ラベルが幅に収まらなければ末尾を「…」にする（全体はツールチップと表で読める）。
// 全角を 12px、半角を 7px として見積もる。
function fitLabel(text, maxWidth) {
  const w = (ch) => (/[\u0000-\u00ff]/.test(ch) ? 7 : 12);
  let total = 0;
  const chars = [...text];
  for (let i = 0; i < chars.length; i++) {
    total += w(chars[i]);
    if (total > maxWidth) return chars.slice(0, Math.max(1, i - 1)).join('') + '…';
  }
  return text;
}

// 右端だけ角丸（4px）、基準線側は直角の横棒
function roundedBar(x, y, w, hgt) {
  const r = Math.min(4, w / 2, hgt / 2);
  if (w <= 0) return '';
  return `M${x},${y}H${x + w - r}A${r},${r} 0 0 1 ${x + w},${y + r}V${y + hgt - r}A${r},${r} 0 0 1 ${x + w - r},${y + hgt}H${x}Z`;
}

// 左端だけ角丸の横棒（x は左端）
function roundedBarLeft(x, y, w, hgt) {
  const r = Math.min(4, w / 2, hgt / 2);
  if (w <= 0) return '';
  return `M${x + w},${y}H${x + r}A${r},${r} 0 0 0 ${x},${y + r}V${y + hgt - r}A${r},${r} 0 0 0 ${x + r},${y + hgt}H${x + w}Z`;
}
