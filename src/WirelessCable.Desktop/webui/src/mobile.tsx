import { Badge, Button, Card, CardHeader, Progress, Stat, Switch, TabBar, TabBarItem } from 'oink-ui';
import { Icon } from './Icon';

import { t } from './i18n';
/* ===================== Android 端（手机尺寸） ===================== */

/**
 * 手机端沿用同一套 oink-ui 组件与令牌，只是换成窄屏布局：
 * 底部 TabBar + 大标题 + 卡片流。
 */
export function AndroidHome() {
  return (
    <div
      style={{
        width: 390,
        height: 844,
        display: 'flex',
        flexDirection: 'column',
        background: 'var(--ok-bg)',
        color: 'var(--ok-text)',
        fontFamily: 'var(--ok-font)',
        overflow: 'hidden',
      }}
    >
      {/* 状态栏 */}
      <div style={{ height: 30, flex: 'none', display: 'flex', alignItems: 'center', padding: '0 20px', fontSize: 12, fontWeight: 500 }}>
        <span>9:41</span>
        <span style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 5 }}>
          <svg width="17" height="10" viewBox="0 0 17 10" fill="none" stroke="currentColor" strokeWidth="1.1">
            <rect x="0.5" y="0.5" width="13" height="9" rx="2" />
            <rect x="2" y="2" width="9" height="6" rx="1" fill="currentColor" stroke="none" />
            <path d="M15 3.5v3" strokeWidth="2" strokeLinecap="round" />
          </svg>
          <svg width="14" height="10" viewBox="0 0 14 10" fill="currentColor">
            <path d="M7 0L0 10h14z" opacity="0.9" />
          </svg>
        </span>
      </div>

      {/* 标题区 */}
      <div style={{ flex: 'none', padding: '10px 20px 14px' }}>
        <div style={{ font: '700 26px/1.2 var(--ok-font-display)', letterSpacing: '-0.02em' }}>{t('首页')}</div>
        <div style={{ font: '500 13px/1.4 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 3 }}>
          {t('你的设备，随时互联')}
        </div>
      </div>

      {/* 内容 */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '0 16px 16px' }}>
        {/* 连接状态卡 */}
        <Card size="sm" style={{ marginBottom: 12 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 11 }}>
            <div
              style={{
                width: 38,
                height: 38,
                flex: 'none',
                display: 'grid',
                placeItems: 'center',
                borderRadius: 'calc(12px * var(--ok-rk))',
                background: 'var(--ok-accent-soft)',
                color: 'var(--ok-accent)',
              }}
            >
              <Icon name="router" size={20} />
            </div>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{ font: '600 14px/1.3 var(--ok-font)' }}>{t('无线数据线')}</div>
              <div style={{ font: '450 12px/1.4 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 2 }}>
                192.168.1.24:52418
              </div>
            </div>
            <Badge tone="success" dot>
              {t('在线')}
            </Badge>
          </div>
        </Card>

        {/* 指标 */}
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, marginBottom: 12 }}>
          <Stat label={t('进行中')} value={3} hint={t('共 148 MB')} variant="card" />
          <Stat label={t('今日完成')} value={27} trend="up" delta="+6" variant="card" />
        </div>

        {/* 设备列表 */}
        <div style={{ font: '700 15px/1.3 var(--ok-font)', marginBottom: 9 }}>{t('我的设备')}</div>
        {[
          { icon: 'phone', name: 'Pixel 8 Pro', meta: 'Android 15', tone: 'success' as const, label: t('在线') },
          { icon: 'laptop', name: 'iPad Air', meta: 'iPadOS 18', tone: 'neutral' as const, label: t('离线') },
        ].map((d, i) => (
          <div
            key={d.name}
            className="wc-anim-item"
            style={{
              animationDelay: `${i * 70}ms`,
              display: 'flex',
              alignItems: 'center',
              gap: 11,
              padding: '12px 13px',
              marginBottom: 8,
              borderRadius: 'calc(14px * var(--ok-rk))',
              background: 'var(--ok-card)',
              boxShadow: 'inset 0 0 0 1px var(--ok-line)',
            }}
          >
            <div
              style={{
                width: 34,
                height: 34,
                flex: 'none',
                display: 'grid',
                placeItems: 'center',
                borderRadius: 'calc(10px * var(--ok-rk))',
                background: 'var(--ok-well)',
                color: 'var(--ok-text-2)',
              }}
            >
              <Icon name={d.icon} size={17} />
            </div>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{ font: '600 13.5px/1.3 var(--ok-font)' }}>{d.name}</div>
              <div style={{ font: '450 11.5px/1.4 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 2 }}>{d.meta}</div>
            </div>
            <Badge tone={d.tone} dot={d.tone === 'success'}>
              {d.label}
            </Badge>
          </div>
        ))}

        {/* 传输 */}
        <div style={{ font: '700 15px/1.3 var(--ok-font)', margin: '16px 0 9px' }}>{t('传输')}</div>
        <div
          style={{
            padding: '13px',
            borderRadius: 'calc(14px * var(--ok-rk))',
            background: 'var(--ok-card)',
            boxShadow: 'inset 0 0 0 1px var(--ok-line)',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
            <div style={{ font: '600 13.5px/1.3 var(--ok-font)', minWidth: 0, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              IMG_20241116_091233.heic
            </div>
            <div style={{ font: '500 12px/1 var(--ok-font-mono)', fontVariantNumeric: 'tabular-nums', color: 'var(--ok-text-2)' }}>
              6.2 MB/s
            </div>
          </div>
          <div style={{ font: '450 11.5px/1.4 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 3 }}>
            {t('Pixel 8 Pro → 本机')}
          </div>
          <div className="wc-progress" style={{ marginTop: 9 }}>
            <div className="wc-progress__bar" style={{ width: '64%' }} />
          </div>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 7 }}>
            <span style={{ font: '450 11.5px/1 var(--ok-font-mono)', color: 'var(--ok-text-3)' }}>24.6 / 38.1 MB</span>
            <span style={{ font: '600 11.5px/1 var(--ok-font)', color: 'var(--ok-accent-text)' }}>64%</span>
          </div>
        </div>

        {/* 快捷操作 */}
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8, marginTop: 12 }}>
          <Button variant="accent" size="md" block leading={<Icon name="download" size={16} />}>
            {t('发送文件')}
          </Button>
          <Button variant="soft" size="md" block leading={<Icon name="qr" size={16} />}>
            {t('配对码')}
          </Button>
        </div>
      </div>

      {/* 底部导航 */}
      <div
        style={{
          flex: 'none',
          display: 'flex',
          borderTop: '1px solid var(--ok-line)',
          background: 'var(--ok-surface)',
          paddingBottom: 14,
        }}
      >
        {[
          { icon: 'home', label: t('首页'), on: true },
          { icon: 'download', label: t('传输'), on: false },
          { icon: 'folder', label: t('文件'), on: false },
          { icon: 'settings', label: t('设置'), on: false },
        ].map((t) => (
          <div
            key={t.label}
            style={{
              flex: 1,
              display: 'flex',
              flexDirection: 'column',
              alignItems: 'center',
              gap: 3,
              paddingTop: 9,
              color: t.on ? 'var(--ok-accent)' : 'var(--ok-text-3)',
            }}
          >
            <Icon name={t.icon} size={20} />
            <span style={{ font: '500 11px/1 var(--ok-font)' }}>{t.label}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/* ===================== 组件状态板（给实现用） ===================== */

export function StatesBoard() {
  return (
    <div
      style={{
        padding: 28,
        background: 'var(--ok-bg)',
        color: 'var(--ok-text)',
        fontFamily: 'var(--ok-font)',
        minHeight: '100%',
      }}
    >
      <div style={{ font: '700 19px/1.2 var(--ok-font-display)', letterSpacing: '-0.015em', marginBottom: 4 }}>
        {t('控件状态板')}
      </div>
      <div style={{ font: '450 12.5px/1.5 var(--ok-font)', color: 'var(--ok-text-3)', marginBottom: 20 }}>
        {t('路线 C 落地的组件规格：每个控件的变体、状态与尺寸')}
      </div>

      {/* 两栏排布：820px 高度内放得下全部规格 */}
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '14px 30px', alignItems: 'start' }}>
        <Row title={t('按钮变体')}>
          <Button variant="primary">{t('主要')}</Button>
          <Button variant="accent">{t('强调')}</Button>
          <Button variant="soft">{t('次要')}</Button>
          <Button variant="outline">{t('描边')}</Button>
          <Button variant="ghost">{t('幽灵')}</Button>
          <Button variant="danger">{t('危险')}</Button>
        </Row>

        <Row title={t('按钮尺寸与状态')}>
          <Button size="sm" variant="soft">
            {t('小 30')}
          </Button>
          <Button size="md" variant="soft">
            {t('中 36')}
          </Button>
          <Button size="lg" variant="soft">
            {t('大 44')}
          </Button>
          <Button variant="accent" loading>
            {t('加载')}
          </Button>
          <Button variant="accent" success>
            {t('完成')}
          </Button>
          <Button variant="soft" disabled>
            {t('禁用')}
          </Button>
        </Row>

        <Row title={t('状态标记')}>
          <Badge tone="success" dot>
            {t('在线')}
          </Badge>
          <Badge tone="warning" ping>
            {t('待授权')}
          </Badge>
          <Badge tone="danger">{t('已断开')}</Badge>
          <Badge tone="info">{t('传输中')}</Badge>
          <Badge tone="neutral">{t('离线')}</Badge>
          <Badge tone="accent" variant="solid">
            {t('2 台')}
          </Badge>
        </Row>

        <Row title={t('输入控件')}>
          <div style={{ width: 210 }}>
            <input
              placeholder={t('搜索设备或文件')}
              style={{
                width: '100%',
                height: 36,
                padding: '0 14px',
                borderRadius: 'var(--ok-r-full)',
                border: '1.5px solid var(--ok-line-2)',
                background: 'var(--ok-card)',
                color: 'var(--ok-text)',
                font: '400 14px/1 var(--ok-font)',
                outline: 'none',
              }}
            />
          </div>
          <Switch defaultChecked aria-label={t('开关')} />
          <Switch aria-label={t('开关')} />
        </Row>

        <Row title={t('进度')}>
          <div style={{ width: 210, display: 'grid', gap: 9 }}>
            <Progress value={64} label={t('进行中')} />
            <Progress value={100} label={t('已完成')} />
            <Progress label={t('不确定进度')} />
          </div>
        </Row>

        <Row title={t('排版阶梯')}>
          <div style={{ display: 'grid', gap: 5 }}>
            <div style={{ font: '700 24px/1.2 var(--ok-font-display)', letterSpacing: '-0.02em' }}>{t('页面标题 24')}</div>
            <div style={{ font: '700 19px/1.25 var(--ok-font-display)', letterSpacing: '-0.015em' }}>{t('区块标题 19')}</div>
            <div style={{ font: '600 15px/1.3 var(--ok-font)' }}>{t('小节标题 15')}</div>
            <div style={{ font: '450 13.5px/1.4 var(--ok-font)' }}>{t('正文 13.5')}</div>
            <div style={{ font: '450 12px/1.4 var(--ok-font)', color: 'var(--ok-text-3)' }}>{t('辅助说明 12')}</div>
            <div
              style={{
                font: '500 12px/1 var(--ok-font-mono)',
                fontVariantNumeric: 'tabular-nums',
                color: 'var(--ok-text-2)',
              }}
            >
              {t('等宽数字 12 · 8.4 MB/s')}
            </div>
          </div>
        </Row>

        <Row title={t('间距与圆角')}>
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end' }}>
            {[
              { r: '8px', l: 'r1' },
              { r: '12px', l: 'r2' },
              { r: '16px', l: 'r3' },
              { r: '24px', l: 'r4' },
              { r: '32px', l: 'r5' },
              { r: '999px', l: 'full' },
            ].map((x) => (
              <div key={x.l} style={{ textAlign: 'center' }}>
                <div
                  style={{
                    width: 56,
                    height: 40,
                    display: 'grid',
                    placeItems: 'center',
                    borderRadius: `calc(${x.r} * var(--ok-rk))`,
                    background: 'var(--ok-accent-soft)',
                    color: 'var(--ok-accent-text)',
                    font: '600 11.5px/1 var(--ok-font-mono)',
                  }}
                >
                  {x.l}
                </div>
                <div style={{ font: '450 10px/1 var(--ok-font-mono)', color: 'var(--ok-text-3)', marginTop: 5 }}>{x.r}</div>
              </div>
            ))}
          </div>
        </Row>

        <Row title={t('强调色')}>
          <div style={{ display: 'flex', gap: 8 }}>
            {[
              { n: t('蓝'), c: '#2E7BFF' },
              { n: t('粉'), c: '#FF6B8E' },
              { n: t('橙'), c: '#FF7A2F' },
              { n: t('琥珀'), c: '#FFBE3B' },
              { n: t('紫'), c: '#C77DFF' },
            ].map((x) => (
              <div key={x.n} style={{ textAlign: 'center' }}>
                <div style={{ width: 46, height: 46, borderRadius: 'calc(13px * var(--ok-rk))', background: x.c }} />
                <div style={{ font: '450 10px/1 var(--ok-font)', color: 'var(--ok-text-3)', marginTop: 5 }}>{x.n}</div>
              </div>
            ))}
          </div>
        </Row>

        <Row title={t('表面层级')}>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8, width: 460 }}>
            {[
              { n: 'bg', v: 'var(--ok-bg)' },
              { n: 'surface', v: 'var(--ok-surface)' },
              { n: 'card', v: 'var(--ok-card)' },
              { n: 'well', v: 'var(--ok-well)' },
            ].map((x) => (
              <div key={x.n} style={{ textAlign: 'center' }}>
                <div
                  style={{
                    height: 48,
                    borderRadius: 'calc(11px * var(--ok-rk))',
                    background: x.v,
                    boxShadow: 'inset 0 0 0 1px var(--ok-line-2)',
                  }}
                />
                <div style={{ font: '450 10px/1 var(--ok-font-mono)', color: 'var(--ok-text-3)', marginTop: 5 }}>{x.n}</div>
              </div>
            ))}
          </div>
        </Row>
      </div>

      <div style={{ marginTop: 4 }}>
        <Row title={t('动效曲线（oink-ui 的 5 套弹簧）')}>
          <div style={{ display: 'flex', gap: 22, flexWrap: 'wrap' }}>
            {[
              { n: t('soft（默认）'), d: '587ms · cubic-bezier(.22,1,.36,1)' },
              { n: 'spring', d: t('454ms · 过冲 1.03') },
              { n: 'snappy', d: t('325ms · 过冲 1.03') },
              { n: 'bouncy', d: t('542ms · 过冲 1.15') },
              { n: 'wobbly', d: t('1104ms · 过冲 1.33') },
            ].map((x) => (
              <div key={x.n} style={{ display: 'grid', gap: 2 }}>
                <span style={{ font: '600 12px/1.3 var(--ok-font)' }}>{x.n}</span>
                <span style={{ font: '450 11px/1.3 var(--ok-font-mono)', color: 'var(--ok-text-3)' }}>{x.d}</span>
              </div>
            ))}
          </div>
        </Row>
      </div>
    </div>
  );
}

function Row({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={{ marginBottom: 24 }}>
      <div
        style={{
          font: '700 12px/1.3 var(--ok-font)',
          letterSpacing: '0.06em',
          textTransform: 'uppercase',
          color: 'var(--ok-text-3)',
          marginBottom: 11,
        }}
      >
        {title}
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>{children}</div>
    </div>
  );
}
