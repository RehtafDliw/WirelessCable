# 第三方组件与许可声明

本项目以 [Apache-2.0](LICENSE) 发布。下表列出运行时会用到的第三方组件；
每项的完整许可原文可在对应包的发布页或 `node_modules/<包名>/LICENSE` 中找到。

生成日期：2026-11-19（对应版本 2.10.2）。

---

## 一、.NET（NuGet）

| 包 | 版本 | 用途 | 许可证 |
| --- | --- | --- | --- |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | 9.0.0 | SQLite 访问 | MIT |
| [Serilog](https://www.nuget.org/packages/Serilog) | 4.2.0 | 结构化日志 | Apache-2.0 |
| [Serilog.Sinks.File](https://www.nuget.org/packages/Serilog.Sinks.File) | 6.0.0 | 文件日志接收器 | Apache-2.0 |
| [Serilog.AspNetCore](https://www.nuget.org/packages/Serilog.AspNetCore) | 9.0.0 | ASP.NET Core 集成 | Apache-2.0 |
| [Makaretu.Dns.Multicast](https://www.nuget.org/packages/Makaretu.Dns.Multicast) | 0.27.0 | mDNS / DNS-SD 发布 | MIT |
| [QRCoder](https://www.nuget.org/packages/QRCoder) | 1.6.0 | 配对二维码生成 | MIT |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData) | 9.0.0 | Windows DPAPI | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | 1.0.2903.40 | 内嵌浏览器内核 | 微软软件许可条款（可随应用再分发） |

测试专用（不进入发布产物）：

| 包 | 版本 | 许可证 |
| --- | --- | --- |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |

---

## 二、Android（Gradle）

| 组件 | 版本 | 用途 | 许可证 |
| --- | --- | --- | --- |
| androidx.compose（BOM） | 2024.09.03 | Jetpack Compose | Apache-2.0 |
| androidx.compose.material3 | 随 BOM | Material 3 组件 | Apache-2.0 |
| androidx.compose.material:material-icons-core | 随 BOM | 图标 | Apache-2.0 |
| androidx.activity:activity-compose | 1.9.2 | Compose 宿主 | Apache-2.0 |
| androidx.lifecycle:* | 2.8.6 | ViewModel / 生命周期 | Apache-2.0 |
| androidx.room:* | 2.6.1 | 本地数据库 | Apache-2.0 |
| androidx.camera:* | 1.4.2 | 扫码预览 | Apache-2.0 |
| com.google.zxing:core | 3.5.3 | 二维码解码 | Apache-2.0 |
| com.squareup.okhttp3:okhttp | 4.12.0 | HTTP 客户端 | Apache-2.0 |
| org.jetbrains.kotlinx:kotlinx-coroutines-android | 1.8.1 | 协程 | Apache-2.0 |
| org.jetbrains.kotlinx:kotlinx-serialization-json | 1.7.3 | JSON 序列化 | Apache-2.0 |

测试专用：

| 组件 | 版本 | 许可证 |
| --- | --- | --- |
| junit:junit | 4.13.2 | EPL-1.0 |
| org.jetbrains.kotlinx:kotlinx-coroutines-test | 1.8.1 | Apache-2.0 |

---

## 三、前端（npm）

| 包 | 版本 | 用途 | 许可证 | 版权 |
| --- | --- | --- | --- | --- |
| [react](https://www.npmjs.com/package/react) | 18.3.1 | UI 框架 | MIT | Copyright (c) Facebook, Inc. and its affiliates |
| [react-dom](https://www.npmjs.com/package/react-dom) | 18.3.1 | DOM 渲染 | MIT | Copyright (c) Facebook, Inc. and its affiliates |
| [oink-ui](https://github.com/oinkvm/oink-ui) | 1.0.11 | 组件库与图标 | MIT | Copyright (c) 2026 Hachiwii |
| [vite](https://www.npmjs.com/package/vite) | 8.3.8 | 构建工具 | MIT | — |
| [@vitejs/plugin-react](https://www.npmjs.com/package/@vitejs/plugin-react) | 6.3.0 | React 插件 | MIT | — |
| [typescript](https://www.npmjs.com/package/typescript) | 7.0.2 | 类型检查 | Apache-2.0 | — |

### 关于 oink-ui

Web 界面使用 [`oink-ui`](https://github.com/oinkvm/oink-ui)（**MIT**）的组件与图标，
按 MIT 要求在此保留其版权声明：

> MIT License — Copyright (c) 2026 Hachiwii

它与 Oink 主仓库（GPL-3.0）是**两个独立项目**，本项目只依赖 npm 上的 MIT 包，
不含、也不链接其 GPL 代码。

### 关于 Microsoft.Web.WebView2

WebView2 SDK 采用微软软件许可条款，允许随应用程序分发运行时与 SDK 二进制。
分发本项目构建产物时，请保留该许可声明。

---

## 四、不是依赖、但需要说明的

| 项目 | 关系 |
| --- | --- |
| [Oink](https://github.com/oinkvm/oink) | 仅作 UI 设计参考（配色与图标圆角参数的思路）。仓库**不包含**其源码，构建也不依赖它 |
| Segoe MDL2 Assets / Segoe Fluent Icons | Windows 系统字体，界面图标字体直接用系统自带的，未随包分发字体文件 |

---

## 五、本项目自己的许可

```
Copyright 2026 The WirelessCable Authors

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
```

完整文本见仓库根目录的 [LICENSE](LICENSE)。
