# BaiduPcs（C# 版百度网盘客户端）

基于 .NET 10 + Avalonia UI 的百度网盘桌面客户端，以 C# 重写 BaiduPCS-Go 的核心能力，提供现代桌面界面与稳定的下载通道。

> **版本：v0.5（MVP）** · 纯 C# 实现 · 支持 Windows（Avalonia 跨平台框架，理论可扩展其他平台）

## 功能特性

### ✅ 已实现（v0.5）
- **BDUSS 登录**：粘贴 BDUSS 直接登录，支持高级模式附加完整 Cookie 串；自动收集/持久化服务器下发 Cookie，登录验证稳定
- **文件浏览**：目录列表、面包屑导航、进入/后退/刷新、新建/重命名/删除、配额显示
- **官方通道下载**：走 PCS `method=download` 接口（服务端签发直链 + 自动跟随重定向），**会员加速在此生效**；支持断点续传（`.part`）与实时进度/速度显示
- **复制下载链接**：独立于下载的"复制链接"按钮，一键获取 locatedownload 签名直链到剪贴板（可发人/手动下载器使用）
- **淡灰蓝浅色系 UI**：柔和配色，低光不刺眼
- **多账号**：账号本地持久化，重启自动恢复登录态

### 计划中
- 多线程分片下载、失败自动重试、传输任务管理（暂停/取消）
- 上传引擎（单文件 → 分片 → 秒传）
- 搜索、回收站、复制/移动、分享
- 设置页（代理/限速/UA）、打包发布

## 技术栈

| 层 | 技术 |
|----|------|
| UI | Avalonia 12.1（跨平台 XAML） |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| 核心 | .NET 10 / C#（async/await） |
| 签名 | System.Security.Cryptography（MD5/SHA1） |

## 项目结构

```
BaiduPcs.slnx
├─ BaiduPcs.Core/            # 核心库（无 UI 依赖）
│  ├─ Api/                   # 百度网盘 API 客户端
│  │  ├─ BaiduPcsApiClient.cs        # 请求管道 / 错误映射
│  │  ├─ BaiduPcsApiClient.Auth.cs   # getinfo / bdstoken
│  │  ├─ BaiduPcsApiClient.FileSystem.cs # 列表/搜索/mkdir/删除/移动
│  │  ├─ BaiduPcsApiClient.Download.cs # 下载直链（locatedownload）
│  │  ├─ PanHomeClient.cs    # 首页解析（sign1/sign3/timestamp）
│  │  └─ ErrorMessageCatalog.cs # 错误码 → 可读消息
│  ├─ Auth/                  # 登录 / 会话 / Cookie
│  ├─ Crypto/                # NetdiskSign（下载签名）/ Md5Obfuscator
│  ├─ Download/              # 下载服务（流式 + 断点续传 + 进度）
│  └─ Infrastructure/        # HTTP 客户端 / 路径常量
├─ BaiduPcs.Core.Tests/      # 单元测试（59 个用例，含密码学 Go 向量验证）
└─ BaiduPcsAPP/              # Avalonia 桌面应用
   ├─ ViewModels/            # 登录 / 文件浏览 / 传输 / 设置
   ├─ Views/                 # XAML 视图
   └─ Services/              # 对话框 / 剪贴板 / 组合根
```

## 快速开始

### 获取 BDUSS
1. 浏览器登录 [pan.baidu.com](https://pan.baidu.com)
2. `F12` → 应用（Application）→ Cookie
3. 复制 `BDUSS` 的值

### 运行
```bash
# 构建
dotnet build BaiduPcsAPP/BaiduPcsAPP.csproj

# 测试
dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj

# 运行
dotnet run --project BaiduPcsAPP/BaiduPcsAPP.csproj
```

## 核心思路

本项目以开源项目 **BaiduPCS-Go** 为协议参考（MIT 社区项目），用 C# 完整重写。关键设计：

- **登录链路**：先访问网盘首页完成 Cookie 初始化（收集 BAIDUID 等），再调 `getinfo` 验证并取 UID——避免裸 BDUSS 请求被风控拒绝
- **UA 策略**：`pan.baidu.com/api/*` 接口必须用 netdisk 客户端 UA（Chrome UA 会被拒）；`pcs.baidu.com/rest/2.0/pcs/*` 用浏览器 UA 即可
- **双下载通道并列**（非自动降级）：
  - 「下载」→ 官方 `method=download`，服务端按账号会员等级签发直链（会员加速）
  - 「复制链接」→ locatedownload 签名直链（独立能力，可分享/手动下载）
- **断点续传**：`.part` 临时文件 + HTTP Range 头，中断后自动续传
- **稳健性**：响应体按字节读取显式 UTF-8 解码（规避百度返回非标准 `charset=utf8` 导致的崩溃）；全局异常日志兜底

## 免责声明

本项目仅用于学习和个人使用。**请勿用于商业用途**。使用本工具可能违反百度网盘服务条款，存在账号风控风险，请自行评估。涉及的一切知识产权归原权利人所有。

## Roadmap（规划）

- **v0.6**：上传引擎（单文件 → 分片 → 秒传）、传输任务管理（暂停/取消/队列）
- **v0.7**：搜索、回收站、复制/移动、多选批处理
- **v0.8**：下载线程池 + 分片加速、失败重试、限速
- **v0.9**：设置页落地（代理/UA/并发）、分享
- **v1.0**：打包发布（Windows 安装包 + 图标 + 托盘 + 自动更新）