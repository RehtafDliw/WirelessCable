/**
 * 极简 i18n：中文原文当键，英文查表。
 *
 * 为什么不用 i18next：这个项目一直在压依赖，而需求只有两种语言、无复数规则、
 * 无按需加载。查表 + 回退到原文，几十行就够，也不会在打包产物里多出几十 KB。
 *
 * 未命中的词条**回退中文**——宁可露出中文，也不要显示 key 或空白。
 */
import { EN, EN_RULES, ERR_EN } from './i18n.en';

export type Lang = 'zh' | 'en';

const STORAGE_KEY = 'wc.lang';
const listeners = new Set<() => void>();

function detect(): Lang {
  const saved = localStorage.getItem(STORAGE_KEY);
  if (saved === 'zh' || saved === 'en') return saved;
  return (navigator.language || '').toLowerCase().startsWith('zh') ? 'zh' : 'en';
}

let current: Lang = detect();

export function getLang(): Lang {
  return current;
}

export function setLang(lang: Lang): void {
  if (lang === current) return;
  current = lang;
  localStorage.setItem(STORAGE_KEY, lang);
  for (const fn of listeners) fn();
}

export function subscribeLang(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/**
 * 翻译一条中文文案。
 *
 * 先查整句；查不到再按规则处理带变量的句子（例如「已选 3 项」）。
 * 都不中就把原文原样返回。
 */
export function t(zh: string): string {
  if (current === 'zh') return zh;

  const exact = EN[zh];
  if (exact !== undefined) return exact;

  for (const [pattern, render] of EN_RULES) {
    const m = zh.match(pattern);
    if (m) return render(m);
  }

  return zh;
}

/** 服务端错误：优先按错误码翻译，没有对应码时退回原文。 */
export function errorText(code: string | undefined, fallback: string): string {
  if (current === 'zh') return fallback;
  if (code && ERR_EN[code]) return ERR_EN[code];
  return fallback;
}
