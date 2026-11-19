import { useEffect, useMemo, useRef, useState } from 'react';
import { ACCENTS, Badge, Button, Dialog, Stat, useOink } from 'oink-ui';
import { Icon } from './Icon';
import {
  api,
  type DevicesResponse,
  type PairedDevice,
  type PairingQr,
  type PendingPairing,
  type RootDto,
  type SettingsSnapshot,
  type Status,
  type TransferDto,
} from './api';
import { useApi } from './useApi';
import { t } from './i18n';
import {
  copyText,
  isDesktop,
  openFile,
  openPath,
  pickFiles,
  pickFolder,
} from './host';
import {
  directionLabel,
  formatAgo,
  formatBytes,
  formatClock,
  formatDuration,
  formatSpeed,
  isActive,
  isInFlight,
  isSent,
  percentOf,
  shortFingerprint,
  stateLabel,
} from './format';

/* ===================== 通用 ===================== */

/**
 * 键盘事件是不是发生在"正在输入文字"的控件里。
 *
 * 勾选框、按钮拿到焦点时也必须让 Ctrl+A / Delete 生效 —— 用户点过全选框之后
 * 快捷键就失灵，正是之前"鼠标快捷键不能用"的原因。
 */
function isTypingTarget(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null;
  if (!element) {
    return false;
  }

  if (element.isContentEditable || element.tagName === 'TEXTAREA') {
    return true;
  }

  if (element.tagName !== 'INPUT') {
    return false;
  }

  const type = (element as HTMLInputElement).type;
  return !['checkbox', 'radio', 'button', 'submit', 'reset', 'file'].includes(type);
}

/** 一次动作的状态：忙碌中 / 一句结果。失败也走这里，界面不会静默。 */
function useAction() {
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  // 同一条消息连续出现两次时，Toast 靠这个时间戳重新计时
  const [noticeAt, setNoticeAt] = useState(0);

  const notify = (text: string | null) => {
    setNotice(text);
    setNoticeAt(Date.now());
  };

  const run = async (task: () => Promise<unknown>, success?: string) => {
    setBusy(true);
    setNotice(null);
    try {
      await task();
      if (success) {
        notify(success);
      }
    } catch (caught) {
      notify(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };

  return { busy, notice, noticeAt, setNotice: notify, run };
}

function Notice({ text }: { text: string | null }) {
  if (!text) {
    return null;
  }

  return (
    <div className="wc-info" style={{ marginTop: 12 }}>
      <div className="wc-info__row">
        <span className="wc-info__value wc-info__value--muted">{text}</span>
      </div>
    </div>
  );
}

function Empty({ text }: { text: string }) {
  return (
    <div className="wc-empty">
      <Icon name="file" size={34} />
      <div style={{ font: '600 14px/1.4 var(--ok-font)', color: 'var(--ok-text-2)' }}>{text}</div>
    </div>
  );
}

/**
 * 传输速度。
 *
 * 服务端下发的是累计字节，没有速率字段；这里用连续两次轮询的差值算，
 * 4 秒没有新样本就归零 —— 卡住的传输不该一直显示"6 MB/s"。
 */
function useSpeeds(items: TransferDto[]): Record<string, number> {
  const samples = useRef(new Map<string, { bytes: number; at: number }>());

  return useMemo(() => {
    const now = Date.now();
    const result: Record<string, number> = {};

    for (const item of items) {
      const previous = samples.current.get(item.taskId);
      if (previous && now - previous.at > 200 && item.transferredBytes >= previous.bytes) {
        const speed = ((item.transferredBytes - previous.bytes) * 1000) / (now - previous.at);
        result[item.taskId] = now - previous.at > 4000 ? 0 : speed;
      }

      samples.current.set(item.taskId, { bytes: item.transferredBytes, at: now });
    }

    return result;
  }, [items]);
}

/* ===================== 首页 ===================== */

export function HomePage({
  onNavigate,
  onShowQr,
  onVisit,
  autoSend = 0,
}: {
  onNavigate: (k: string) => void;
  onShowQr: () => void;
  /** 打开某台手机的文件浏览。 */
  onVisit: (deviceId: string) => void;
  /** 排障：?sendtest=N 自动连发 N 次，复现「第二次选文件没反应」的路径 */
  autoSend?: number;
}) {
  const status = useApi<Status>('/status', 3000);
  const devices = useApi<DevicesResponse>('/devices', 3000);
  const transfers = useApi<TransferDto[]>('/transfers?limit=100', 2000);
  const action = useAction();
  const [managing, setManaging] = useState<PairedDevice | null>(null);
  // 多台在线时，选完文件先挂在这里等用户选设备（不确定目标就不发）
  const [pendingSend, setPendingSend] = useState<string[] | null>(null);

  const rows = transfers.data ?? [];
  const paired = devices.data?.paired ?? [];
  const online = paired.filter((d) => d.online && !d.sessionPaused);

  // 断开/连接是会话级动作：配对、令牌、目录授权都保留，点「连接」即可恢复
  const toggleSession = (device: PairedDevice) =>
    action.run(async () => {
      await api.post(`/devices/${device.deviceId}/session`, { paused: !device.sessionPaused });
      devices.reload();
      return device.sessionPaused ? t(`已连接 ${device.name}`) : t(`已断开 ${device.name}`);
    }, undefined);

  const sendTo = async (target: PairedDevice, paths: string[]): Promise<string> => {
    // 对话框关掉到任务建好之间有一小段空窗：先给一句，避免"点完没反应"的错觉
    action.setNotice(t(`已选择 ${paths.length} 个文件，正在创建任务…`));

    const result = await api.post<{ created: string[]; failed: string[] }>('/transfers/send', {
      deviceId: target.deviceId,
      paths,
    });

    if (result.failed.length > 0) {
      throw new Error(result.failed[0]);
    }

    return t(`已交给 ${target.name}：${result.created.length} 个文件`);
  };

  const sendFiles = () =>
    action.run(async () => {
      if (online.length === 0) {
        throw new Error(t('没有在线设备，先在手机上连上这台电脑'));
      }

      const paths = await pickFiles();
      if (paths.length === 0) {
        return;
      }

      // 只有一台在线就直接发，不打断；多台必须让用户自己选——
      // 原来写的是 online[0]，两台手机都连着时会悄悄发给"列表里第一台"
      if (online.length === 1) {
        return sendTo(online[0], paths);
      }

      setPendingSend(paths);
      return t(`选了 ${paths.length} 个文件，选择要发给哪台设备`);
    }, undefined);

  // 排障用：自动按顺序触发 N 次发送（每次都会弹系统选文件框）。
  // 必须等设备列表到位：刚挂载时 devices 还是 null，会误判成"没有在线设备"。
  const autoStarted = useRef(false);
  useEffect(() => {
    if (autoSend <= 0 || !isDesktop() || autoStarted.current || online.length === 0) {
      return;
    }

    autoStarted.current = true;
    let cancelled = false;
    const sequence = async () => {
      for (let i = 0; i < autoSend && !cancelled; i++) {
        await sendFiles();
        await new Promise((resolve) => window.setTimeout(resolve, 1200));
      }
    };

    void sequence();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoSend, online.length]);

  return (
    <div className="wc-page wc-home">
      {/* 顶行：已连接设备 + 连接信息长磁贴 */}
      <div className="wc-home__top">
        <Stat
          label={t('已连接设备')}
          value={devices.data?.stats.online ?? 0}
          hint={online.length > 0 ? online.map((d) => d.name).join(' · ') : t('暂无在线设备')}
          icon={<Icon name="users" size={16} />}
        />

        <div className="wc-infotile">
          <div className="wc-infotile__cell">
            <span>{t('服务状态')}</span>
            <b style={{ color: status.data?.state === 'Running' ? 'var(--ok-success-text)' : 'var(--ok-danger-text)' }}>
              {status.data ? (status.data.state === 'Running' ? t('运行中') : status.data.state) : (status.error ?? t('读取中'))}
            </b>
          </div>
          <div className="wc-infotile__cell">
            <span>{t('监听地址')}</span>
            <b className="mono">
              {status.data?.addresses[0] ?? '—'}
              {status.data?.port ? `:${status.data.port}` : ''}
            </b>
          </div>
          <div className="wc-infotile__cell wc-infotile__cell--wide" title={status.data?.receivedDirectory}>
            <span>{t('接收目录')}</span>
            <b className="mono">{status.data?.receivedDirectory ?? '—'}</b>
          </div>
          <div className="wc-infotile__cell">
            <span>{t('证书指纹')}</span>
            <b className="mono" style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              {shortFingerprint(status.data?.fingerprint ?? null)}
              <button
                className="wc-copy"
                aria-label={t('复制证书指纹')}
                onClick={() => action.run(() => copyText(status.data?.fingerprint ?? ''), t('已复制证书指纹'))}
              >
                <Icon name="copy" size={13} />
              </button>
            </b>
          </div>
          <div className="wc-infotile__cell">
            <span>{t('版本')}</span>
            <b className="mono">{status.data?.version ?? '—'}</b>
          </div>
        </div>
      </div>

      {/* 主体：左「我的设备」/ 右「快速操作 + 最近活动」 */}
      <div className="wc-home__body">
        <section className="wc-section wc-home__devices">
          <div className="wc-section__head">
            <h2 className="wc-section__title">{t('我的设备')}</h2>
            <span className="wc-section__hint">{t(`已配对 · ${paired.length} 台`)}</span>
            <div className="wc-section__action">
              <Button variant="ghost" size="sm" onClick={() => onNavigate('devices')}>
                {t('全部设备')}
              </Button>
            </div>
          </div>

          {paired.length === 0 ? (
            <Empty text={t('还没有配对设备')} />
          ) : (
            <div className="wc-list">
              {paired.map((device) => (
                <DeviceRow
                  key={device.deviceId}
                  device={device}
                  busy={action.busy}
                  onVisit={() => onVisit(device.deviceId)}
                  onToggleSession={() => void toggleSession(device)}
                  onManage={() => setManaging(device)}
                />
              ))}
            </div>
          )}
        </section>

        <div className="wc-home__side">
          <section className="wc-section wc-home__actions">
            <div className="wc-section__head">
              <h2 className="wc-section__title">{t('快速操作')}</h2>
            </div>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <Button variant="accent" size="sm" style={{ whiteSpace: 'nowrap' }} disabled={!isDesktop() || action.busy} onClick={sendFiles}>
                <Icon name="upload" size={16} />
                {t('发送文件')}
              </Button>
              <Button variant="soft" size="sm" style={{ whiteSpace: 'nowrap' }} onClick={onShowQr}>
                <Icon name="qr" size={16} />
                {t('配对码')}
              </Button>
              <Button
                variant="soft"
                size="sm"
                style={{ whiteSpace: 'nowrap' }}
                disabled={!isDesktop() || !status.data?.receivedDirectory}
                onClick={() => action.run(() => openPath(status.data?.receivedDirectory ?? ''), t('已在资源管理器打开'))}
              >
                <Icon name="folderOpen" size={16} />
                {t('打开接收目录')}
              </Button>
            </div>
          </section>

          <section className="wc-section wc-home__recent">
            <div className="wc-section__head">
              <h2 className="wc-section__title">{t('最近活动')}</h2>
              <span className="wc-section__hint">{t(`最近 ${Math.min(5, rows.length)} 条`)}</span>
            </div>
            {rows.length === 0 ? (
              <Empty text={t('还没有传输记录')} />
            ) : (
              <div className="wc-timeline">
                {rows.slice(0, 5).map((item) => (
                  <div className="wc-tl" data-tone={toneOf(item.state)} key={item.taskId}>
                    <div className="wc-tl__dot">
                      <Icon name={iconOf(item.state)} size={14} />
                    </div>
                    <div className="wc-tl__body">
                      <div className="wc-tl__title">
                        {item.fileName} · {stateLabel(item.state)}
                      </div>
                      <div className="wc-tl__meta">
                        {item.deviceName || t('已移除的设备')} · {directionLabel(item.direction)} ·{' '}
                        {formatBytes(item.totalBytes)}
                      </div>
                    </div>
                    <div className="wc-tl__time">{formatClock(item.updatedAtUnixMs)}</div>
                  </div>
                ))}
              </div>
            )}
          </section>
        </div>
      </div>

      <Dialog
        open={pendingSend !== null}
        onOpenChange={(open) => {
          if (!open) setPendingSend(null);
        }}
        size={420}
        icon={<Icon name="phone" />}
        tone="accent"
        title={t('发送到哪台设备')}
        description={pendingSend ? t(`已选 ${pendingSend.length} 个文件`) : ''}
      >
        <div style={{ display: 'grid', gap: 8 }}>
          {online.map((device) => (
            <Button
              key={device.deviceId}
              variant="soft"
              onClick={() => {
                const paths = pendingSend ?? [];
                setPendingSend(null);
                void action.run(() => sendTo(device, paths), undefined);
              }}
            >
              {device.name}
            </Button>
          ))}
        </div>
      </Dialog>

      <Toast text={action.notice} at={action.noticeAt} />

      <ManageDeviceDialog
        device={managing}
        onClose={() => setManaging(null)}
        onDone={(message) => {
          setManaging(null);
          action.setNotice(message);
          devices.reload();
        }}
      />
    </div>
  );
}

/**
 * 一句话提示，3 秒自动消失。
 *
 * 之前是页面里常驻的一行（"已选择 3 个文件…"会一直挂着），既占位置又像界面卡住了。
 */
function Toast({ text, at }: { text: string | null; at: number }) {
  const [shown, setShown] = useState(false);

  useEffect(() => {
    if (!text) {
      setShown(false);
      return;
    }

    setShown(true);
    const timer = window.setTimeout(() => setShown(false), 3000);
    return () => window.clearTimeout(timer);
  }, [text, at]);

  if (!text || !shown) {
    return null;
  }

  return (
    <div className="wc-toast" role="status">
      <Icon name="info" size={15} />
      <span>{text}</span>
    </div>
  );
}

/**
 * 电脑亮码：手机扫这张图完成配对。
 *
 * 二维码里带一次性随机数、局域网地址、端口和证书指纹，不含长期密钥；
 * 每次打开（或点「重新生成」）都换一张，旧码随即失效。
 */
export function PairingQrDialog({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (value: boolean) => void;
}) {
  const [qr, setQr] = useState<PairingQr | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [generation, setGeneration] = useState(0);
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!open) {
      return;
    }

    let alive = true;
    setBusy(true);
    setError(null);
    setQr(null);

    api
      .post<PairingQr>('/pairing/qr')
      .then((result) => {
        if (!alive) {
          return;
        }

        if (!result.ok || !result.pngBase64) {
          setError(result.message ?? t('出码失败'));
          return;
        }

        setQr(result);
        setNow(Date.now());
      })
      .catch((caught: unknown) => {
        if (alive) {
          setError(caught instanceof Error ? caught.message : String(caught));
        }
      })
      .finally(() => {
        if (alive) {
          setBusy(false);
        }
      });

    return () => {
      alive = false;
    };
  }, [open, generation]);

  // 倒计时。只在对话框开着时跑，关掉就停，别让页面每秒重渲染。
  useEffect(() => {
    if (!open) {
      return;
    }

    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [open]);

  const remain = qr?.expiresAtUnixMs ? Math.max(0, Math.ceil((qr.expiresAtUnixMs - now) / 1000)) : 0;
  const expired = Boolean(qr) && remain <= 0;

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      size={420}
      tone="accent"
      icon={<Icon name="qr" />}
      title={t('手机扫码配对')}
      description={t('手机打开 WirelessCable → 首页 → 扫码配对。')}
      actions={
        <>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            {t('关闭')}
          </Button>
          <Button variant="accent" disabled={busy} onClick={() => setGeneration((n) => n + 1)}>
            {busy ? t('生成中…') : qr ? t('重新生成') : t('生成')}
          </Button>
        </>
      }
    >
      <div style={{ display: 'grid', gap: 10, justifyItems: 'center', paddingTop: 2 }}>
        <div
          style={{
            position: 'relative',
            display: 'grid',
            placeItems: 'center',
            padding: 12,
            borderRadius: 14,
            background: '#fff',
            boxShadow: 'inset 0 0 0 1px var(--ok-line)',
            minWidth: 232,
            minHeight: 232,
          }}
        >
          {qr?.pngBase64 ? (
            <img
              src={`data:image/png;base64,${qr.pngBase64}`}
              alt={t('配对二维码')}
              width={208}
              height={208}
              style={{ display: 'block', opacity: expired ? 0.18 : 1, transition: 'opacity 200ms' }}
            />
          ) : (
            <span style={{ font: '450 12.5px/1.5 var(--ok-font)', color: '#666' }}>
              {busy ? t('正在准备热点…') : (error ?? t('未生成'))}
            </span>
          )}

          {expired && (
            <span
              style={{
                position: 'absolute',
                font: '600 13px/1.4 var(--ok-font)',
                color: '#333',
              }}
            >
              {t('已过期，请重新生成')}
            </span>
          )}
        </div>

        <div style={{ display: 'grid', gap: 1, justifyItems: 'center' }}>
          <span
            style={{
              font: '500 12.5px/1.5 var(--ok-font)',
              color: expired ? 'var(--ok-danger-text)' : 'var(--ok-text-2)',
              fontVariantNumeric: 'tabular-nums',
            }}
          >
            {qr ? (expired ? t('已过期') : t(`有效期 ${remain} 秒`)) : ' '}
          </span>
          {qr?.hotspot && (
            <span style={{ font: '450 12px/1.5 var(--ok-font)', color: 'var(--ok-text-3)' }}>
              {qr.hotspot}
            </span>
          )}
          {qr?.hotspotError && (
            <span style={{ font: '450 12px/1.5 var(--ok-font)', color: 'var(--ok-text-3)' }}>
              {qr.hotspotError}
            </span>
          )}
        </div>

        {error && (
          <span style={{ font: '450 12px/1.5 var(--ok-font)', color: 'var(--ok-danger-text)' }}>
            {error}
          </span>
        )}
      </div>
    </Dialog>
  );
}

/** 一台设备一行：名字 + 平台/最后活动 + 状态胶囊 + 管理。 */
function DeviceRow({
  device,
  busy,
  onVisit,
  onToggleSession,
  onManage,
}: {
  device: PairedDevice;
  busy: boolean;
  /** 打开这台手机的文件浏览（首页「访问」）。 */
  onVisit: () => void;
  /** 断开 / 连接（会话级，配对与授权都保留）。 */
  onToggleSession: () => void;
  /** 打开这台设备的授权管理弹窗，不再跳到设备管理页。 */
  onManage: () => void;
}) {
  const online = device.online && !device.sessionPaused;

  return (
    <div className="wc-row">
      <div className="wc-row__icon">
        <Icon name={device.platform.toLowerCase().includes('android') ? 'phone' : 'laptop'} />
      </div>
      <div style={{ minWidth: 0 }}>
        <div className="wc-row__name">{device.name}</div>
        <div className="wc-row__meta">
          {device.platform} ·{' '}
          {device.sessionPaused
            ? t('已断开连接')
            : online
              ? t(`最后活动 ${formatAgo(device.lastSeenUnixMs)}`)
              : t(`离线 · 最后活动 ${formatAgo(device.lastSeenUnixMs)}`)}
        </div>
      </div>
      <div className="wc-row__right">
        {device.sessionPaused ? (
          <Badge tone="warning">{t('已断开')}</Badge>
        ) : online ? (
          <Badge tone="success" dot>
            {t('在线')}
          </Badge>
        ) : (
          <Badge tone="neutral">{t('离线')}</Badge>
        )}
        <Button variant="ghost" size="sm" disabled={!online} onClick={onVisit}>
          {t('访问')}
        </Button>
        <Button variant="ghost" size="sm" disabled={busy || (!online && !device.sessionPaused)} onClick={onToggleSession}>
          {device.sessionPaused ? t('连接') : t('断开')}
        </Button>
        <Button variant="ghost" size="sm" onClick={onManage}>
          {t('管理')}
        </Button>
      </div>
    </div>
  );
}

function toneOf(state: string): string {
  switch (state) {
    case 'completed':
      return 'success';
    case 'failed':
      return 'danger';
    case 'cancelled':
      return 'neutral';
    default:
      return 'accent';
  }
}

function iconOf(state: string): string {
  switch (state) {
    case 'completed':
      return 'check';
    case 'failed':
      return 'alert';
    case 'cancelled':
      return 'stop';
    default:
      return 'send';
  }
}

function accessLabel(level: number): string {
  switch (level) {
    case 0:
      return t('不可访问');
    case 1:
      return t('只读');
    case 2:
      return t('读写（不能删）');
    default:
      return t('读写 + 删除');
  }
}

/* ===================== 设备管理 ===================== */

export function DevicesPage() {
  const devices = useApi<DevicesResponse>('/devices', 2000);
  const [managing, setManaging] = useState<PairedDevice | null>(null);
  const [qrOpen, setQrOpen] = useState(false);
  const action = useAction();

  const paired = devices.data?.paired ?? [];
  const pending = devices.data?.pending ?? [];
  const stats = devices.data?.stats;

  return (
    <div className="wc-page">
      <div className="wc-stats">
        <Stat label={t('在线设备')} value={stats?.online ?? 0} hint={t('30 秒内有心跳')} icon={<Icon name="wifi" size={16} />} />
        <Stat label={t('已配对')} value={stats?.paired ?? 0} hint={t('本机记录')} icon={<Icon name="users" size={16} />} />
        <Stat
          label={t('授权目录')}
          value={stats?.directories ?? 0}
          hint={t(`${stats?.grants ?? 0} 条设备授权`)}
          icon={<Icon name="folder" size={16} />}
        />
        <Stat label={t('待授权')} value={pending.length} hint={pending[0]?.deviceName ?? t('没有请求')} icon={<Icon name="shield" size={16} />} />
      </div>

      <div className="wc-section__head">
        <h2 className="wc-section__title">{t('已配对设备')}</h2>
        <span className="wc-section__hint">{t(`已配对 ${paired.length} 台记录`)}</span>
        <div className="wc-section__action">
          <Button variant="soft" size="sm" onClick={() => setQrOpen(true)}>
            <Icon name="qr" size={16} />
            {t('扫码配对')}
          </Button>
        </div>
      </div>

      {paired.length === 0 ? (
        <Empty text={t('还没有配对过设备')} />
      ) : (
        <div className="wc-list">
          {paired.map((device, index) => (
            <div className="wc-row wc-anim-item" key={device.deviceId} style={{ animationDelay: `${index * 55}ms` }}>
              <div className="wc-row__icon">
                <Icon name={device.platform.toLowerCase().includes('android') ? 'phone' : 'laptop'} />
              </div>
              <div style={{ minWidth: 0 }}>
                <div className="wc-row__name">{device.name}</div>
                <div className="wc-row__meta">
                  {device.platform}{t(' · 最后活动 ')}{formatAgo(device.lastSeenUnixMs)} · {accessLabel(device.machineAccess)}
                </div>
              </div>
              <div className="wc-row__right">
                {device.sessionPaused ? (
                  <Badge tone="warning">{t('已断开')}</Badge>
                ) : device.online ? (
                  <Badge tone="success" dot>
                    {t('在线')}
                  </Badge>
                ) : (
                  <Badge tone="neutral">{t('离线')}</Badge>
                )}
                <Button variant="ghost" size="sm" onClick={() => setManaging(device)}>
                  {t('管理')}
                </Button>
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="wc-section__head" style={{ marginTop: 26 }}>
        <h2 className="wc-section__title">{t('待授权请求')}</h2>
        <span className="wc-section__hint">{t(`${pending.length} 个`)}</span>
      </div>

      {pending.length === 0 ? (
        <Empty text={t('没有等待授权的设备')} />
      ) : (
        <div className="wc-list">
          {pending.map((request) => (
            <div className="wc-row" key={request.requestId}>
              <div
                className="wc-row__icon"
                style={{ background: 'var(--ok-warning-soft)', color: 'var(--ok-warning-text)' }}
              >
                <Icon name="phone" />
              </div>
              <div style={{ minWidth: 0 }}>
                <div className="wc-row__name">{request.deviceName}</div>
                <div className="wc-row__meta">
                  {request.platform}{t(' · 校验码 ')}<b style={{ letterSpacing: 2 }}>{request.code}</b>
                </div>
              </div>
              <div className="wc-row__right">
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={action.busy}
                  onClick={() => action.run(() => api.post(`/pairing/${request.requestId}/reject`), t('已拒绝'))}
                >
                  {t('拒绝')}
                </Button>
                <Button
                  variant="accent"
                  size="sm"
                  disabled={action.busy}
                  onClick={() => action.run(() => api.post(`/pairing/${request.requestId}/approve`), t('已授权'))}
                >
                  {t('授权')}
                </Button>
              </div>
            </div>
          ))}
        </div>
      )}

      <Notice text={action.notice} />

      <PairingQrDialog open={qrOpen} onOpenChange={setQrOpen} />

      <ManageDeviceDialog
        device={managing}
        onClose={() => setManaging(null)}
        onDone={(message) => {
          setManaging(null);
          action.setNotice(message);
          devices.reload();
        }}
      />
    </div>
  );
}

function ManageDeviceDialog({
  device,
  onClose,
  onDone,
}: {
  device: PairedDevice | null;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const [name, setName] = useState(device?.name ?? '');
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [level, setLevel] = useState(device?.machineAccess ?? 3);

  if (!device) {
    return null;
  }

  const apply = async (path: string, body: unknown, message: string) => {
    try {
      await api.post(path, body);
      onDone(message);
    } catch (caught) {
      onDone(caught instanceof Error ? caught.message : String(caught));
    }
  };

  const remove = async () => {
    try {
      await api.del(`/devices/${device.deviceId}`);
      onDone(t(`已删除 ${device.name}，传输记录一并清空`));
    } catch (caught) {
      onDone(caught instanceof Error ? caught.message : String(caught));
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      size={480}
      icon={<Icon name="phone" />}
      title={device.name}
      description={t(`${device.platform} · 配对于 ${formatAgo(device.pairedAtUnixMs)}`)}
      actions={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t('关闭')}
          </Button>
          <Button variant="accent" onClick={() => apply(`/devices/${device.deviceId}/rename`, { name }, t('已改名'))}>
            {t('保存名称')}
          </Button>
        </>
      }
    >
      <div style={{ display: 'grid', gap: 12, marginTop: 4 }}>
        <label className="wc-field" style={{ padding: 0 }}>
          <span style={{ minWidth: 72, font: '600 13px/1.4 var(--ok-font)', color: 'var(--ok-text-2)' }}>{t('名称')}</span>
          <input
            value={name}
            onChange={(event) => setName(event.target.value)}
            style={{
              flex: 1,
              height: 36,
              padding: '0 10px',
              borderRadius: 'calc(10px * var(--ok-rk))',
              border: '1px solid var(--ok-line)',
              background: 'var(--ok-well)',
              color: 'var(--ok-text)',
              font: '450 13.5px/1.4 var(--ok-font)',
            }}
          />
        </label>

        <div className="wc-field" style={{ padding: 0, alignItems: 'flex-start' }}>
          <span style={{ minWidth: 72, font: '600 13px/1.4 var(--ok-font)', color: 'var(--ok-text-2)', paddingTop: 6 }}>
            {t('整机访问')}
          </span>
          <select
            value={level}
            onChange={(event) => {
              const next = Number(event.target.value);
              setLevel(next);
              void apply(`/devices/${device.deviceId}/machine-access`, { level: next }, t('已更新整机权限'));
            }}
            style={{
              height: 36,
              padding: '0 8px',
              borderRadius: 'calc(10px * var(--ok-rk))',
              border: '1px solid var(--ok-line)',
              background: 'var(--ok-well)',
              color: 'var(--ok-text)',
              font: '450 13.5px/1.4 var(--ok-font)',
            }}
          >
            <option value={0}>{t('不可访问')}</option>
            <option value={1}>{t('只读')}</option>
            <option value={2}>{t('读写（不能删）')}</option>
            <option value={3}>{t('读写 + 删除')}</option>
          </select>
        </div>

        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Button
            variant="soft"
            size="sm"
            onClick={() =>
              apply(
                `/devices/${device.deviceId}/session`,
                { paused: !device.sessionPaused },
                device.sessionPaused ? t('已恢复连接') : t('已断开这台设备'),
              )
            }
          >
            {device.sessionPaused ? t('恢复连接') : t('断开连接')}
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setConfirmDelete((current) => !current)}
          >
            {confirmDelete ? t('取消删除') : t('删除设备')}
          </Button>
        </div>

        {confirmDelete && (
          <div style={{ display: 'grid', gap: 8 }}>
            <div style={{ font: '450 12.5px/1.5 var(--ok-font)', color: 'var(--ok-text-3)' }}>
              {t('删除后记录与授权一并清除，需要重新配对；已接收的文件保留。')}
            </div>
            <Button variant="danger" size="sm" onClick={remove}>
              {t('确认删除')}
            </Button>
          </div>
        )}
      </div>
    </Dialog>
  );
}

/* ===================== 传输中心 ===================== */

type Filter = 'all' | 'sent' | 'received';

/**
 * 传输列表里的一行。
 *
 * `batchId` 非空表示这是"一次下载整个文件夹"展开出来的一批任务：
 * 界面上收成一行，行上的暂停/取消/删除都作用到整批。
 */
type RowGroup = {
  /** 行的稳定 key：单条用 taskId，整批用 `batch:<batchId>`。 */
  key: string;
  batchId: string | null;
  name: string;
  /** 这一行覆盖的任务；单条时只有一个。 */
  items: TransferDto[];
};

/**
 * 把任务按 `batchId` 收成行。
 *
 * 顺序按"第一次出现"的位置决定——服务端已经按创建时间排好了，
 * 这里只做合并，不重新排序（否则行会随着进度回报来回跳）。
 */
function groupTasks(rows: TransferDto[]): RowGroup[] {
  const byBatch = new Map<string, TransferDto[]>();
  rows.forEach((row) => {
    if (!row.batchId) {
      return;
    }

    const list = byBatch.get(row.batchId) ?? [];
    list.push(row);
    byBatch.set(row.batchId, list);
  });

  const emitted = new Set<string>();
  const groups: RowGroup[] = [];

  rows.forEach((row) => {
    if (!row.batchId) {
      groups.push({ key: row.taskId, batchId: null, name: row.fileName, items: [row] });
      return;
    }

    if (emitted.has(row.batchId)) {
      return;
    }

    emitted.add(row.batchId);
    groups.push({
      key: `batch:${row.batchId}`,
      batchId: row.batchId,
      name: row.batchName || t('文件夹'),
      items: byBatch.get(row.batchId) ?? [row],
    });
  });

  return groups;
}

export function TransfersPage() {
  const transfers = useApi<TransferDto[]>('/transfers?limit=100', 1500);
  const [tab, setTab] = useState<'active' | 'done'>('active');
  const [filter, setFilter] = useState<Filter>('all');
  const [selected, setSelected] = useState<string[]>([]);
  const [menu, setMenu] = useState<{ key: string; x: number; y: number } | null>(null);
  // 删除确认：`name` 非空表示这是整批（整文件夹），标题里要写出文件夹名
  const [confirming, setConfirming] = useState<{ ids: string[]; name: string | null } | null>(null);
  const askDelete = (ids: string[], name: string | null = null) => {
    if (ids.length === 0) {
      setConfirming(null);
      return;
    }

    // 选中的恰好是同一批时，确认框直接写文件夹名——用户点的是"一行"，
    // 只说"3 条记录"他不知道删的是哪个文件夹
    const picked = groupTasks(shown.filter((row) => ids.includes(row.taskId)));
    const only = picked.length === 1 && picked[0].batchId ? picked[0].name : name;
    setConfirming({ ids, name: only });
  };
  const action = useAction();

  // 服务端按 updated_at 倒序给，进度一变就换位 —— 这里按创建时间排，顺序固定。
  const rows = useMemo(
    () => [...(transfers.data ?? [])].sort((a, b) => b.createdAtUnixMs - a.createdAtUnixMs),
    [transfers.data],
  );

  const byFilter = rows.filter((row) =>
    filter === 'all' ? true : filter === 'sent' ? isSent(row.direction) : !isSent(row.direction),
  );
  const active = byFilter.filter((row) => isActive(row.state));
  const done = byFilter.filter((row) => !isActive(row.state));

  // 一次「下载整个文件夹」= 一批任务，列表里收成一行：
  // 200 个文件刷满 200 行既看不出是同一个文件夹，也没法整批暂停/整批删。
  const activeGroups = useMemo(() => groupTasks(active), [active]);
  const doneGroups = useMemo(() => groupTasks(done), [done]);
  const groups = tab === 'active' ? activeGroups : doneGroups;
  const shown = tab === 'active' ? active : done;
  const speeds = useSpeeds(shown);

  // 切页签/筛选就清空选择：否则会删到已经看不见的行
  useEffect(() => {
    setSelected([]);
    setMenu(null);
  }, [tab, filter]);

  const selectedRows = shown.filter((row) => selected.includes(row.taskId));
  // 用户点的是"一行"：已选 2 项 = 两个文件夹/两条记录，不是两个文件
  const selectedGroupCount = groups.filter((group) =>
    group.items.every((row) => selected.includes(row.taskId)),
  ).length;
  // 能删的是"不在跑"的：失败/中断/暂停/完成都可以删，服务端也只拦在跑的那几种
  const deletableIds = selectedRows
    .filter((row) => !isInFlight(row.state))
    .map((row) => row.taskId);
  const receivedFiles = selectedRows.filter(
    (row) => !isActive(row.state) && !isSent(row.direction) && Boolean(row.localPath),
  ).length;
  const allChecked =
    groups.length > 0 && groups.every((group) => group.items.every((row) => selected.includes(row.taskId)));
  const someChecked = selected.length > 0 && !allChecked;

  // 全选框的三态只能靠命令式设置
  const master = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (master.current) {
      master.current.indeterminate = someChecked;
    }
  }, [someChecked]);

  // 快捷键：Ctrl+A 全选、Delete 删除、Esc 取消选择。
  // 输入框里不抢键，否则搜索/改名时按 Delete 会误删记录。
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (isTypingTarget(event.target)) {
        return;
      }

      if (event.key === 'Escape') {
        setSelected([]);
        setMenu(null);
        return;
      }

      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
        event.preventDefault();
        setSelected(shown.map((row) => row.taskId));
        return;
      }

      if (event.key === 'Delete' || event.key === 'Backspace') {
        if (selected.length === 0) {
          return;
        }

        event.preventDefault();
        askDelete(selected);
      }
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [shown, selected]);

  /**
   * 一整行上的动作。
   *
   * 单条走 /transfers/{id}/{verb}；整批走 /transfers/batch 一次发完——
   * 200 个文件逐个发请求，光往返就要好几秒，中途失败还会只停一半。
   */
  const actGroup = (
    group: RowGroup,
    verb: 'pause' | 'resume' | 'cancel' | 'restart',
    message: string,
  ) =>
    action.run(async () => {
      if (group.batchId) {
        await api.post('/transfers/batch', {
          taskIds: group.items.map((row) => row.taskId),
          action: verb,
        });
      } else {
        await api.post(`/transfers/${group.items[0].taskId}/${verb}`);
      }

      transfers.reload();
      return message;
    }, message);

  const menuGroup = menu ? (groups.find((group) => group.key === menu.key) ?? null) : null;

  const menuItems = (group: RowGroup) => {
    const items: { label: string; danger?: boolean; disabled?: boolean; onSelect: () => void }[] = [];

    // 只有一个文件时才谈得上"定位/打开"——文件夹没有单一落点可定位
    const single = group.items.length === 1 ? group.items[0] : null;

    if (single?.localPath && isDesktop()) {
      items.push({
        label: t('在资源管理器中显示'),
        onSelect: () => action.run(() => openPath(single.localPath ?? ''), t('已定位到文件')),
      });
    }

    const groupActive = group.items.some((row) => isActive(row.state));
    const allPaused = group.items.every((row) => row.state === 'paused');
    const allStuck = group.items.every((row) => row.state === 'failed' || row.state === 'interrupted');

    if (groupActive) {
      if (allPaused) {
        items.push({
          label: group.batchId ? t('继续整批') : t('继续'),
          onSelect: () => actGroup(group, 'resume', t('已继续传输')),
        });
      } else {
        items.push({
          label: group.batchId ? t('全部暂停') : t('暂停'),
          onSelect: () => actGroup(group, 'pause', t('已暂停')),
        });
      }

      if (allStuck) {
        items.push({
          label: group.batchId ? t('整批重新开始') : t('重新开始'),
          onSelect: () => actGroup(group, 'restart', t('已重新开始')),
        });
      }

      items.push({
        label: group.batchId ? t('全部取消') : t('取消'),
        danger: true,
        onSelect: () => actGroup(group, 'cancel', t('已取消')),
      });
    }

    items.push({
      label: group.batchId ? t(`删除整批（${group.items.length} 个文件）`) : t('删除记录'),
      danger: true,
      disabled: group.items.some((row) => isInFlight(row.state)),
      onSelect: () =>
        askDelete(
          group.items.map((row) => row.taskId),
          group.batchId ? group.name : null,
        ),
    });

    return items;
  };

  return (
    <div className="wc-page">
      <div className="wc-section__head" style={{ marginBottom: 10 }}>
        <div style={{ display: 'flex', gap: 6 }}>
          {(
            [
              ['active', t(`传输中 ${activeGroups.length}`)],
              ['done', t(`已完成 ${doneGroups.length}`)],
            ] as const
          ).map(([key, label]) => (
            <button
              key={key}
              className="wc-navitem"
              data-current={tab === key}
              style={{ width: 'auto', padding: '0 12px', height: 32 }}
              onClick={() => setTab(key)}
            >
              <span>{label}</span>
            </button>
          ))}
        </div>
        <div className="wc-section__action">
          {selected.length > 0 && (
            <span style={{ font: '500 12px/1 var(--ok-font)', color: 'var(--ok-text-3)' }}>
              {/* 点的是行：整批选中时把文件数一起写出来，避免以为只选了一个 */}
              {selected.length === selectedGroupCount
                ? t(`已选 ${selectedGroupCount} 项`)
                : t(`已选 ${selectedGroupCount} 项 · ${selected.length} 个文件`)}
            </span>
          )}
          <label className="wc-checkline">
            <input
              ref={master}
              type="checkbox"
              checked={allChecked}
              onChange={(event) => setSelected(event.target.checked ? shown.map((row) => row.taskId) : [])}
            />
            {t('全选')}
          </label>
          <Button
            variant="ghost"
            size="sm"
            disabled={deletableIds.length === 0 || action.busy}
            title={deletableIds.length === 0 && selected.length > 0 ? t('进行中的任务不能删除，请先取消') : undefined}
            onClick={() => askDelete(deletableIds)}
          >
            {t('删除')}
          </Button>
          {(
            [
              ['all', t('全部')],
              ['sent', t('我发送的')],
              ['received', t('我收到的')],
            ] as const
          ).map(([key, label]) => (
            <Button
              key={key}
              variant="ghost"
              size="sm"
              onClick={() => setFilter(key)}
              style={{ opacity: filter === key ? 1 : 0.55 }}
            >
              {label}
            </Button>
          ))}
        </div>
      </div>

      {transfers.error && <Notice text={t(`读取失败：${transfers.error}`)} />}

      {groups.length === 0 ? (
        <Empty text={tab === 'active' ? t('没有进行中的传输') : t('还没有完成的传输')} />
      ) : (
        <div className="wc-list">
          {groups.map((group, index) => {
            const lead = group.items[0];
            const files = group.items.length;
            const total = group.items.reduce((sum, row) => sum + row.totalBytes, 0);
            const doneBytes = group.items.reduce((sum, row) => sum + row.transferredBytes, 0);
            const speed = group.items.reduce((sum, row) => sum + (speeds[row.taskId] ?? 0), 0);
            const completed = group.items.filter((row) => row.state === 'completed').length;
            const allPaused = group.items.every((row) => row.state === 'paused');
            const allStuck = group.items.every(
              (row) => row.state === 'failed' || row.state === 'interrupted',
            );
            const allDone = group.items.every((row) => row.state === 'completed');
            const checked = group.items.every((row) => selected.includes(row.taskId));
            // 文件夹里只有一个文件时，"打开/定位"仍然有意义
            const single = files === 1 ? group.items[0] : null;
            const pct = percentOf(doneBytes, total);

            return (
              <div
                className="wc-row wc-row--selectable wc-anim-item"
                key={group.key}
                style={{ animationDelay: `${index * 40}ms` }}
                onDoubleClick={() => {
                  // 双击 = 用默认应用打开；整批没有单一落点，只在单文件行生效
                  if (single?.localPath && isDesktop()) {
                    action.run(() => openFile(single.localPath ?? ''), t('已打开'));
                  }
                }}
                onContextMenu={(event) => {
                  event.preventDefault();
                  // 右键非选中行时只选它：跟着旧界面的习惯，避免误删一大片
                  if (!checked) {
                    setSelected(group.items.map((row) => row.taskId));
                  }

                  setMenu({ key: group.key, x: event.clientX, y: event.clientY });
                }}
              >
                <input
                  type="checkbox"
                  aria-label={t(`选择 ${group.name}`)}
                  checked={checked}
                  onChange={(event) =>
                    setSelected((current) => {
                      const ids = group.items.map((row) => row.taskId);
                      return event.target.checked
                        ? Array.from(new Set([...current, ...ids]))
                        : current.filter((id) => !ids.includes(id));
                    })
                  }
                />

                {tab === 'active' ? (
                  <Ring pct={pct} />
                ) : (
                  <div
                    className="wc-row__icon"
                    style={{
                      background: allDone ? 'var(--ok-success-soft)' : 'var(--ok-danger-soft)',
                      color: allDone ? 'var(--ok-success-text)' : 'var(--ok-danger-text)',
                    }}
                  >
                    <Icon name={allDone ? 'check' : iconOf(lead.state)} />
                  </div>
                )}

                <div style={{ minWidth: 0 }}>
                  <div className="wc-row__name">
                    {group.name}
                    {group.batchId && (
                      <span
                        style={{
                          marginLeft: 6,
                          font: '400 12px/1 var(--ok-font)',
                          color: 'var(--ok-text-3)',
                        }}
                      >
                        {t(`${files} 个文件`)}
                      </span>
                    )}
                  </div>
                  <div className="wc-row__meta">
                    {lead.deviceName || t('已移除的设备')} · {directionLabel(lead.direction)} ·{' '}
                    {group.batchId ? t(`已完成 ${completed}/${files} · `) : ''}
                    {formatBytes(doneBytes)} / {formatBytes(total)}
                    {tab === 'active' && t(` · ${allPaused ? t('已暂停') : allStuck ? t('未完成') : t('传输中')}`)}
                  </div>
                  {tab === 'active' ? (
                    <div className="wc-progress" style={{ marginTop: 8 }}>
                      <div className="wc-progress__bar" style={{ width: `${pct}%` }} />
                    </div>
                  ) : (
                    lead.errorMessage && (
                      <div className="wc-row__meta" style={{ color: 'var(--ok-danger-text)' }}>
                        {files > 1 && t(`${files - completed} 个没传完：`)}
                        {lead.errorMessage}
                      </div>
                    )
                  )}
                </div>

                <div className="wc-row__right">
                  {tab === 'active' ? (
                    <>
                      <span
                        style={{
                          font: '500 12.5px/1 var(--ok-font-mono)',
                          fontVariantNumeric: 'tabular-nums',
                          color: 'var(--ok-text-2)',
                        }}
                      >
                        {formatSpeed(speed)}
                      </span>
                      {allPaused ? (
                        <Button
                          variant="ghost"
                          size="sm"
                          iconOnly
                          aria-label={group.batchId ? t('继续整批') : t('继续')}
                          disabled={action.busy}
                          onClick={() => actGroup(group, 'resume', t('已继续传输'))}
                        >
                          <Icon name="play" size={16} />
                        </Button>
                      ) : (
                        <Button
                          variant="ghost"
                          size="sm"
                          iconOnly
                          aria-label={group.batchId ? t('全部暂停') : t('暂停')}
                          disabled={action.busy || allStuck}
                          onClick={() => actGroup(group, 'pause', t('已暂停'))}
                        >
                          <Icon name="pause" size={16} />
                        </Button>
                      )}
                      {allStuck && (
                        <Button
                          variant="ghost"
                          size="sm"
                          iconOnly
                          aria-label={t('重试')}
                          disabled={action.busy}
                          onClick={() => actGroup(group, 'restart', t('已重新开始'))}
                        >
                          <Icon name="refresh" size={16} />
                        </Button>
                      )}
                    </>
                  ) : (
                    <>
                      <span className="wc-tl__time" style={{ paddingTop: 0 }}>
                        {formatClock(lead.updatedAtUnixMs)}
                      </span>
                      {single?.localPath && isDesktop() && (
                        <>
                          <Button
                            variant="ghost"
                            size="sm"
                            iconOnly
                            aria-label={t('打开文件')}
                            onClick={() => action.run(() => openFile(single.localPath ?? ''), t('已打开'))}
                          >
                            <Icon name="file" size={16} />
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            iconOnly
                            aria-label={t('打开所在目录')}
                            onClick={() =>
                              action.run(() => openPath(single.localPath ?? ''), t('已定位到文件'))
                            }
                          >
                            <Icon name="folderOpen" size={16} />
                          </Button>
                        </>
                      )}
                    </>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

<Toast text={action.notice} at={action.noticeAt} />

      {menu && menuGroup && (
        <RowMenu
          x={menu.x}
          y={menu.y}
          items={menuItems(menuGroup)}
          onClose={() => setMenu(null)}
        />
      )}

      <DeleteTransfersDialog
        ids={confirming?.ids ?? null}
        batchName={confirming?.name ?? null}
        receivedFiles={receivedFiles}
        onClose={() => setConfirming(null)}
        onDone={(message) => {
          setConfirming(null);
          setSelected([]);
          action.setNotice(message);
          transfers.reload();
        }}
      />
    </div>
  );
}

/** 右键菜单。位置跟着鼠标走，贴边时往回收，不让它跑出窗口。 */
function RowMenu({
  x,
  y,
  items,
  onClose,
}: {
  x: number;
  y: number;
  items: { label: string; danger?: boolean; disabled?: boolean; onSelect: () => void }[];
  onClose: () => void;
}) {
  useEffect(() => {
    const close = () => onClose();
    window.addEventListener('mousedown', close);
    window.addEventListener('blur', close);
    window.addEventListener('resize', close);
    document.addEventListener('scroll', close, true);
    return () => {
      window.removeEventListener('mousedown', close);
      window.removeEventListener('blur', close);
      window.removeEventListener('resize', close);
      document.removeEventListener('scroll', close, true);
    };
  }, [onClose]);

  const width = 168;
  const height = items.length * 30 + 8;

  return (
    <div
      className="wc-menu"
      role="menu"
      style={{
        left: Math.max(8, Math.min(x, window.innerWidth - width - 8)),
        top: Math.max(8, Math.min(y, window.innerHeight - height - 8)),
        width,
      }}
      onMouseDown={(event) => event.stopPropagation()}
      onContextMenu={(event) => event.preventDefault()}
    >
      {items.map((item) => (
        <button
          key={item.label}
          className="wc-menu__item"
          role="menuitem"
          data-danger={item.danger ? 'true' : undefined}
          disabled={item.disabled}
          onClick={() => {
            onClose();
            item.onSelect();
          }}
        >
          {item.label}
        </button>
      ))}
    </div>
  );
}

/**
 * 删除传输记录。
 *
 * 语义与手机端一致：勾了「同时删除接收到的文件」也只删**本机收到的**文件
 * （手机传过来的那些），电脑发出去的原文件永远不动 —— 服务端同样按这个规则执行。
 */
function DeleteTransfersDialog({
  ids,
  batchName,
  receivedFiles,
  onClose,
  onDone,
}: {
  ids: string[] | null;
  /** 整批删除时的文件夹名，写进标题让用户知道自己在删什么。 */
  batchName: string | null;
  receivedFiles: number;
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const [withFiles, setWithFiles] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setWithFiles(false);
    setError(null);
  }, [ids]);

  const count = ids?.length ?? 0;

  const submit = async () => {
    if (!ids || ids.length === 0) {
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const result = await api.post<{ deleted: number; rejected: string[] }>('/transfers/delete', {
        taskIds: ids,
        deleteFiles: withFiles,
      });

      const rejected = result.rejected?.length ?? 0;
      onDone(
        rejected > 0
          ? t(`已删除 ${result.deleted} 条，${rejected} 条被跳过：${result.rejected[0]}`)
          : t(`已删除 ${result.deleted} 条记录`),
      );
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open={ids !== null}
      onOpenChange={(value) => {
        if (!value) {
          onClose();
        }
      }}
      size={430}
      tone="danger"
      icon={<Icon name="trash" />}
      title={batchName ? t(`删除「${batchName}」的 ${count} 个文件`) : t(`删除 ${count} 条传输记录`)}
      description={t('只删记录和本机收到的文件；手机上的原件、电脑发出的原文件都不动。')}
      actions={
        <>
          <Button variant="ghost" onClick={onClose} disabled={busy}>
            {t('取消')}
          </Button>
          <Button variant="danger" onClick={submit} disabled={busy || count === 0}>
            {t('删除')}
          </Button>
        </>
      }
    >
      <label className="wc-checkline" style={{ marginTop: 4 }}>
        <input
          type="checkbox"
          checked={withFiles}
          disabled={receivedFiles === 0}
          onChange={(event) => setWithFiles(event.target.checked)}
        />
        {t(`同时删除接收到的文件（${receivedFiles} 个）`)}
      </label>
      <div style={{ font: '450 12px/1.5 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 6 }}>
        {receivedFiles === 0
          ? t('所选记录里没有本机收到的文件。')
          : t('只删手机传过来、已经落到本机的文件；本机发出去的原文件不会被删除。')}
      </div>
      {error && (
        <div style={{ font: '450 12.5px/1.5 var(--ok-font)', color: 'var(--ok-danger-text)', marginTop: 8 }}>
          {error}
        </div>
      )}
    </Dialog>
  );
}

/* ===================== 手机文件（浏览手机） ===================== */

type DeviceRoot = {
  rootId: string;
  displayName: string;
  kind: string;
  capabilities: number;
};

type DeviceEntry = {
  name: string;
  relativePath: string;
  isDirectory: boolean;
  sizeBytes: number;
  modifiedUnixMs: number;
  capabilities: number;
};

type DeviceEntries = {
  entries: DeviceEntry[];
  live: boolean;
  error: string | null;
};

type PendingJob = {
  jobId: string;
  operation: string;
  sourcePath: string | null;
  displayName: string | null;
};

/** 能力位：0 = 未知（按放行处理）。位值与 FileCapabilities 一致。 */
function allowsCapability(capabilities: number, bit: number): boolean {
  return capabilities === 0 || (capabilities & bit) !== 0;
}

function canRead(capabilities: number): boolean {
  return allowsCapability(capabilities, 2);
}

const CAP_RENAME = 16;
const CAP_DELETE = 32;
const CAP_MOVE = 64;

/**
 * 浏览手机里授权给电脑的目录。
 *
 * 数据来自手机上报的索引快照；快照没覆盖到的深目录由服务端现场下发 list 作业，
 * 手机执行完把结果挂回作业里 —— 界面只认"有就是有、没有就是空"，不猜。
 */
export function FilesPage({
  deviceId,
  onDeviceChange,
}: {
  deviceId: string;
  onDeviceChange: (deviceId: string) => void;
}) {
  const devices = useApi<DevicesResponse>('/devices', 3000);
  const [roots, setRoots] = useState<DeviceRoot[]>([]);
  const [rootId, setRootId] = useState('');
  const [path, setPath] = useState('');
  const [entries, setEntries] = useState<DeviceEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [savingPath, setSavingPath] = useState<string | null>(null);
  const [pending, setPending] = useState<PendingJob[]>([]);
  const [menu, setMenu] = useState<{ entry: DeviceEntry | null; x: number; y: number } | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [actionBusy, setActionBusy] = useState<string | null>(null);
  const [renameTarget, setRenameTarget] = useState<DeviceEntry | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [transferMode, setTransferMode] = useState<'move' | 'copy' | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [noticeAt, setNoticeAt] = useState(0);

  // 改过手机文件（移动/复制/删除/改名）之后要强制现场列目录：
  // 索引快照还是旧的，直接用会让刚删掉的条目"复活"。
  const forceLive = useRef(false);

  const paired = devices.data?.paired ?? [];
  const online = paired.filter((device) => device.online && !device.sessionPaused);
  const current = paired.find((device) => device.deviceId === deviceId) ?? null;

  // 只看在线设备：离线时索引快照可能过期，显示出来只会误导（用户明确要求不显示快照）。
  // 当前设备掉线就立即退回未选择状态，由下面的空状态给出提示。
  useEffect(() => {
    if (!deviceId) {
      return;
    }

    if (!online.some((device) => device.deviceId === deviceId)) {
      onDeviceChange('');
    }
  }, [deviceId, online, onDeviceChange]);

  useEffect(() => {
    if (!deviceId && online.length > 0) {
      onDeviceChange(online[0].deviceId);
    }
  }, [deviceId, online, onDeviceChange]);

  // 设备换了：重读根，清掉旧的路径与列表
  useEffect(() => {
    let alive = true;
    setRoots([]);
    setRootId('');
    setPath('');
    setEntries([]);
    setError(null);

    if (!deviceId) {
      return;
    }

    void (async () => {
      try {
        const list = await api.get<DeviceRoot[]>(`/files/roots?deviceId=${encodeURIComponent(deviceId)}`);
        if (!alive) {
          return;
        }

        setRoots(list);
        if (list.length > 0) {
          setRootId(list[0].rootId);
        } else {
          setError(t('手机上还没有授权给电脑的目录。在手机端「设备」里授权后，这里会出现。'));
        }
      } catch (caught) {
        if (alive) {
          setError(caught instanceof Error ? caught.message : String(caught));
        }
      }
    })();

    return () => {
      alive = false;
    };
  }, [deviceId]);

  // 根 / 路径 / 刷新：列目录
  useEffect(() => {
    let alive = true;
    setError(null);

    if (!deviceId || !rootId) {
      setEntries([]);
      setLoading(false);
      return;
    }

    setLoading(true);
    const live = forceLive.current;
    forceLive.current = false;

    // 现场刷新（改过文件之后）保留旧列表，等结果回来再整体替换：
    // 否则删/移之后的 3 秒里列表会闪成空白，看起来像"操作把目录弄没了"
    if (!live) {
      setEntries([]);
    }
    void (async () => {
      try {
        const query = new URLSearchParams({ deviceId, rootId, path });
        if (live) {
          query.set('refresh', '1');
        }

        const result = await api.get<DeviceEntries>(`/files/entries?${query.toString()}`);
        if (!alive) {
          return;
        }

        setEntries(result.entries);
        if (result.error) {
          setError(result.error);
        }
      } catch (caught) {
        if (alive) {
          setError(caught instanceof Error ? caught.message : String(caught));
        }
      } finally {
        if (alive) {
          setLoading(false);
        }
      }
    })();

    return () => {
      alive = false;
    };
  }, [deviceId, rootId, path, refresh]);

  // 已经排给手机、还没被取走的保存请求。手机休眠时它们会一直 pending，
  // 界面必须显示出来：否则用户会以为点了没反应，反复点出一堆重复下载。
  useEffect(() => {
    if (!deviceId) {
      setPending([]);
      return;
    }

    let alive = true;
    const load = async () => {
      try {
        const list = await api.get<PendingJob[]>(`/files/pending?deviceId=${encodeURIComponent(deviceId)}`);
        if (alive) {
          setPending(list.filter((job) => job.operation === 'download'));
        }
      } catch {
        // 读不到就算了，不影响浏览
      }
    };

    void load();
    const timer = window.setInterval(load, 3000);
    return () => {
      alive = false;
      window.clearInterval(timer);
    };
  }, [deviceId]);

  const notify = (text: string) => {
    setNotice(text);
    setNoticeAt(Date.now());
  };

  /** 文件行/空白处的右键菜单：双击与右键共用同一套主操作。 */
  const fileMenuItems = (entry: DeviceEntry | null) => {
    const items: { label: string; danger?: boolean; disabled?: boolean; onSelect: () => void }[] = [];

    if (entry) {
      if (entry.isDirectory) {
        items.push({ label: t('打开'), onSelect: () => setPath(entry.relativePath) });
        // 整个文件夹也能直接保存：展开后逐个下发，接收目录里按原结构建目录（1.5.59）
        items.push({
          label: t('保存到电脑'),
          disabled: !canRead(entry.capabilities) || !current?.online || Boolean(current?.sessionPaused),
          onSelect: () => void saveEntries([entry]),
        });
      } else if (queued.has(entry.relativePath)) {
        items.push({ label: t('等待手机接收…'), disabled: true, onSelect: () => undefined });
      } else {
        items.push({
          label: t('保存到电脑'),
          disabled: !canRead(entry.capabilities) || !current?.online || Boolean(current?.sessionPaused),
          onSelect: () => void saveToComputer(entry),
        });
      }
    }

    if (segments.length > 0) {
      items.push({ label: t('返回上一级'), onSelect: () => jumpTo(segments.length - 1) });
    }

    items.push({ label: t('刷新'), onSelect: () => setRefresh((n) => n + 1) });
    return items;
  };

  const saveToComputer = async (entry: DeviceEntry) => {
    if (savingPath) {
      return;
    }

    if (queued.has(entry.relativePath)) {
      notify(t('这个文件已在等待手机接收'));
      return;
    }

    if (!current || !current.online || current.sessionPaused) {
      notify(t('手机不在线，连上后再保存'));
      return;
    }

    // 心跳 10 秒一次、30 秒判离线：20 秒没动静就是连续丢了两次。
    // 这时候排下去的作业会一直 pending，用户只会看到"点了没反应"。
    if (Date.now() - current.lastSeenUnixMs > 20_000) {
      notify(t('手机已超过 20 秒没有响应（可能已休眠），点亮屏幕后再试'));
      return;
    }

    setSavingPath(entry.relativePath);
    try {
      const job = await api.post<{ jobId: string }>('/files/jobs', {
        deviceId,
        rootId,
        operation: 'download',
        sourcePath: entry.relativePath,
        displayName: entry.name,
      });

      // 手机挂起式轮询，正常一两秒就动；6 秒没动静就交给排队条，
      // 不让一个可能永远不来的响应把整个页面卡在"保存中"。
      const deadline = Date.now() + 6_000;
      let completed = false;
      while (Date.now() < deadline) {
        await new Promise((resolve) => window.setTimeout(resolve, 500));
        const status = await api.get<{ status: string; errorMessage?: string | null }>(
          `/files/jobs/${job.jobId}`,
        );

        if (status.status === 'completed') {
          completed = true;
          break;
        }

        if (status.status === 'failed') {
          throw new Error(status.errorMessage ?? t('手机读取文件失败'));
        }
      }

      if (!completed) {
        // 作业不会丢：手机下次连上会接着执行，落点仍是接收目录。
        // 按钮会通过 /files/pending 变成"等待手机…"，这里只给一句说明。
        notify(t('已排队，手机连上后会自动保存到接收目录'));
        return;
      }

      notify(t(`「${entry.name}」已保存到接收目录`));
    } catch (caught) {
      notify(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSavingPath(null);
    }
  };

  /** 手机是否真的能干活：在线 + 心跳够新。返回 false 时已经给过提示。 */
  const ensurePhoneReady = (action: string): boolean => {
    if (!current || !current.online || current.sessionPaused) {
      notify(t(`手机不在线，连上后再${action}`));
      return false;
    }

    if (Date.now() - current.lastSeenUnixMs > 20_000) {
      notify(t('手机已超过 20 秒没有响应（可能已休眠），点亮屏幕后再试'));
      return false;
    }

    return true;
  };

  const waitJob = async (jobId: string, timeoutMs = 15_000): Promise<'completed' | 'timeout'> => {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      await new Promise((resolve) => window.setTimeout(resolve, 350));
      const status = await api.get<{ status: string; errorMessage?: string | null }>(
        `/files/jobs/${jobId}`,
      );

      if (status.status === 'completed') {
        return 'completed';
      }

      if (status.status === 'failed') {
        throw new Error(status.errorMessage ?? t('手机执行失败'));
      }
    }

    return 'timeout';
  };

  /** 顺序下发一批作业并等结果。手机本身是一个循环，并发下发没有意义。 */
  const runJobs = async (
    operation: 'delete' | 'move' | 'copy' | 'rename',
    targets: { sourcePath: string; destinationPath?: string; displayName?: string; label: string }[],
    verb: string,
  ) => {
    setActionBusy(`${verb} 0/${targets.length}`);
    let done = 0;
    let timeouts = 0;
    const failures: string[] = [];

    for (const target of targets) {
      try {
        const job = await api.post<{ jobId: string }>('/files/jobs', {
          deviceId,
          rootId,
          operation,
          sourcePath: target.sourcePath,
          destinationPath: target.destinationPath,
          displayName: target.displayName,
        });

        const outcome = await waitJob(job.jobId);
        if (outcome === 'completed') {
          done += 1;
        } else {
          timeouts += 1;
        }
      } catch (caught) {
        failures.push(`${target.label}：${caught instanceof Error ? caught.message : String(caught)}`);
      }

      setActionBusy(`${verb} ${done + timeouts + failures.length}/${targets.length}`);
    }

    // 手机改了文件，索引快照就过期了：强制现场列一次
    forceLive.current = true;
    setSelected([]);
    setRefresh((n) => n + 1);
    setActionBusy(null);
    return { done, timeouts, failures };
  };

  const summarize = (result: { done: number; timeouts: number; failures: string[] }, verb: string) => {
    if (result.failures.length > 0) {
      return t(`${verb}完成 ${result.done} 项，${result.failures.length} 项失败：${result.failures[0]}`);
    }

    if (result.timeouts > 0) {
      return t(`${verb}完成 ${result.done} 项，${result.timeouts} 项仍在手机执行`);
    }

    return t(`${verb}完成 ${result.done} 项`);
  };

  const deleteSelected = async () => {
    setDeleteOpen(false);
    if (!ensurePhoneReady(t('删除'))) {
      return;
    }

    const result = await runJobs(
      'delete',
      selectedRows.map((entry) => ({
        sourcePath: entry.relativePath,
        label: entry.name,
      })),
      t('正在删除'),
    );
    notify(summarize(result, t('删除')));
  };

  const confirmTransfer = async (targetDir: string) => {
    const mode = transferMode;
    if (!mode) {
      return;
    }

    if (!ensurePhoneReady(mode === 'move' ? t('移动') : t('复制'))) {
      return;
    }

    setTransferMode(null);
    const result = await runJobs(
      mode,
      selectedRows.map((entry) => ({
        sourcePath: entry.relativePath,
        destinationPath: targetDir.length > 0 ? `${targetDir}/${entry.name}` : entry.name,
        displayName: entry.name,
        label: entry.name,
      })),
      mode === 'move' ? t('正在移动') : t('正在复制'),
    );
    notify(summarize(result, mode === 'move' ? t('移动') : t('复制')));
  };

  const confirmRename = async (newName: string) => {
    const entry = renameTarget;
    if (!entry) {
      return;
    }

    if (!ensurePhoneReady(t('重命名'))) {
      return;
    }

    setRenameTarget(null);
    const result = await runJobs(
      'rename',
      [{ sourcePath: entry.relativePath, displayName: newName, label: entry.name }],
      t('正在重命名'),
    );
    notify(result.failures.length > 0 ? result.failures[0] : t(`已重命名为「${newName}」`));
  };

  /**
   * 递归读出一个文件夹里的所有文件（保留相对子目录）。
   *
   * 手机不监听端口，电脑没法自己进去拉，所以"保存文件夹"只能是：
   * 先把目录树展开成文件清单，再逐个下发 download 作业。
   * 目录在索引快照里就直接返回；没索引的服务端会现场下发 list 作业（最多等 8 秒）。
   * 串行展开——手机的作业循环本身就是串行的，并发只会把队列塞满、进度还不好报。
   *
   * **不限深度**（1.5.60）：原来是 `depth > 12` 直接 return，深目录里的文件会被
   * 静默漏掉（用户只看到"保存完了"，实际少东西）。防死循环不该靠深度，而该靠
   * "每个目录只进一次"——SAF 个别提供方确实会给出环路或重复路径。
   */
  const collectFolderFiles = async (folder: DeviceEntry) => {
    const files: { sourcePath: string; targetDir: string; label: string }[] = [];
    let truncated = false;
    let cyclic = false;

    // 已经进过的目录。正常目录树里路径唯一，重复出现只能是环路或提供方异常。
    const visited = new Set<string>([folder.relativePath]);
    // 选中项为空路径时（理论上只有根）不加前缀，否则 `'' + '/'` 会把所有子项挡掉
    const prefix = folder.relativePath ? `${folder.relativePath}/` : '';

    const walk = async (path: string, targetDir: string): Promise<void> => {
      if (truncated) {
        return;
      }

      const query = new URLSearchParams({ deviceId, rootId, path });
      const result = await api.get<DeviceEntries>(`/files/entries?${query.toString()}`);
      if (result.error) {
        throw new Error(result.error);
      }

      for (const item of result.entries) {
        if (files.length >= 1000) {
          truncated = true;
          return;
        }

        if (!item.isDirectory) {
          files.push({ sourcePath: item.relativePath, targetDir, label: item.name });
          continue;
        }

        // 只允许往下走"确实在这个文件夹里面"的路径：既挡环路，也挡提供方返回越界路径
        if ((prefix && !item.relativePath.startsWith(prefix)) || visited.has(item.relativePath)) {
          cyclic = true;
          continue;
        }

        visited.add(item.relativePath);
        await walk(item.relativePath, `${targetDir}/${item.name}`);
      }
    };

    await walk(folder.relativePath, folder.name);
    return { files, truncated, cyclic };
  };

  /**
   * 保存到电脑。
   *
   * 单个文件：直接下发 download 作业，落到默认接收目录（按设备名分文件夹）。
   * 文件夹：展开后逐个下发，每个作业带上 `destinationPath`（接收目录下的相对子目录，
   * 例如「相册/2024」），手机上传时声明它，服务端按同样层级建目录。
   */
  const saveEntries = async (targets: DeviceEntry[]) => {
    if (!ensurePhoneReady(t('保存'))) {
      return;
    }

    const tasks: { sourcePath: string; displayName: string; targetDir?: string }[] = [];
    const failures: string[] = [];
    let folders = 0;

    for (const entry of targets) {
      if (!entry.isDirectory) {
        tasks.push({ sourcePath: entry.relativePath, displayName: entry.name });
        continue;
      }

      folders += 1;
      setSavingPath(entry.relativePath);
      setActionBusy(t(`正在读取「${entry.name}」…`));
      try {
        const result = await collectFolderFiles(entry);
        for (const file of result.files) {
          tasks.push({
            sourcePath: file.sourcePath,
            displayName: file.label,
            targetDir: file.targetDir,
          });
        }

        if (result.truncated) {
          failures.push(t(`「${entry.name}」文件太多，只取了前 1000 个`));
        }
        if (result.cyclic) {
          failures.push(t(`「${entry.name}」里有循环引用或越界路径，已跳过那部分`));
        }
      } catch (caught) {
        failures.push(`「${entry.name}」：${caught instanceof Error ? caught.message : String(caught)}`);
      }
    }

    setSavingPath(null);

    if (tasks.length === 0) {
      setActionBusy(null);
      notify(failures[0] ?? t('这个文件夹里没有文件'));
      return;
    }

    let ok = 0;
    let index = 0;
    for (const task of tasks) {
      index += 1;
      setActionBusy(t(`正在交给手机 ${index}/${tasks.length}`));

      if (queued.has(task.sourcePath)) {
        ok += 1;
        continue;
      }

      try {
        await api.post('/files/jobs', {
          deviceId,
          rootId,
          operation: 'download',
          sourcePath: task.sourcePath,
          displayName: task.displayName,
          destinationPath: task.targetDir,
        });
        ok += 1;
      } catch (caught) {
        failures.push(`${task.displayName}：${caught instanceof Error ? caught.message : String(caught)}`);
      }
    }

    setActionBusy(null);
    setSelected([]);

    const scope = folders > 0 ? t(`（${folders} 个文件夹按原结构保存）`) : '';
    notify(
      failures.length > 0
        ? t(`已交给手机 ${ok} 个文件${scope}，${failures.length} 条失败：${failures[0]}`)
        : t(`已交给手机 ${ok} 个文件${scope}，保存到接收目录`),
    );
  };

  const saveSelected = () => saveEntries(selectedRows);

  const queued = new Set(pending.map((job) => job.sourcePath ?? ''));
  const queuedNames = pending.map((job) => job.displayName ?? t('文件'));

  // 换设备/根/目录都清空选择：看不见的行不能继续被操作
  useEffect(() => {
    setSelected([]);
  }, [deviceId, rootId, path]);

  const selectedRows = entries.filter((entry) => selected.includes(entry.relativePath));
  const allChecked = entries.length > 0 && selected.length === entries.length;
  const someChecked = selected.length > 0 && !allChecked;
  const masterRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (masterRef.current) {
      masterRef.current.indeterminate = someChecked;
    }
  }, [someChecked]);

  const canMoveSelected = selectedRows.length > 0 && selectedRows.every((e) => allowsCapability(e.capabilities, CAP_MOVE));
  // 复制只要求"读源 + 写目标"；条目自己的能力位里不一定有 CREATE_DIRECTORY
  const canCopySelected = selectedRows.length > 0 && selectedRows.every((e) => allowsCapability(e.capabilities, 2 | 4));
  const canDeleteSelected = selectedRows.length > 0 && selectedRows.every((e) => allowsCapability(e.capabilities, CAP_DELETE));
  const canRenameSelected = selectedRows.length === 1 && allowsCapability(selectedRows[0].capabilities, CAP_RENAME);

  const toggleSelected = (path: string, checked: boolean) =>
    setSelected((current) => (checked ? [...current, path] : current.filter((item) => item !== path)));

  // 和传输中心一致：Ctrl+A 全选、Delete 删除、Esc 清空
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (isTypingTarget(event.target)) {
        return;
      }

      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
        event.preventDefault();
        setSelected(entries.map((entry) => entry.relativePath));
        return;
      }

      if (event.key === 'Escape') {
        setSelected([]);
        setMenu(null);
        return;
      }

      if ((event.key === 'Delete' || event.key === 'Backspace') && selected.length > 0) {
        event.preventDefault();
        setDeleteOpen(true);
      }
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [entries, selected]);

  const segments = path.length > 0 ? path.split('/').filter(Boolean) : [];
  const root = roots.find((item) => item.rootId === rootId) ?? null;

  // 鼠标侧键后退（button=3）：与旧界面一致，返回上一级。
  // WebView2 默认可能拿它做历史后退，这里 preventDefault 掉（页面本身没有历史栈）。
  useEffect(() => {
    const goBack = (event: MouseEvent) => {
      if (event.button !== 3) {
        return;
      }

      event.preventDefault();
      setPath((current) => current.split('/').filter(Boolean).slice(0, -1).join('/'));
    };

    window.addEventListener('mouseup', goBack);
    window.addEventListener('auxclick', goBack);
    return () => {
      window.removeEventListener('mouseup', goBack);
      window.removeEventListener('auxclick', goBack);
    };
  }, []);

  const jumpTo = (index: number) => {
    setPath(segments.slice(0, index).join('/'));
    setNotice(null);
  };

  return (
    <div
      className="wc-page"
      onContextMenu={(event) => {
        event.preventDefault();
        setMenu({ entry: null, x: event.clientX, y: event.clientY });
      }}
    >
      {/* 设备 + 刷新同一行：页面标题已经在顶栏，这里不再重复 */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', minWidth: 0 }}>
          {online.map((device) => (
            <Button
              key={device.deviceId}
              variant={device.deviceId === deviceId ? 'soft' : 'ghost'}
              size="sm"
              style={{ opacity: device.deviceId === deviceId ? 1 : 0.6 }}
              onClick={() => onDeviceChange(device.deviceId)}
            >
              <Icon name="phone" size={15} />
              {device.name}
            </Button>
          ))}
        </div>
        <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 8 }}>
          <label className="wc-checkline" title={t('全选当前目录')}>
            <input
              ref={masterRef}
              type="checkbox"
              checked={allChecked}
              disabled={entries.length === 0}
              onChange={(event) =>
                setSelected(event.target.checked ? entries.map((entry) => entry.relativePath) : [])
              }
            />
            {t('全选')}
          </label>
          <Button variant="ghost" size="sm" disabled={!deviceId} onClick={() => setRefresh((n) => n + 1)}>
            <Icon name="refresh" size={16} />
            {t('刷新')}
          </Button>
        </div>
      </div>

      {roots.length > 1 && (
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 10 }}>
          {roots.map((item) => (
            <Button
              key={item.rootId}
              variant={item.rootId === rootId ? 'soft' : 'ghost'}
              size="sm"
              style={{ opacity: item.rootId === rootId ? 1 : 0.6 }}
              onClick={() => {
                setRootId(item.rootId);
                setPath('');
              }}
            >
              <Icon name="folder" size={15} />
              {item.displayName}
            </Button>
          ))}
        </div>
      )}

      {rootId && (
        <div className="wc-pathbar">
          {segments.length > 0 && (
            <Button
              variant="ghost"
              size="sm"
              iconOnly
              aria-label={t('上一级')}
              onClick={() => jumpTo(segments.length - 1)}
            >
              <Icon name="arrowLeft" size={15} />
            </Button>
          )}

          <div className="wc-crumbs">
            <button
              className="wc-crumb"
              data-current={segments.length === 0}
              onClick={() => jumpTo(0)}
              title={root?.displayName ?? ''}
            >
              {root?.displayName ?? t('根目录')}
            </button>
            {segments.map((segment, index) => (
              <span key={`${segment}-${index}`} style={{ display: 'inline-flex', alignItems: 'center' }}>
                <span className="wc-crumbs__sep">/</span>
                <button
                  className="wc-crumb"
                  data-current={index === segments.length - 1}
                  onClick={() => jumpTo(index + 1)}
                >
                  {segment}
                </button>
              </span>
            ))}
          </div>
        </div>
      )}

      {pending.length > 0 && (
        <div className="wc-queue">
          <Icon name="clock" size={15} />
          <span>
            {t('等待手机接收：')}
            {queuedNames.slice(0, 2).join('、')}
            {queuedNames.length > 2 ? t(` 等 ${queuedNames.length} 个`) : ''}
          </span>
          <span
            className="wc-queue__hint"
            title={t('手机休眠时不会执行；可在系统设置里把 WirelessCable 设为「后台不限制」')}
          >
            {t('手机连上后自动保存到接收目录')}
          </span>
        </div>
      )}

      {devices.error && <Notice text={t(`读取设备失败：${devices.error}`)} />}
      {error && <Notice text={error} />}

      {!deviceId ? (
        <Empty
          text={
            paired.length === 0
              ? t('还没有配对设备')
              : online.length === 0
                ? t('没有在线设备（离线时无法浏览）')
                : t('正在读取设备…')
          }
        />
      ) : loading ? (
        <Empty text={t('正在读取手机目录…')} />
      ) : entries.length === 0 ? (
        <Empty text={error ? t('读不到这个目录') : t('这个目录是空的')} />
      ) : (
        <div className="wc-list">
          {entries.map((entry, index) => (
            <div
              className="wc-row wc-row--dense wc-row--selectable wc-anim-item"
              key={entry.relativePath}
              data-picked={selected.includes(entry.relativePath) ? 'true' : undefined}
              style={{
                animationDelay: `${index * 30}ms`,
                cursor: entry.isDirectory ? 'pointer' : 'default',
              }}
              onDoubleClick={() =>
                entry.isDirectory ? setPath(entry.relativePath) : void saveToComputer(entry)
              }
              onContextMenu={(event) => {
                event.preventDefault();
                // 行上的菜单要盖掉页面级那一个
                event.stopPropagation();
                // 右键未选中的行只选它；已选中的保持多选（和资源管理器一致）
                if (!selected.includes(entry.relativePath)) {
                  setSelected([entry.relativePath]);
                }

                setMenu({ entry, x: event.clientX, y: event.clientY });
              }}
            >
              <input
                type="checkbox"
                aria-label={t(`选择 ${entry.name}`)}
                checked={selected.includes(entry.relativePath)}
                onChange={(event) => toggleSelected(entry.relativePath, event.target.checked)}
                onDoubleClick={(event) => event.stopPropagation()}
              />

              <div
                className="wc-row__icon"
                style={
                  entry.isDirectory
                    ? undefined
                    : { background: 'var(--ok-success-soft)', color: 'var(--ok-success-text)' }
                }
              >
                <Icon name={entry.isDirectory ? 'folder' : 'file'} />
              </div>

              <div style={{ minWidth: 0 }}>
                <div className="wc-row__name" title={entry.name}>
                  {entry.name}
                </div>
                <div className="wc-row__meta">
                  {entry.isDirectory
                    ? t('文件夹')
                    : `${formatBytes(entry.sizeBytes)}${entry.modifiedUnixMs > 0 ? ` · ${formatClock(entry.modifiedUnixMs)}` : ''}`}
                </div>
              </div>

              <div className="wc-row__right">
                {entry.isDirectory ? (
                  <>
                    <Button variant="ghost" size="sm" onClick={() => setPath(entry.relativePath)}>
                      {t('打开')}
                    </Button>
                    <Button
                      variant="soft"
                      size="sm"
                      disabled={
                        !canRead(entry.capabilities) ||
                        savingPath !== null ||
                        !current?.online ||
                        current?.sessionPaused
                      }
                      title={
                        !current?.online || current?.sessionPaused
                          ? t('手机不在线，连上后再保存')
                          : t('按原目录结构保存到接收目录')
                      }
                      onClick={() => void saveEntries([entry])}
                    >
                      {savingPath === entry.relativePath ? t('读取中…') : t('保存')}
                    </Button>
                  </>
                ) : queued.has(entry.relativePath) ? (
                  <Button variant="ghost" size="sm" disabled>
                    {t('等待手机…')}
                  </Button>
                ) : (
                  <Button
                    variant="soft"
                    size="sm"
                    disabled={
                      !canRead(entry.capabilities) ||
                      savingPath !== null ||
                      !current?.online ||
                      current?.sessionPaused
                    }
                    title={!current?.online || current?.sessionPaused ? t('手机不在线，连上后再保存') : undefined}
                    onClick={() => void saveToComputer(entry)}
                  >
                    {savingPath === entry.relativePath ? t('保存中…') : t('保存到电脑')}
                  </Button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {selected.length > 0 && (
        <>
          {/* 撑开剩余高度：列表短的时候把操作条推到页面最下边，长的时候只留 12px 间距 */}
          <div className="wc-filebar__spacer" />
          <div className="wc-filebar">
          <span className="wc-filebar__count">
            {t(`已选 ${selected.length} 项`)}
            {actionBusy && <span className="wc-filebar__busy">{actionBusy}</span>}
          </span>

          <div className="wc-filebar__actions">
            <Button variant="soft" size="sm" disabled={Boolean(actionBusy)} onClick={() => void saveSelected()}>
              {t('保存到电脑')}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={Boolean(actionBusy) || !canMoveSelected}
              title={!canMoveSelected ? t('所选项目不支持移动') : undefined}
              onClick={() => setTransferMode('move')}
            >
              {t('移动')}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={Boolean(actionBusy) || !canCopySelected}
              title={!canCopySelected ? t('所选项目不支持复制') : undefined}
              onClick={() => setTransferMode('copy')}
            >
              {t('复制')}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={Boolean(actionBusy) || !canRenameSelected}
              title={!canRenameSelected ? t('选中单个项目才能重命名') : undefined}
              onClick={() => setRenameTarget(selectedRows[0] ?? null)}
            >
              {t('重命名')}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              disabled={Boolean(actionBusy) || !canDeleteSelected}
              title={!canDeleteSelected ? t('所选项目不支持删除') : undefined}
              onClick={() => setDeleteOpen(true)}
            >
              {t('删除')}
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setSelected([])}>
              {t('取消')}
            </Button>
          </div>
          </div>
        </>
      )}

      <Toast text={notice} at={noticeAt} />

      {menu && (
        <RowMenu x={menu.x} y={menu.y} items={fileMenuItems(menu.entry)} onClose={() => setMenu(null)} />
      )}

      <RenameFileDialog
        entry={renameTarget}
        busy={Boolean(actionBusy)}
        onClose={() => setRenameTarget(null)}
        onConfirm={(name) => void confirmRename(name)}
      />

      <DeleteFilesDialog
        open={deleteOpen}
        entries={selectedRows}
        onClose={() => setDeleteOpen(false)}
        onConfirm={() => void deleteSelected()}
      />

      <DestinationDialog
        open={transferMode !== null}
        mode={transferMode}
        deviceId={deviceId}
        rootId={rootId}
        rootName={root?.displayName ?? ''}
        sources={selectedRows}
        startPath={path}
        onClose={() => setTransferMode(null)}
        onConfirm={(target) => void confirmTransfer(target)}
      />
    </div>
  );
}

/** 重命名单个手机文件/文件夹。 */
function RenameFileDialog({
  entry,
  busy,
  onClose,
  onConfirm,
}: {
  entry: DeviceEntry | null;
  busy: boolean;
  onClose: () => void;
  onConfirm: (name: string) => void;
}) {
  const [name, setName] = useState('');

  useEffect(() => {
    setName(entry?.name ?? '');
  }, [entry]);

  const trimmed = name.trim();
  const badFormat =
    trimmed === '.' || trimmed === '..' || trimmed.includes('/') || trimmed.includes('\\');
  const invalid = !entry || trimmed.length === 0 || trimmed === entry.name || badFormat;

  return (
    <Dialog
      open={entry !== null}
      onOpenChange={(value) => {
        if (!value) {
          onClose();
        }
      }}
      size={420}
      tone="accent"
      icon={<Icon name="edit" />}
      title={t('重命名')}
      description={t('只改名字，位置不变。')}
      actions={
        <>
          <Button variant="ghost" onClick={onClose} disabled={busy}>
            {t('取消')}
          </Button>
          <Button variant="accent" disabled={busy || invalid} onClick={() => onConfirm(trimmed)}>
            {t('重命名')}
          </Button>
        </>
      }
    >
      <input
        className="wc-textinput"
        autoFocus
        value={name}
        onChange={(event) => setName(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' && !invalid && !busy) {
            onConfirm(trimmed);
          }
        }}
      />
      {badFormat && <div className="wc-field__hint">{t('名称不能包含 / 或 \\。')}</div>}
      {entry && trimmed.length === 0 && <div className="wc-field__hint">{t('名称不能为空。')}</div>}
    </Dialog>
  );
}

/** 删除前确认。手机端是递归删除，删了就找不回来。 */
function DeleteFilesDialog({
  open,
  entries,
  onClose,
  onConfirm,
}: {
  open: boolean;
  entries: DeviceEntry[];
  onClose: () => void;
  onConfirm: () => void;
}) {
  const names = entries.slice(0, 3).map((entry) => entry.name);
  const more = entries.length - names.length;

  return (
    <Dialog
      open={open}
      onOpenChange={(value) => {
        if (!value) {
          onClose();
        }
      }}
      size={430}
      tone="danger"
      icon={<Icon name="trash" />}
      title={t(`删除 ${entries.length} 项`)}
      description={t('删除后无法恢复；文件夹会连同里面的内容一起删除。')}
      actions={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t('取消')}
          </Button>
          <Button variant="danger" onClick={onConfirm}>
            {t('删除')}
          </Button>
        </>
      }
    >
      <div style={{ font: '450 12.5px/1.6 var(--ok-font)', color: 'var(--ok-text-2)', wordBreak: 'break-all' }}>
        {names.join('、')}
        {more > 0 ? t(` 等 ${entries.length} 项`) : ''}
      </div>
    </Dialog>
  );
}

/** 移动/复制的目标目录选择器。只列目录，进入逐级浏览。 */
function DestinationDialog({
  open,
  mode,
  deviceId,
  rootId,
  rootName,
  sources,
  startPath,
  onClose,
  onConfirm,
}: {
  open: boolean;
  mode: 'move' | 'copy' | null;
  deviceId: string;
  rootId: string;
  rootName: string;
  sources: DeviceEntry[];
  startPath: string;
  onClose: () => void;
  onConfirm: (targetPath: string) => void;
}) {
  const [path, setPath] = useState(startPath);
  const [folders, setFolders] = useState<DeviceEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setPath(startPath);
      setError(null);
    }
  }, [open, startPath]);

  useEffect(() => {
    if (!open || !deviceId || !rootId) {
      return;
    }

    let alive = true;
    setLoading(true);
    setError(null);
    setFolders([]);

    void (async () => {
      try {
        const query = new URLSearchParams({ deviceId, rootId, path });
        const result = await api.get<DeviceEntries>(`/files/entries?${query.toString()}`);
        if (!alive) {
          return;
        }

        setFolders(result.entries.filter((entry) => entry.isDirectory));
        if (result.error) {
          setError(result.error);
        }
      } catch (caught) {
        if (alive) {
          setError(caught instanceof Error ? caught.message : String(caught));
        }
      } finally {
        if (alive) {
          setLoading(false);
        }
      }
    })();

    return () => {
      alive = false;
    };
  }, [open, deviceId, rootId, path]);

  const segments = path.length > 0 ? path.split('/').filter(Boolean) : [];
  const parentOfPath = (value: string) => (value.includes('/') ? value.slice(0, value.lastIndexOf('/')) : '');

  // 不能把目录移进自己或自己的子目录；全都已经在目标目录里也没意义
  const blocked = sources.some(
    (entry) => entry.isDirectory && (path === entry.relativePath || path.startsWith(`${entry.relativePath}/`)),
  );
  const sameParent = sources.length > 0 && sources.every((entry) => parentOfPath(entry.relativePath) === path);
  const canConfirm = !blocked && !sameParent;

  const jump = (index: number) => setPath(segments.slice(0, index).join('/'));

  return (
    <Dialog
      open={open}
      onOpenChange={(value) => {
        if (!value) {
          onClose();
        }
      }}
      size={470}
      tone="accent"
      icon={<Icon name="folder" />}
      title={mode === 'move' ? t('移动到') : t('复制到')}
      description={t(`已选 ${sources.length} 项，选择目标文件夹。`)}
      actions={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t('取消')}
          </Button>
          <Button variant="accent" disabled={!canConfirm || loading} onClick={() => onConfirm(path)}>
            {mode === 'move' ? t('移动到此处') : t('复制到此处')}
          </Button>
        </>
      }
    >
      <div className="wc-pathbar" style={{ marginBottom: 8 }}>
        <div className="wc-crumbs">
          <button className="wc-crumb" data-current={segments.length === 0} onClick={() => jump(0)}>
            {rootName || t('根目录')}
          </button>
          {segments.map((segment, index) => (
            <span key={`${segment}-${index}`} style={{ display: 'inline-flex', alignItems: 'center' }}>
              <span className="wc-crumbs__sep">/</span>
              <button
                className="wc-crumb"
                data-current={index === segments.length - 1}
                onClick={() => jump(index + 1)}
              >
                {segment}
              </button>
            </span>
          ))}
        </div>
      </div>

      {loading ? (
        <Empty text={t('正在读取…')} />
      ) : folders.length === 0 ? (
        <Empty text={error ? t('读不到这个目录') : t('没有子文件夹')} />
      ) : (
        <div className="wc-dirlist">
          {folders.map((folder) => (
            <button
              key={folder.relativePath}
              className="wc-dirpick"
              onClick={() => setPath(folder.relativePath)}
            >
              <Icon name="folder" size={16} />
              <span className="wc-dirpick__name">{folder.name}</span>
              <Icon name="chevronRight" size={14} />
            </button>
          ))}
        </div>
      )}

      {blocked && <div className="wc-field__hint">{t('不能移动/复制到自身或其子目录。')}</div>}
      {!blocked && sameParent && <div className="wc-field__hint">{t('已经在目标目录里了。')}</div>}
    </Dialog>
  );
}

function Ring({ pct }: { pct: number }) {
  const R = 15;
  const C = 2 * Math.PI * R;
  return (
    <div className="wc-ring">
      <svg width="40" height="40" viewBox="0 0 40 40">
        <circle className="wc-ring__track" cx="20" cy="20" r={R} />
        <circle
          className="wc-ring__bar"
          cx="20"
          cy="20"
          r={R}
          strokeDasharray={C}
          strokeDashoffset={C * (1 - pct / 100)}
        />
      </svg>
      <span className="wc-ring__label">{pct}</span>
    </div>
  );
}

/* ===================== 共享目录 ===================== */

/** 只看不选：给色板画出每个预设的真实颜色。 */
const ACCENT_HEX: Record<string, string> = {
  pink: '#E2557B',
  orange: '#E4762C',
  amber: '#D89B1B',
  blue: '#2E7BFF',
  violet: '#7C5CE6',
};

// 同样只存中文，渲染时再 t()：模块级求值会冻住启动时的语言
const PERMISSION_OPTIONS = [
  { label: '只读', value: 3 },
  { label: '读写', value: 127 },
  { label: '全部', value: 255 },
];

export function SharedPage({ openEditor = false }: { openEditor?: boolean }) {
  const roots = useApi<RootDto[]>('/roots', 4000);
  const devices = useApi<DevicesResponse>('/devices', 8000);
  const [adding, setAdding] = useState<string | null>(null);
  const [editing, setEditing] = useState<RootDto | null>(null);
  const action = useAction();

  // 截图/排障用：?dialog=root 直接把第一个目录的授权弹窗打开
  useEffect(() => {
    if (openEditor && !editing && roots.data && roots.data.length > 0) {
      setEditing(roots.data[0]);
    }
  }, [openEditor, editing, roots.data]);

  const rows = roots.data ?? [];

  const addRoot = () =>
    action.run(async () => {
      const folder = await pickFolder(t('选择要共享给设备的目录'));
      if (!folder) {
        return;
      }

      setAdding(folder);
    });

  return (
    <div className="wc-page">
      <div className="wc-section__head">
        <span className="wc-section__hint">{t('授权给已配对设备的本机目录')}</span>
        <div className="wc-section__action">
          <Button variant="accent" size="sm" disabled={!isDesktop() || action.busy} onClick={addRoot}>
            <Icon name="plus" size={16} />
            {t('添加目录')}
          </Button>
        </div>
      </div>

      {roots.error && <Notice text={t(`读取失败：${roots.error}`)} />}

      {rows.length === 0 ? (
        <Empty text={t('还没有共享目录')} />
      ) : (
        <div className="wc-list">
          {rows.map((root, index) => {
            const permissionNames = Array.from(
              new Set(root.grants.flatMap((grant) => grant.permissionNames)),
            );

            return (
              <div
                className="wc-row wc-anim-item"
                key={root.directoryId}
                style={{ animationDelay: `${index * 60}ms`, gridTemplateColumns: '34px minmax(0,1fr) auto' }}
              >
                <div className="wc-row__icon">
                  <Icon name="folder" />
                </div>
                <div style={{ minWidth: 0 }}>
                  <div className="wc-row__name">{root.name}</div>
                  <div className="wc-row__meta">{root.path}</div>
                  <div className="wc-perms">
                    {permissionNames.length === 0 ? (
                      <button className="wc-perm" onClick={() => setEditing(root)}>
                        {t('未授权设备')}
                      </button>
                    ) : (
                      permissionNames.map((name) => (
                        <button
                          className="wc-perm"
                          data-on="true"
                          key={name}
                          title={t('点击编辑授权')}
                          onClick={() => setEditing(root)}
                        >
                          {t(name)}
                        </button>
                      ))
                    )}
                  </div>
                </div>
                <div className="wc-row__right">
                  <Badge tone="neutral">{t(`${root.grants.length} 台设备`)}</Badge>
                  <Button
                    variant="ghost"
                    size="sm"
                    iconOnly
                    aria-label={t('编辑授权')}
                    onClick={() => setEditing(root)}
                  >
                    <Icon name="edit" size={16} />
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    iconOnly
                    aria-label={t('打开目录')}
                    disabled={!isDesktop()}
                    onClick={() => action.run(() => openPath(root.path), t('已打开目录'))}
                  >
                    <Icon name="folderOpen" size={16} />
                  </Button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      <div className="wc-section__head" style={{ marginTop: 26 }}>
        <h2 className="wc-section__title">{t('权限说明')}</h2>
      </div>

      <div className="wc-info">
        <div className="wc-info__row">
          <span className="wc-info__label">{t('浏览')}</span>
          <span className="wc-info__value wc-info__value--muted">{t('查看目录与文件列表，不写入')}</span>
        </div>
        <div className="wc-info__row">
          <span className="wc-info__label">{t('下载')}</span>
          <span className="wc-info__value wc-info__value--muted">{t('把文件复制到设备本地')}</span>
        </div>
        <div className="wc-info__row">
          <span className="wc-info__label">{t('上传 / 新建')}</span>
          <span className="wc-info__value wc-info__value--muted">{t('向目录写入文件与子目录')}</span>
        </div>
        <div className="wc-info__row">
          <span className="wc-info__label">{t('重命名 / 移动 / 删除')}</span>
          <span className="wc-info__value wc-info__value--muted">{t('修改已有条目')}</span>
        </div>
      </div>

      <Notice text={action.notice} />

      <EditRootDialog
        root={editing}
        devices={devices.data?.paired ?? []}
        onClose={() => setEditing(null)}
        onDone={(message) => {
          setEditing(null);
          action.setNotice(message);
          roots.reload();
        }}
      />

      <AddRootDialog
        folder={adding}
        devices={devices.data?.paired ?? []}
        onClose={() => setAdding(null)}
        onDone={(message) => {
          setAdding(null);
          action.setNotice(message);
          roots.reload();
        }}
      />
    </div>
  );
}

function EditRootDialog({
  root,
  devices,
  onClose,
  onDone,
}: {
  root: RootDto | null;
  devices: PairedDevice[];
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  // 每次打开用当前授权初始化：勾选 = 这台设备能访问，下拉 = 权限档位
  const [selected, setSelected] = useState<Record<string, number>>(() =>
    root ? Object.fromEntries(root.grants.map((grant) => [grant.deviceId, grant.permissions])) : {},
  );
  const [confirmRemove, setConfirmRemove] = useState(false);

  if (!root) {
    return null;
  }

  // 授权行可能指向已删除的设备（历史数据）：要让用户看见，否则"1 台设备"和勾选状态互相打脸
  const orphans = root.grants.filter(
    (grant) => !devices.some((device) => device.deviceId === grant.deviceId),
  );

  const save = async () => {
    try {
      await api.post(`/roots/${root.directoryId}/grants`, {
        grants: Object.entries(selected).map(([deviceId, permissions]) => ({ deviceId, permissions })),
      });
      onDone(t('已保存授权'));
    } catch (caught) {
      onDone(caught instanceof Error ? caught.message : String(caught));
    }
  };

  const remove = async () => {
    try {
      await api.del(`/roots/${root.directoryId}`);
      onDone(t(`已取消共享「${root.name}」，文件仍在原处`));
    } catch (caught) {
      onDone(caught instanceof Error ? caught.message : String(caught));
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      size={520}
      icon={<Icon name="shield" />}
      title={t(`「${root.name}」的访问权限`)}
      description={root.path}
      actions={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t('取消')}
          </Button>
          <Button variant="accent" onClick={save}>
            {t('保存')}
          </Button>
        </>
      }
    >
      <div style={{ display: 'grid', gap: 8, marginTop: 4 }}>
        {devices.length === 0 && (
          <div style={{ font: '450 12.5px/1.5 var(--ok-font)', color: 'var(--ok-text-3)' }}>
            {t('还没有配对设备，先配对再授权。')}
          </div>
        )}

        {devices.map((device) => {
          const value = selected[device.deviceId];
          return (
            <label
              key={device.deviceId}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 10,
                padding: '10px 12px',
                borderRadius: 'calc(12px * var(--ok-rk))',
                background: value === undefined ? 'var(--ok-well)' : 'var(--ok-accent-soft)',
                boxShadow: 'inset 0 0 0 1px var(--ok-line)',
              }}
            >
              <input
                type="checkbox"
                checked={value !== undefined}
                onChange={(event) =>
                  setSelected((current) => {
                    const next = { ...current };
                    if (event.target.checked) {
                      next[device.deviceId] = 255;
                    } else {
                      delete next[device.deviceId];
                    }

                    return next;
                  })
                }
              />
              <div style={{ minWidth: 0, flex: 1 }}>
                <div style={{ font: '600 13.5px/1.35 var(--ok-font)', color: 'var(--ok-text)' }}>{device.name}</div>
                <div style={{ font: '450 12px/1.4 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 2 }}>
                  {device.platform}
                  {device.online ? t(' · 在线') : t(' · 离线')}
                </div>
              </div>
              <select
                disabled={value === undefined}
                value={value ?? 255}
                onChange={(event) =>
                  setSelected((current) => ({ ...current, [device.deviceId]: Number(event.target.value) }))
                }
                style={{
                  height: 30,
                  borderRadius: 'calc(9px * var(--ok-rk))',
                  border: '1px solid var(--ok-line)',
                  background: 'var(--ok-card)',
                  color: 'var(--ok-text)',
                  font: '450 12.5px/1.4 var(--ok-font)',
                }}
              >
                {PERMISSION_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {t(option.label)}
                  </option>
                ))}
              </select>
            </label>
          );
        })}

        {orphans.length > 0 && (
          <div style={{ font: '450 12px/1.5 var(--ok-font)', color: 'var(--ok-warning-text)' }}>
            {t(`有 ${orphans.length} 条授权指向已移除的设备，保存后会一并清除。`)}
          </div>
        )}

        <div style={{ display: 'flex', gap: 8, marginTop: 6 }}>
          <Button variant="ghost" size="sm" onClick={() => setConfirmRemove((v) => !v)}>
            {confirmRemove ? t('取消') : t('取消共享')}
          </Button>
          {confirmRemove && (
            <Button variant="danger" size="sm" onClick={remove}>
              {t('确认取消共享')}
            </Button>
          )}
        </div>
        {confirmRemove && (
          <div style={{ font: '450 12px/1.5 var(--ok-font)', color: 'var(--ok-text-3)' }}>
            {t('只删共享记录与授权，目录与里面的文件不会被删除。')}
          </div>
        )}
      </div>
    </Dialog>
  );
}

function AddRootDialog({
  folder,
  devices,
  onClose,
  onDone,
}: {
  folder: string | null;
  devices: PairedDevice[];
  onClose: () => void;
  onDone: (message: string) => void;
}) {
  const [selected, setSelected] = useState<Record<string, number>>({});

  if (!folder) {
    return null;
  }

  const name = folder.split(/[\\/]/).filter(Boolean).pop() ?? folder;

  const submit = async () => {
    try {
      await api.post('/roots', {
        path: folder,
        name,
        grants: Object.entries(selected).map(([deviceId, permissions]) => ({ deviceId, permissions })),
      });
      onDone(t(`已共享「${name}」`));
    } catch (caught) {
      onDone(caught instanceof Error ? caught.message : String(caught));
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      size={520}
      icon={<Icon name="folder" />}
      title={t('添加共享目录')}
      description={folder}
      actions={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t('取消')}
          </Button>
          <Button variant="accent" onClick={submit}>
            {t('共享')}
          </Button>
        </>
      }
    >
      <div style={{ display: 'grid', gap: 8, marginTop: 4 }}>
        {devices.length === 0 && (
          <div style={{ font: '450 12.5px/1.5 var(--ok-font)', color: 'var(--ok-text-3)' }}>
            {t('还没有配对设备，先配对再授权。')}
          </div>
        )}
        {devices.map((device) => {
          const value = selected[device.deviceId];
          return (
            <label
              key={device.deviceId}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 10,
                padding: '10px 12px',
                borderRadius: 'calc(12px * var(--ok-rk))',
                background: value === undefined ? 'var(--ok-well)' : 'var(--ok-accent-soft)',
                boxShadow: 'inset 0 0 0 1px var(--ok-line)',
              }}
            >
              <input
                type="checkbox"
                checked={value !== undefined}
                onChange={(event) =>
                  setSelected((current) => {
                    const next = { ...current };
                    if (event.target.checked) {
                      next[device.deviceId] = 255;
                    } else {
                      delete next[device.deviceId];
                    }

                    return next;
                  })
                }
              />
              <div style={{ minWidth: 0, flex: 1 }}>
                <div style={{ font: '600 13.5px/1.35 var(--ok-font)', color: 'var(--ok-text)' }}>{device.name}</div>
                <div style={{ font: '450 12px/1.4 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 2 }}>
                  {device.platform}
                </div>
              </div>
              <select
                disabled={value === undefined}
                value={value ?? 255}
                onChange={(event) =>
                  setSelected((current) => ({ ...current, [device.deviceId]: Number(event.target.value) }))
                }
                style={{
                  height: 30,
                  borderRadius: 'calc(9px * var(--ok-rk))',
                  border: '1px solid var(--ok-line)',
                  background: 'var(--ok-card)',
                  color: 'var(--ok-text)',
                  font: '450 12.5px/1.4 var(--ok-font)',
                }}
              >
                {PERMISSION_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {t(option.label)}
                  </option>
                ))}
              </select>
            </label>
          );
        })}
      </div>
    </Dialog>
  );
}

/* ===================== 设置 ===================== */

export function SettingsPage({ initialTab = t('常规') }: { initialTab?: string }) {
  const [tab, setTab] = useState(initialTab);
  const settings = useApi<SettingsSnapshot>('/settings', 0);
  const { resolvedTheme, setTheme, accent, setAccent } = useOink();
  const action = useAction();

  const tabs = [t('常规'), t('连接'), t('传输'), t('外观'), t('高级')];

  // 改接收目录：选目录 → 落库 → 服务端立刻换落点。只影响之后接收的文件。
  const changeReceivedDirectory = () =>
    action.run(async () => {
      const path = await pickFolder(t('选择接收目录'));
      if (!path) {
        return;
      }

      const result = await api.post<{ path: string }>('/settings/received-directory', { path });
      settings.reload();
      return t(`接收目录已改为 ${result.path}`);
    }, undefined);
  const data = settings.data;

  return (
    <div className="wc-page">
      <div className="wc-settings">
        <nav className="wc-settings__nav">
          <div className="wc-nav" style={{ padding: 0 }}>
            {tabs.map((item) => (
              <button
                key={item}
                className="wc-navitem"
                data-current={tab === item}
                onClick={() => setTab(item)}
              >
                <span>{item}</span>
              </button>
            ))}
          </div>
        </nav>

        <div>
          {settings.error && <Notice text={t(`读取失败：${settings.error}`)} />}

          {tab === t('常规') && (
            <section className="wc-section" style={{ marginBottom: 0 }}>
              <div className="wc-info">
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('版本')}</span>
                  <span className="wc-info__value">{data?.version ?? '—'}</span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('本机名')}</span>
                  <span className="wc-info__value">{data?.machineName ?? '—'}</span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('已配对设备')}</span>
                  <span className="wc-info__value">{t(`${data?.pairedDevices ?? 0} 台`)}</span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('接收目录')}</span>
                  <span className="wc-info__value wc-info__value--muted">{data?.receivedDirectory ?? '—'}</span>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={!isDesktop() || action.busy}
                    onClick={changeReceivedDirectory}
                  >
                    {t('更改')}
                  </Button>
                  <button
                    className="wc-copy"
                    aria-label={t('打开接收目录')}
                    disabled={!isDesktop()}
                    onClick={() => action.run(() => openPath(data?.receivedDirectory ?? ''), t('已打开'))}
                  >
                    <Icon name="folderOpen" size={13} />
                  </button>
                </div>
                <div className="wc-field__hint">{t('只影响之后接收的文件，已收到的文件不会搬家。')}</div>
              </div>
            </section>
          )}

          {tab === t('连接') && (
            <section className="wc-section" style={{ marginBottom: 0 }}>
              <div className="wc-info">
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('端口')}</span>
                  <span className="wc-info__value">{data?.port ?? '—'}</span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('局域网地址')}</span>
                  <span className="wc-info__value">
                    {data?.addresses.length ? data.addresses.join(' · ') : '—'}
                  </span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('协议版本')}</span>
                  <span className="wc-info__value">{data?.protocolVersion ?? '—'}</span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('证书指纹')}</span>
                  <span className="wc-info__value" style={{ wordBreak: 'break-all' }}>
                    {data?.fingerprint ?? '—'}
                  </span>
                  <button
                    className="wc-copy"
                    aria-label={t('复制证书指纹')}
                    onClick={() => action.run(() => copyText(data?.fingerprint ?? ''), t('已复制'))}
                  >
                    <Icon name="copy" size={13} />
                  </button>
                </div>
              </div>
            </section>
          )}

          {tab === t('传输') && (
            <section className="wc-section" style={{ marginBottom: 0 }}>
              <div className="wc-info">
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('接收目录')}</span>
                  <span className="wc-info__value wc-info__value--muted">{data?.receivedDirectory ?? '—'}</span>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={!isDesktop() || action.busy}
                    onClick={changeReceivedDirectory}
                  >
                    {t('更改')}
                  </Button>
                </div>
                <div className="wc-field__hint">{t('只影响之后接收的文件，已收到的文件不会搬家。')}</div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('分块大小')}</span>
                  <span className="wc-info__value">{data ? `${Math.round(data.chunkSize / 1024)} KB` : '—'}</span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('单设备并发')}</span>
                  <span className="wc-info__value">{data?.maxActiveTransfers ?? '—'}{t(' 个任务')}</span>
                </div>
              </div>
            </section>
          )}

          {tab === t('外观') && (
            <section className="wc-section" style={{ marginBottom: 0 }}>
              <div className="wc-field">
                <span className="wc-info__label" style={{ width: 72 }}>
                  {t('主题')}
                </span>
                <div style={{ display: 'flex', gap: 6 }}>
                  {(
                    [
                      ['light', t('浅色')],
                      ['dark', t('深色')],
                    ] as const
                  ).map(([key, label]) => (
                    <Button
                      key={key}
                      variant="soft"
                      size="sm"
                      onClick={() => setTheme(key)}
                      style={{ opacity: resolvedTheme === key ? 1 : 0.55 }}
                    >
                      {label}
                    </Button>
                  ))}
                </div>
              </div>
              <div className="wc-field">
                <span className="wc-info__label" style={{ width: 72 }}>
                  {t('强调色')}
                </span>
                <div className="wc-swatches">
                  {ACCENTS.map((key) => (
                    <button
                      key={key}
                      className="wc-swatch"
                      data-on={accent === key}
                      style={{ background: ACCENT_HEX[key] }}
                      aria-label={key}
                      onClick={() => setAccent(key)}
                    />
                  ))}
                </div>
              </div>
            </section>
          )}

          {tab === t('高级') && (
            <section className="wc-section" style={{ marginBottom: 0 }}>
              <div className="wc-info">
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('本机界面地址')}</span>
                  <span className="wc-info__value">https://127.0.0.1:{data?.port ?? '—'}/app/</span>
                </div>
                <div className="wc-info__row">
                  <span className="wc-info__label">{t('调试接口')}</span>
                  <span className="wc-info__value wc-info__value--muted">{t('/app/api（仅 loopback + 本机令牌）')}</span>
                </div>
              </div>
              <div style={{ display: 'flex', gap: 8, marginTop: 14, flexWrap: 'wrap' }}>
                <Button variant="soft" size="sm" disabled={!isDesktop()} onClick={() => action.run(() => openPath(data?.receivedDirectory ?? ''), t('已打开接收目录'))}>
                  <Icon name="folderOpen" size={16} />
                  {t('打开接收目录')}
                </Button>
                <Button variant="soft" size="sm" onClick={() => action.run(() => copyText(`https://127.0.0.1:${data?.port ?? ''}/app/`), t('已复制地址'))}>
                  <Icon name="copy" size={16} />
                  {t('复制界面地址')}
                </Button>
              </div>
            </section>
          )}

          <Notice text={action.notice} />
        </div>
      </div>
    </div>
  );
}
