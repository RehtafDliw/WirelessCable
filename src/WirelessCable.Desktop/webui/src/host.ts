/**
 * 页面 → 桌面宿主的桥（14.5）。
 *
 * 浏览器里没有"选文件""开资源管理器""复制到剪贴板""最小化窗口"这些能力，
 * 所以这几件事统一交给 WebView2 宿主做：postMessage 一个 { id, action, ... }，
 * 宿主执行完回一条 { id, ok, ... }。
 *
 * 不在桌面窗口里打开时（比如直接用浏览器访问 /app 调试），`isDesktop()` 为 false，
 * 调用方据此把按钮置灰或给一句说明，而不是静默失败。
 */

import { t } from './i18n';

type HostReply = {
  id: string;
  ok: boolean;
  error?: string;
  path?: string;
  files?: string[];
};

type WebViewBridge = {
  postMessage: (message: unknown) => void;
  addEventListener: (type: 'message', handler: (event: { data: unknown }) => void) => void;
};

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
  }
}

const pending = new Map<string, (reply: HostReply) => void>();
let counter = 0;
let listening = false;

function ensureListener(bridge: WebViewBridge): void {
  if (listening) {
    return;
  }

  bridge.addEventListener('message', (event) => {
    const reply = event.data as HostReply;
    if (!reply?.id) {
      return;
    }

    const resolve = pending.get(reply.id);
    if (resolve) {
      pending.delete(reply.id);
      resolve(reply);
    }
  });

  listening = true;
}

export function isDesktop(): boolean {
  return Boolean(window.chrome?.webview);
}

/**
 * 超时。
 *
 * 普通动作 15 秒足够；但「选文件 / 选目录」是人在操作，挑两分钟很正常 ——
 * 之前统一 15 秒，超时后 promise 已经被拒，对话框关掉时的回包就没人接，
 * 表现正是「选完文件点打开没反应」。这两类给 10 分钟。
 */
/**
 * 宿主返回的错误码 → 中文原文，再交给 t() 翻。
 *
 * 以前宿主直接回中文（"缺少路径"），英文界面下会原样弹出来。改成语义码，
 * 翻译留在前端——宿主不必知道自己当前是什么语言。
 */
const HOST_ERRORS: Record<string, string> = {
  missing_path: '缺少路径',
  unknown_action: '未知动作',
  unknown_window_action: '未知窗口动作',
  scheme_not_allowed: '只允许 http/https',
};

function hostFailure(code: string | undefined, fallback: string): Error {
  if (!code) {
    return new Error(t(fallback));
  }

  const zh = HOST_ERRORS[code];
  // 认不出来的码多半是宿主透传的系统异常消息，原样给出，别吞掉
  return new Error(zh ? t(zh) : code);
}

const TIMEOUT_MS = 15_000;
const PICKER_TIMEOUT_MS = 10 * 60_000;

export function host(
  action: string,
  payload: Record<string, unknown> = {},
  timeoutMs: number = TIMEOUT_MS,
): Promise<HostReply> {
  const bridge = window.chrome?.webview;
  if (!bridge) {
    return Promise.reject(new Error(t('只能在桌面窗口里使用')));
  }

  ensureListener(bridge);

  return new Promise<HostReply>((resolve, reject) => {
    const id = String(++counter);
    pending.set(id, resolve);

    try {
      bridge.postMessage({ id, action, ...payload });
    } catch (caught) {
      pending.delete(id);
      reject(caught instanceof Error ? caught : new Error(String(caught)));
      return;
    }

    window.setTimeout(() => {
      if (pending.delete(id)) {
        reject(new Error(t('桌面端没有响应')));
      }
    }, timeoutMs);
  });
}

/** 在资源管理器里打开目录 / 定位文件。 */
export async function openPath(path: string): Promise<void> {
  const reply = await host('openPath', { path });
  if (!reply.ok) {
    throw hostFailure(reply.error, '打不开这个路径');
  }
}

/** 用系统默认程序打开文件（等于在资源管理器里双击）。 */
export async function openFile(path: string): Promise<void> {
  const reply = await host('openFile', { path });
  if (!reply.ok) {
    throw hostFailure(reply.error, '打不开这个文件');
  }
}

/** 选文件，返回绝对路径；用户取消时返回空数组。 */
export async function pickFiles(title = t('选择要发送的文件')): Promise<string[]> {
  const reply = await host('pickFiles', { title }, PICKER_TIMEOUT_MS);
  if (!reply.ok) {
    if (reply.error === 'cancelled') {
      return [];
    }

    throw hostFailure(reply.error, '选文件失败');
  }

  return reply.files ?? [];
}

/** 选目录，返回绝对路径；用户取消时返回 null。 */
export async function pickFolder(title = t('选择目录')): Promise<string | null> {
  const reply = await host('pickFolder', { title }, PICKER_TIMEOUT_MS);
  if (!reply.ok) {
    if (reply.error === 'cancelled') {
      return null;
    }

    throw hostFailure(reply.error, '选目录失败');
  }

  return reply.path ?? null;
}

export async function copyText(text: string): Promise<void> {
  const reply = await host('copyText', { text });
  if (!reply.ok) {
    throw hostFailure(reply.error, '复制失败');
  }
}

export async function windowAction(value: 'minimize' | 'maximize' | 'close'): Promise<void> {
  await host('window', { value });
}
