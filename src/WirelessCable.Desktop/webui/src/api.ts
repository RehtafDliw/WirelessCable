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
    try {
      const body = (await response.json()) as { message?: string; code?: string };
      message = errorText(body.code, body.message ?? body.code ?? message);
    } catch {
      // 响应不是 JSON：保留状态码
    }

    throw new Error(message);
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
};

/** 电脑亮码：配对二维码 + 热点兜底信息。 */
export type PairingQr = {
  ok: boolean;
  message?: string;
  pngBase64?: string;
  text?: string;
  expiresAtUnixMs?: number;
  hotspot?: string | null;
  hotspotAddress?: string | null;
  hotspotError?: string | null;
};
