// PvP で使えるジョブ。キーは JSONL の job に入る略称。
export const JOBS = {
  PLD: { name: 'ナイト', role: 'tank' },
  WAR: { name: '戦士', role: 'tank' },
  DRK: { name: '暗黒騎士', role: 'tank' },
  GNB: { name: 'ガンブレイカー', role: 'tank' },
  WHM: { name: '白魔道士', role: 'healer' },
  SCH: { name: '学者', role: 'healer' },
  AST: { name: '占星術師', role: 'healer' },
  SGE: { name: '賢者', role: 'healer' },
  MNK: { name: 'モンク', role: 'melee' },
  DRG: { name: '竜騎士', role: 'melee' },
  NIN: { name: '忍者', role: 'melee' },
  SAM: { name: '侍', role: 'melee' },
  RPR: { name: 'リーパー', role: 'melee' },
  VPR: { name: 'ヴァイパー', role: 'melee' },
  BRD: { name: '吟遊詩人', role: 'ranged' },
  MCH: { name: '機工士', role: 'ranged' },
  DNC: { name: '踊り子', role: 'ranged' },
  BLM: { name: '黒魔道士', role: 'caster' },
  SMN: { name: '召喚士', role: 'caster' },
  RDM: { name: '赤魔道士', role: 'caster' },
  PCT: { name: 'ピクトマンサー', role: 'caster' },
};

export const ROLE_NAMES = {
  tank: 'タンク',
  healer: 'ヒーラー',
  melee: 'メレー',
  ranged: 'レンジ',
  caster: 'キャスター',
};

export function jobName(code) {
  if (!code) return '不明';
  return JOBS[code]?.name ?? code;
}
