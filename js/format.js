const nf = new Intl.NumberFormat('ja-JP');
const dateFmt = new Intl.DateTimeFormat('ja-JP', {
  month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit',
});
const dayFmt = new Intl.DateTimeFormat('ja-JP', { month: 'numeric', day: 'numeric' });

export function int(v) {
  return v == null ? '–' : nf.format(Math.round(v));
}

export function dec(v, digits = 1) {
  return v == null ? '–' : v.toFixed(digits);
}

export function pct(v, digits = 0) {
  return v == null ? '–' : `${(v * 100).toFixed(digits)}%`;
}

export function signedPct(v) {
  if (v == null) return '–';
  const s = (v * 100).toFixed(1);
  if (Number(s) === 0) return '±0.0%';
  return v > 0 ? `+${s}%` : `${s}%`;
}

// 1,324,585 → 132.5万
export function big(v) {
  if (v == null) return '–';
  if (Math.abs(v) >= 10000) return `${(v / 10000).toFixed(1)}万`;
  return nf.format(Math.round(v));
}

export function clock(sec) {
  if (sec == null) return '–';
  const s = Math.round(sec);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

export function dateTime(ms) {
  return dateFmt.format(new Date(ms));
}

export function day(ms) {
  return dayFmt.format(new Date(ms));
}

export function metric(v, format) {
  if (format === 'big') return big(v);
  if (format === 'clock') return clock(v);
  return dec(v);
}
