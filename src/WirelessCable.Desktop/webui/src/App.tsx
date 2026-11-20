import { useEffect, useReducer, useRef, useState } from 'react';
import { Badge, Button, Dialog, useOink } from 'oink-ui';
import { Icon } from './Icon';
import { api, type DevicesResponse, type Status } from './api';
import { host, isDesktop, setWindowTheme } from './host';
import { useApi } from './useApi';
import {
  HomePage,
  DevicesPage,
  FilesPage,
  TransfersPage,
  SharedPage,
  SettingsPage,
  PairingQrDialog,
} from './pages';
import { AndroidHome, StatesBoard } from './mobile';

import { getLang, setLang, subscribeLang, t } from './i18n';
/** 左侧导航项——与 WPF 版 MainViewModel 的分区一一对应 */
const NAV = [
  { key: 'home', zh: '首页', icon: 'home' as const },
  { key: 'files', zh: '手机文件', icon: 'phone' as const },
  { key: 'devices', zh: '设备管理', icon: 'users' as const },
  { key: 'transfers', zh: '传输中心', icon: 'download' as const },
  { key: 'shared', zh: '共享目录', icon: 'folder' as const },
  { key: 'settings', zh: '设置', icon: 'settings' as const },
];

const TITLES: Record<string, { title: string; sub: string }> = {
  home: { title: '首页', sub: '本机与已连接设备' },
  files: { title: '手机文件', sub: '浏览手机里授权给电脑的目录' },
  devices: { title: '设备管理', sub: '已配对的设备与权限' },
  transfers: { title: '传输中心', sub: '进行中与历史任务' },
  shared: { title: '共享目录', sub: '授权给设备的本机目录' },
  settings: { title: '设置', sub: '应用偏好与连接' },
};

export function App() {
  // 语言切换：订阅后强制重渲染，页面与状态都保留（不 remount）
  const [, bumpLang] = useReducer((x: number) => x + 1, 0);
  useEffect(() => subscribeLang(bumpLang), []);

  // 样张渲染用：?page=home&theme=dark 直接定位到某个页面与主题
  const params = new URLSearchParams(window.location.search);
  const view = params.get('view');

  // 手机端与状态板是独立画布，不套桌面外壳
  if (view === 'android') {
    return (
      <div style={{ display: 'grid', placeItems: 'center', minHeight: '100vh', background: 'var(--ok-well)' }}>
        <AndroidHome />
      </div>
    );
  }
  if (view === 'states') {
    return <StatesBoard />;
  }

  const [page, setPage] = useState(params.get('page') ?? 'home');
  const [grantOpen, setGrantOpen] = useState(params.get('dialog') === 'grant');
  const [qrOpen, setQrOpen] = useState(params.get('dialog') === 'qr');
  const [filesDevice, setFilesDevice] = useState('');
  const [nonce, setNonce] = useState(0);

  // 排障自检：?selfcheck=1 时向宿主发一条复制请求，用来验证宿主桥真的通了
  // （剪贴板内容可以在外部读取，是唯一"不靠肉眼"的桥接证据）。
  const [selfCheck, setSelfCheck] = useState<string | null>(null);
  useEffect(() => {
    if (params.get('selfcheck') !== '1') {
      return;
    }

    if (!isDesktop()) {
      setSelfCheck(t('不在桌面窗口里'));
      return;
    }

    host('copyText', { text: 'wc-selfcheck-ok' })
      .then((reply) => setSelfCheck(reply.ok ? t('宿主桥可用') : (reply.error ?? t('失败'))))
      .catch((error: unknown) => setSelfCheck(error instanceof Error ? error.message : String(error)));
  }, [params]);

  const status = useApi<Status>('/status', 5000);
  const devices = useApi<DevicesResponse>('/devices', 5000);
  const pending = devices.data?.pending ?? [];

  // 配对请求一到就弹确认框：用户刚扫完码，下一步本来就是在这台电脑上点「允许」。
  // 同一个 requestId 只自动弹一次（关掉后不再打扰），同时收起二维码，
  // 免得两个对话框叠在一起。旧窗口不再抢焦点，提示只能由这里给。
  const firstPendingId = pending[0]?.requestId ?? null;
  const promptedRequest = useRef<string | null>(null);
  useEffect(() => {
    if (!firstPendingId) {
      promptedRequest.current = null;
      return;
    }

    if (promptedRequest.current === firstPendingId) {
      return;
    }

    promptedRequest.current = firstPendingId;
    setQrOpen(false);
    setGrantOpen(true);
  }, [firstPendingId]);

  // 主题由 OinkProvider 统一管理，切换走它的圆形揭开过渡
  const { resolvedTheme, setTheme } = useOink();

  // 原生标题栏跟着主题走：深色时不至于顶上挂一条白边，浅色时也和侧栏一个色。
  // 必须等一帧再读 CSS 变量——同一帧里 setTheme 还没把变量刷到 :root 上。
  useEffect(() => {
    const id = requestAnimationFrame(() => {
      const style = getComputedStyle(document.documentElement);
      void setWindowTheme(
        resolvedTheme === 'dark' ? 'dark' : 'light',
        style.getPropertyValue('--ok-surface').trim(),
        style.getPropertyValue('--ok-text').trim(),
      );
    });

    return () => cancelAnimationFrame(id);
  }, [resolvedTheme]);

  const meta = TITLES[page] ?? TITLES.home;

  return (
    <div className="wc">
      {/* ================= 左侧导航 ================= */}
      <aside className="wc-side">
        <div className="wc-brand">
          <div className="wc-brand__mark">
            <Icon name="cable" />
          </div>
          <div>
            <div className="wc-brand__name">{t('无线数据线')}</div>
            <div className="wc-brand__ver">{status.data?.version ?? '—'}</div>
          </div>
        </div>

        <nav className="wc-nav" aria-label={t('主导航')}>
          {NAV.map((item) => (
            <button
              key={item.key}
              className="wc-navitem"
              data-current={page === item.key}
              onClick={() => setPage(item.key)}
            >
              <Icon name={item.icon} />
              <span>{t(item.zh)}</span>
              {item.key === 'devices' && pending.length > 0 && (
                <span className="wc-navitem__badge">{pending.length}</span>
              )}
            </button>
          ))}

          <div className="wc-nav__sep" />

          <button className="wc-navitem" onClick={() => setGrantOpen(true)}>
            <Icon name="shield" />
            <span>{t('设备授权')}</span>
            {pending.length > 0 && <span className="wc-navitem__badge">{pending.length}</span>}
          </button>
        </nav>

        <button
          onClick={() => setLang(getLang() === 'zh' ? 'en' : 'zh')}
          style={{
            margin: '8px 16px 12px',
            padding: '6px 10px',
            border: '1px solid var(--ok-line)',
            borderRadius: 8,
            background: 'transparent',
            color: 'var(--ok-text-2)',
            cursor: 'pointer',
            font: '500 12px/1 var(--ok-font)',
            textAlign: 'left',
          }}
          title={getLang() === 'zh' ? 'Switch to English' : '切换到中文'}
        >
          {getLang() === 'zh' ? 'English' : '中文'}
        </button>

        {/* 手机浏览器访问这台电脑就能下安装包 —— 不用把 APK 传来传去 */}
        <a
          href="/download/android"
          download="WirelessCable.apk"
          style={{
            margin: '0 16px 8px',
            color: 'var(--ok-text-2)',
            font: '500 12px/1 var(--ok-font)',
            textDecoration: 'underline',
          }}
          title={t('在手机上打开这个链接，下载并安装手机版')}
        >
          {t('下载手机版')}
        </a>

        <div className="wc-status">
          <span className="wc-status__dot" />
          <div className="wc-status__text">
            <b>{status.data?.state === 'Running' ? t('服务运行中') : (status.error ?? t('读取中'))}</b>
            {status.data?.addresses[0] ?? '127.0.0.1'}
            {status.data?.port ? `:${status.data.port}` : ''}
          </div>
        </div>
      </aside>

      {/* ================= 主区 ================= */}
      <div className="wc-main">
        <header className="wc-topbar">
          <div>
            <div className="wc-topbar__title">{t(meta.title)}</div>
            <div className="wc-topbar__sub">{t(meta.sub)}</div>
          </div>
          <div className="wc-topbar__right">
            <Button variant="soft" size="sm" onClick={() => setQrOpen(true)}>
              <Icon name="qr" size={16} />
              {t('配对码')}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              iconOnly
              aria-label={t('切换主题')}
              onClick={() => setTheme(resolvedTheme === 'light' ? 'dark' : 'light')}
            >
              <Icon name={resolvedTheme === 'light' ? 'moon' : 'sun'} />
            </Button>
            <Button
              variant="ghost"
              size="sm"
              iconOnly
              aria-label={t('刷新')}
              onClick={() => {
                setNonce((n) => n + 1);
                devices.reload();
                status.reload();
              }}
            >
              <Icon name="refresh" />
            </Button>
          </div>
        </header>

        <div className="wc-scroll" key={nonce}>
          {page === 'home' && (
            <HomePage
              onNavigate={setPage}
              onShowQr={() => setQrOpen(true)}
              onVisit={(deviceId) => {
                setFilesDevice(deviceId);
                setPage('files');
              }}
              autoSend={Number(params.get('sendtest') ?? 0)}
            />
          )}
          {page === 'files' && <FilesPage deviceId={filesDevice} onDeviceChange={setFilesDevice} />}
          {page === 'devices' && <DevicesPage />}
          {page === 'transfers' && <TransfersPage />}
          {page === 'shared' && <SharedPage openEditor={params.get('dialog') === 'root'} />}
          {page === 'settings' && <SettingsPage initialTab={params.get('tab') ?? t('常规')} />}
        </div>
      </div>

      {selfCheck && (
        <div
          style={{
            position: 'fixed',
            left: 12,
            bottom: 12,
            padding: '6px 10px',
            borderRadius: 8,
            background: 'var(--ok-well)',
            color: 'var(--ok-text-2)',
            font: '500 12px/1.4 var(--ok-font)',
            boxShadow: 'inset 0 0 0 1px var(--ok-line)',
            zIndex: 50,
          }}
        >
          {t('自检：')}{selfCheck}
        </div>
      )}

      <GrantDialog open={grantOpen} onOpenChange={setGrantOpen} pending={pending} onDone={devices.reload} />
      <PairingQrDialog open={qrOpen} onOpenChange={setQrOpen} />
    </div>
  );
}

/* ===================== 设备授权对话框 ===================== */

function GrantDialog({
  open,
  onOpenChange,
  pending,
  onDone,
}: {
  open: boolean;
  onOpenChange: (value: boolean) => void;
  pending: DevicesResponse['pending'];
  onDone: () => void;
}) {
  const [error, setError] = useState<string | null>(null);
  const request = pending[0];

  const decide = async (ok: boolean) => {
    if (!request) {
      return;
    }

    setError(null);
    try {
      await api.post(`/pairing/${request.requestId}/${ok ? 'approve' : 'reject'}`);
      onDone();
      onOpenChange(false);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      size={460}
      icon={<Icon name="shield" />}
      tone="accent"
      title={request ? t('有设备请求配对') : t('没有待授权的请求')}
      description={
        request
          ? `${request.deviceName} · ${request.platform}`
          : t('手机发起配对后，这里会出现六位校验码。')
      }
      actions={
        request ? (
          <>
            <Button variant="ghost" onClick={() => decide(false)}>
              {t('拒绝')}
            </Button>
            <Button variant="accent" onClick={() => decide(true)}>
              {t('允许访问')}
            </Button>
          </>
        ) : (
          <Button variant="soft" onClick={() => onOpenChange(false)}>
            {t('关闭')}
          </Button>
        )
      }
    >
      {request ? (
        <div style={{ display: 'grid', gap: 8, marginTop: 4 }}>
          <div style={{ font: '450 12.5px/1.5 var(--ok-font)', color: 'var(--ok-text-3)' }}>
            {t('核对手机上的六位码与下面一致再允许。')}
          </div>
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              padding: '14px 0',
              borderRadius: 'calc(12px * var(--ok-rk))',
              background: 'var(--ok-well)',
              boxShadow: 'inset 0 0 0 1px var(--ok-line)',
              font: '600 26px/1 var(--ok-font-mono)',
              letterSpacing: 8,
              color: 'var(--ok-text)',
            }}
          >
            {request.code}
          </div>
          {pending.length > 1 && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Badge tone="warning">{t('还有 ')}{pending.length - 1}{t(' 个请求排队')}</Badge>
            </div>
          )}
          {error && (
            <div style={{ font: '450 12.5px/1.5 var(--ok-font)', color: 'var(--ok-danger-text)' }}>{error}</div>
          )}
        </div>
      ) : null}
    </Dialog>
  );
}
