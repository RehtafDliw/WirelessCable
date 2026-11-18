import React from 'react';
import ReactDOM from 'react-dom/client';
import { OinkProvider } from 'oink-ui';
import 'oink-ui/styles.css';
import './tokens.css';
import { App } from './App';

const root = document.getElementById('root');
if (root) {
  // OinkProvider 把主题/强调色/动效写到 <html> 的 data-theme / data-accent / data-motion 上，
  // oink-ui 的令牌表全靠这三个属性派生。样张渲染时用 URL 参数直接指定初始状态。
  const params = new URLSearchParams(window.location.search);
  const initialTheme = (params.get('theme') as 'light' | 'dark') ?? 'light';

  ReactDOM.createRoot(root).render(
    <React.StrictMode>
      <OinkProvider
        defaultTheme={initialTheme}
        defaultAccent="blue"
        defaultMotion="full"
        locale="zh-CN"
      >
        <App />
      </OinkProvider>
    </React.StrictMode>,
  );
}
