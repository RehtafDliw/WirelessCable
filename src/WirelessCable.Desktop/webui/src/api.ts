/**
 * 本机接口客户端。
 *
 * 页面由自己的 Kestrel 以 /app 托管，接口挂在 /app/api 下 —— 同源，
 * 不需要 CORS；身份靠服务端注入的 `window.__WC_LOCAL__`（loopback 才注入）。
 *
 * 任何一步失败都抛错，由页面显示"读不到"，绝不回落到假数据：
 * 界面上出现的每个数字都必须来自库里的真实状态。
 */

import { errorText } from './i18n';

declare global {
  interface Window {
    __WC_LOCAL__?: string;
  }
}

const BASE = '/app/api';

function token(): string {
  return window.__WC_LOCAL__ ?? '';
}

/** 服务端回来的失败。带 `code`，调用方可以按错误码分支处理。 */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly code?: string,
    readonly status?: number,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(BASE + path, {
    ...init,
    headers: {
      'X-WC-Local': token(),
      ...(init?.body ? { 'Content-Type': 'application/json' } : null),
      ...(init?.headers ?? null),
    },
  });

  if (!response.ok) {
    let message = `HTTP ${response.status}`;
    let code: string | undefined;
    try {
      const body = (await response.json()) as { message?: string; code?: string };
      code = body.code;
      message = errorText(body.code, body.message ?? body.code ?? message);
    } catch {
      // 响应不是 JSON：保留状态码
    }

    // 光有文字不够：调用方有时要按错误码分支
    // （例如"对面已取消"要弹删除确认，而不是弹一句红字）。
    throw new ApiError(message, code, response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const api = {
  get: <T,>(path: string) => request<T>(path),
  post: <T,>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }),
  del: <T,>(path: string) => request<T>(path, { method: 'DELETE' }),
};

/**
 * 下载一个二进制响应（诊断包）。
 *
 * 不能走 `api.get`：那套统一按 JSON 解析，zip 会直接抛解析错误。
 * 文件名优先用服务端的 Content-Disposition（带时间戳，连着导两次不会覆盖），
 * 取不到才用兜底名。
 */
export async function download(path: string, fallbackName: string): Promise<void> {
  const response = await fetch(BASE + path, { headers: { 'X-WC-Local': token() } });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename="?([^";]+)"?/.exec(disposition);
  const blob = await response.blob();

  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = match?.[1] ?? fallbackName;
  anchor.click();
  // 立刻释放：blob 已经交给下载器，留着只会占内存
  URL.revokeObjectURL(url);
}

/* ===================== 数据形状（与服务端匿名对象对齐） ===================== */

export type Status = {
  version: string;
  state: string;
  port: number | null;
  addresses: string[];
  fingerprint: string | null;
  machineName: string;
  startedAtUnixMs: number;
  uptimeSeconds: number;
  protocolVersion: number;
  databaseReady: boolean;
  lastError: string | null;
  receivedDirectory: string;
};

export type PairedDevice = {
  deviceId: string;
  name: string;
  platform: string;
  online: boolean;
  lastSeenUnixMs: number;
  pairedAtUnixMs: number;
  machineAccess: number;
  sessionPaused: boolean;
  revoked: boolean;
};

export type PendingPairing = {
  requestId: string;
  deviceName: string;
  platform: string;
  code: string;
  createdAtUnixMs: number;
  expiresAtUnixMs: number;
};

export type DevicesResponse = {
  paired: PairedDevice[];
  pending: PendingPairing[];
  stats: { online: number; paired: number; directories: number; grants: number };
};

export type TransferDto = {
  taskId: string;
  fileName: string;
  direction: 'upload' | 'download';
  purpose: string;
  deviceName: string;
  totalBytes: number;
  transferredBytes: number;
  state: string;
  errorMessage: string | null;
  localPath: string | null;
  /**
   * 落点相对路径（'/' 分隔）。整文件夹下载时形如 `相册/1.jpg`，
   * 界面据此把同一目录的几百条任务收成一行——这些任务没有 batchId
   * （它们是逐条作业创建的，服务端只对批量接口写批次）。
   */
  relativePath?: string;
  /** 整文件夹传输的分组键：同一批里的任务共用一个值（单文件任务为空）。 */
  batchId?: string | null;
  /** 批次对应的文件夹名（收成一行时显示这个）。 */
  batchName?: string | null;
  /**
   * 这个落点目录下还有多少作业没跑完（pending/paused/running）。
   *
   * 整文件夹是逐条作业下发的，暂停时队列里压着的文件**还没有传输任务**；
   * 只看任务列表会以为这一批已经结束，界面几秒后把它扔进「已完成」。
   */
  queuedJobs?: number;
  /** 其中被暂停的作业数：决定这一行要不要给「继续」。 */
  pausedJobs?: number;
  createdAtUnixMs: number;
  updatedAtUnixMs: number;
};

export type RootGrant = {
  deviceId: string;
  deviceName: string;
  permissions: number;
  permissionNames: string[];
};

export type RootDto = {
  directoryId: string;
  name: string;
  path: string;
  createdAtUnixMs: number;
  grants: RootGrant[];
};

export type SettingsSnapshot = {
  version: string;
  machineName: string;
  port: number | null;
  addresses: string[];
  fingerprint: string | null;
  protocolVersion: number;
  pairedDevices: number;
  receivedDirectory: string;
  maxActiveTransfers: number;
  chunkSize: number;
  /** 出配对码时是否开电脑热点（界面上的「用热点直连」，默认 false，记上次的选择）。 */
  hotspotOnPairing: boolean;
};

/** 电脑亮码：配对二维码 + 热点兜底信息。 */
export type PairingQr = {
  ok: boolean;
  message?: string;
  pngBase64?: string;
  text?: string;
  expiresAtUnixMs?: number;
  hotspot?: string | null;
  /** 热点的 SSID / 密码：界面按语言自己拼文案，不用服务端那句中文。 */
  hotspotSsid?: string | null;
  hotspotPassphrase?: string | null;
  hotspotAddress?: string | null;
  hotspotError?: string | null;
  /** 这次出码有没有尝试开热点（等于本次请求里的选择）。 */
  hotspotPreferred?: boolean;
};
