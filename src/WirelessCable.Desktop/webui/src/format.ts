import { t } from './i18n';
/** 显示层的格式化。全部按中文习惯，取整规则与桌面端 WPF 版一致。 */

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return '0 B';
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }

  return `${value >= 100 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

export function formatSpeed(bytesPerSecond: number): string {
  if (!Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) {
    return '';
  }

  return `${formatBytes(bytesPerSecond)}/s`;
}

/** 秒 → 「2 小时 14 分」。 */
export function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return '—';
  }

  const total = Math.floor(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);

  if (hours > 0) {
    return minutes > 0 ? t(`${hours} 小时 ${minutes} 分`) : t(`${hours} 小时`);
  }

  if (minutes > 0) {
    return t(`${minutes} 分`);
  }

  return t(`${total} 秒`);
}

/** 时钟：今天只给 14:32，更早给 11-16 14:32。 */
export function formatClock(unixMs: number): string {
  if (!unixMs) {
    return '—';
  }

  const date = new Date(unixMs);
  const now = new Date();
  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate();

  const time = `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
  if (sameDay) {
    return time;
  }

  return `${date.getMonth() + 1}-${String(date.getDate()).padStart(2, '0')} ${time}`;
}

/** 相对时间：刚刚 / N 分钟前 / N 小时前 / 昨天 21:40 / 11-14。 */
export function formatAgo(unixMs: number): string {
  if (!unixMs) {
    return t('从未');
  }

  const diff = Date.now() - unixMs;
  if (diff < 60_000) {
    return t('刚刚');
  }

  if (diff < 3_600_000) {
    return t(`${Math.floor(diff / 60_000)} 分钟前`);
  }

  const date = new Date(unixMs);
  const now = new Date();
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  if (unixMs >= startOfToday) {
    return t(`${Math.floor(diff / 3_600_000)} 小时前`);
  }

  if (unixMs >= startOfToday - 86_400_000) {
    return t(`昨天 ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`);
  }

  return `${date.getMonth() + 1}-${String(date.getDate()).padStart(2, '0')}`;
}

// 只存中文原文，**在调用时**才翻译：模块级常量只求值一次，
// 放在这里调 t() 会导致启动后切语言时这一列不跟着变（截图里就是这么露的）。
const STATE_TEXT: Record<string, string> = {
  waiting: '等待中',
  preparing: '准备中',
  transferring: '传输中',
  verifying: '校验中',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
  paused: '已暂停',
  interrupted: '已中断',
};

export function stateLabel(state: string): string {
  return t(STATE_TEXT[state] ?? state);
}

const TERMINAL = new Set(['completed', 'cancelled']);

/** 「传输中」页签的归属：失败/中断/暂停都还留在这一页，只有完成和取消沉到下面。 */
export function isActive(state: string): boolean {
  return !TERMINAL.has(state);
}

/**
 * 还在跑的状态（与 C# `TransferStates.IsActive` 严格一致）。
 *
 * 删除记录只能删这些之外的：服务端会拒绝"还在传输"的任务。
 * 注意**不能**用 [isActive] 来判断——失败/中断的任务留在「传输中」页签里，
 * 但它们已经不在跑，记录是可以删的；以前拿 isActive 当判据，
 * 结果失败的任务既不能取消（服务端说已结束）也不能删（界面说进行中），卡死在列表里。
 */
const IN_FLIGHT = new Set(['waiting', 'preparing', 'transferring', 'verifying']);

export function isInFlight(state: string): boolean {
  return IN_FLIGHT.has(state);
}

export function directionLabel(direction: string): string {
  return direction === 'upload' ? t('手机 → 本机') : t('本机 → 手机');
}

/** 上传是手机发来的（我收到的），下载是本机发出去的（我发送的）。 */
export function isSent(direction: string): boolean {
  return direction === 'download';
}

export function percentOf(transferred: number, total: number): number {
  if (!total || total <= 0) {
    return 0;
  }

  return Math.max(0, Math.min(100, Math.round((transferred / total) * 100)));
}

export function shortFingerprint(fingerprint: string | null): string {
  if (!fingerprint) {
    return '—';
  }

  const clean = fingerprint.replace(/:/g, '').toUpperCase();
  if (clean.length <= 12) {
    return clean.replace(/(.{2})/g, '$1:').replace(/:$/, '');
  }

  return `${clean.slice(0, 4)}:${clean.slice(4, 8)}:…:${clean.slice(-4)}`;
}
