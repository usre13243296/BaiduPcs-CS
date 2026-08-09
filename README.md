# BaiduPcs-CS（C# 版百度网盘客户端）

基于 .NET 10 + Avalonia UI 的百度网盘桌面客户端，以 C# 重写 BaiduPCS-Go 的核心能力，提供现代桌面界面与稳定的下载通道。**Core 完全解耦**——同一份核心代码可接 Avalonia 桌面（Windows / Linux / macOS）、Linux ARM（NAS/软路由），未来可直出安卓（MAUI）与 Web 管理界面。

> **版本：v0.6.0** · 纯 C# 实现 · 发布包支持 Windows / Linux / macOS（Avalonia 跨平台框架）

![badges](https://img.shields.io/badge/.NET-10.0-512BD4)
![badges](https://img.shields.io/badge/license-MIT-blue)
![badges](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)
![badges](https://img.shields.io/badge/tests-162%20passing-brightgreen)

<img width="1488" height="982" alt="屏幕截图 2026-08-08 055921" src="https://github.com/user-attachments/assets/c6a07365-6dd8-4b26-8444-5f1919bf6175" />

## 功能特性

### ✅ 已实现
- **BDUSS 登录**：粘贴 BDUSS 直接登录，支持高级模式附加完整 Cookie 串；自动收集/持久化服务器下发 Cookie，登录验证稳定
- **文件浏览**：目录列表、面包屑导航、进入/后退/刷新、新建/重命名/删除、配额显示
- **多线程分片下载**：locatedownload 签名直链为主通道（多备选链接）+ method=download 官方通道自动降级；分片并发（统一 8 并发，会员实测 8 并发 ≈ 20MB/s 与官方客户端一致），`WriteAt` 绝对偏移写免合并
- **断点续传**：`.part.json` 记录各分片剩余范围，中断/取消后自动恢复；失败自动重试（429/509 限流退避重连、断流读超时重连、403 自动换直链）
- **分片上传引擎**：秒传预检（precreate 二合一，`return_type=2` 直完成）→ 分片并发上传（superfile2，统一 4 并发，4MB/16MB/64MB 分片档位）→ 合并（create）；全文件 MD5 / 前 256KB MD5 / 逐块 MD5 单趟流式计算；同名文件自动递增重命名（`报告.txt` → `报告2.txt` → …）；进程内断点续传（uploadid + 已传分片跳过，31363 会话失效自动整体重传）；动态上传服务器（locateupload）
- **传输任务管理**：队列调度（同时传输 2 个）、暂停/恢复/取消/删除、同路径去重、失败重试、错误详情展示
- **复制下载链接**：独立于下载的"复制链接"按钮，一键获取 locatedownload 签名直链到剪贴板（可发人/手动下载器使用）
- **淡灰蓝浅色系 UI**：柔和配色，低光不刺眼
- **多账号**：账号本地持久化，重启自动恢复登录态

### 计划中
- 搜索、回收站、复制/移动、分享
- 下载限速、设置页（代理/限速/UA）
- 目录递归上传、跨进程重启恢复

## 架构：Core 是独立后端

`BaiduPcs.Core` 从设计之初就是与 UI 完全解耦的「网盘协议内核」，具备三个特点：

1. **零 UI 依赖** —— `BaiduPcs.Core` 零外部 NuGet 包、不引用 Avalonia；所有 UI 交互（对话框/剪贴板/文件选择）都通过 APP 层的 `IDialogService` 抽象隔离。同一份核心代码可以接任意前端：Avalonia 桌面、MAUI 移动端、Web 管理界面（NAS/软路由），扩展前端**无需改动任何协议与引擎代码**。
2. **协议与引擎完整** —— 登录（`Auth/`，含 Cookie 收集/持久化与 BDUSS 会话）、API 客户端（`Api/`，请求管道/错误映射/各接口封装）、下载引擎（`Download/`，纯 HTTP 直链，只吃 `Urls + CookieHeader`）、上传引擎（`Upload/`，秒传/分片/断点）全部位于 Core。
3. **引擎可独立测** —— **162 个单元/集成测试不依赖任何 UI 框架**：密码学算法对齐 Go 测试向量、协议层用 mock handler 验证参数、引擎层用本地测试服务器验证并发/断点/重试/暂停恢复。

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
├─ BaiduPcs.Core/            # 核心库（无 UI 依赖，可独立接其他前端）
│  ├─ Api/                   # 百度网盘 API 客户端
│  │  ├─ BaiduPcsApiClient.cs        # 请求管道 / 错误映射
│  │  ├─ BaiduPcsApiClient.Auth.cs   # getinfo / bdstoken
│  │  ├─ BaiduPcsApiClient.FileSystem.cs # 列表/搜索/mkdir/删除/移动
│  │  ├─ BaiduPcsApiClient.Download.cs # 下载直链（locatedownload）
│  │  ├─ BaiduPcsApiClient.Upload.cs # 上传（precreate/superfile2/create/locateupload）
│  │  ├─ PanHomeClient.cs    # 首页解析（sign1/sign3/timestamp）
│  │  └─ ErrorMessageCatalog.cs # 错误码 → 可读中文消息
│  ├─ Auth/                  # 登录 / 会话 / Cookie 收集与持久化
│  ├─ Crypto/                # NetdiskSign（下载签名）/ Md5Obfuscator
│  ├─ Download/              # 下载引擎（多线程分片 + 断点续传 + 重试 + 暂停/取消）
│  ├─ Upload/                # 上传引擎（秒传 + 分片 + 进程内断点 + 重名递增）
│  ├─ Transfer/              # ITransferEngine（下载/上传共用接口）
│  └─ Infrastructure/        # HTTP 客户端 / 路径常量 / multipart 构造
├─ BaiduPcs.Core.Tests/      # 单元 + 集成测试（162 个用例，含密码学 Go 向量验证）
└─ BaiduPcsAPP/              # Avalonia 桌面应用（可替换为任意前端）
   ├─ ViewModels/            # 登录 / 文件浏览 / 传输 / 设置
   ├─ Views/                 # XAML 视图
   └─ Services/              # 对话框 / 剪贴板 / 组合根 / 传输管理器
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

### 打包发布（多平台单文件）

```bash
dotnet publish BaiduPcsAPP/BaiduPcsAPP.csproj -c Release -r <RID> \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/<RID>
```

支持 RID：`win-x64` / `win-arm64` / `linux-x64` / `linux-arm64` / `osx-x64` / `osx-arm64`。

发布包注意事项：
- **Windows**：直接运行 `BaiduPcsAPP.exe`（单文件，免安装）
- **macOS**：未签名应用首次运行需右键 → 打开（Gatekeeper）；如遇"已损坏"提示执行 `xattr -cr BaiduPcsAPP`
- **Linux**：需系统安装 X11/Wayland 与 OpenGL 依赖（Avalonia 运行时要求，如 `libX11 libxcb libGL` 等）
- **Linux/macOS**：Windows 侧打包的 zip 不保留 Unix 执行位，解压后需 `chmod +x BaiduPcsAPP`

## 核心思路

本项目以开源项目 **BaiduPCS-Go** 为协议参考（MIT 社区项目），用 C# 完整重写。关键设计：

### 登录与认证
- **登录链路**：先访问网盘首页完成 Cookie 初始化（收集 BAIDUID 等），再调 `getinfo` 验证并取 UID——避免裸 BDUSS 请求被风控拒绝
- **UA 策略**：`pan.baidu.com/api/*` 接口必须用 netdisk 客户端 UA（Chrome UA 会被拒）；`pcs.baidu.com/rest/2.0/pcs/*` 用浏览器 UA 即可
- **UA 发送坑**：`netdisk;P2SP;...` 是非标准 UA 格式，必须 `TryAddWithoutValidation` 原样发送，否则被静默丢弃导致 `errno=2`

### 下载
- **双下载通道并列**（非自动降级）：
  - 「下载」→ locatedownload 签名直链为主通道（多备选链接），失败自动降级官方 `method=download`（服务端按账号会员等级签发直链）
  - 「复制链接」→ locatedownload 签名直链（独立能力，可分享/手动下载）
- **多线程分片**：统一 8 并发 + `WriteAt` 绝对偏移写免合并；大小探测请求复用为 0 号分片（省一次请求）
- **断点续传**：`.part` 数据文件 + `.part.json` 断点元数据（各分片剩余范围），监控循环每秒持久化，中断/取消后自动恢复
- **错误分类**：403/404/416 致命换直链；429/509 限流指数退避重连；断流读超时（30s）重连；备选直链逐条尝试
- **超时坑**：`HttpClient.Timeout` 覆盖整个请求，50s 总超时会截断大文件下载——下载引擎使用独立无限超时 HttpClient，由 `CancellationToken` 负责取消

### 上传
- **秒传与 precreate 二合一**：一次请求拿 `return_type` + `uploadid`；秒传命中（`return_type=2`）进度瞬间读满并标注「秒传命中」，与真实上传一眼区分
- **三份校验和单趟流式计算**：全文件 MD5（content-md5）+ 前 256KB MD5（slice-md5）+ 逐块 MD5（block_list），一次读文件同时累计
- **防滥用随机片段**：`data_offset = md5("{uk}{md5}{时间}") 前 8 位 hex 取模`，`data_content` 为偏移处 4KB 内容 base64 去 `=`
- **重名自动递增**：`报告.txt` 已存在 → `报告2.txt` → `报告3.txt`…
- **进程内断点**：复用 uploadid + 已传分片跳过；31363 会话失效自动清空整体重传一轮；上传 host 经 locateupload 动态获取（失败降级 pcs.baidu.com）
- **进度/速度统计**：周期采样模型（1s 重算，杜绝瞬时速度爆炸）；监控心跳时间驱动刷新；checksum/秒传/跳过块等本地瞬间完成的字节不计入网络速度

### 稳健性
- **编码双保险**：响应体按字节读取 + 显式 UTF-8 解码（规避百度返回非标准 `charset=utf8` 导致的崩溃）；`CodePagesEncodingProvider` 注册兜底
- **错误映射**：`ErrorMessageCatalog` 将远端错误码（31061/31079/31112/31363 等）映射为可读中文消息
- **全局异常日志兜底**：UI 线程未处理异常统一捕获，不静默崩溃

## 测试

162 个用例，分三层（均不依赖 UI 框架）：
- **密码学**：NetdiskSign / Md5Obfuscator 对齐 Go 测试向量（sign2 / locatedownloadsign）
- **协议层**：mock HttpMessageHandler 验证请求参数（UA/Cookie/URL/表单/multipart 字段）
- **引擎层**：本地测试服务器验证并发分片、断点续传、限流重试、403 换链、断流重连、暂停/恢复/取消、上传秒传/重名/会话失效

## Roadmap（规划）

- ~~v0.6 传输任务管理（暂停/取消/队列）~~ ✅ 已落地（下载侧）
- ~~v0.6 上传引擎（单文件 → 分片 → 秒传）~~ ✅ 已落地；目录递归上传、跨进程重启恢复后置
- **v0.7**：搜索、回收站、复制/移动、多选批处理
- ~~v0.8 下载线程池 + 分片加速、失败重试~~ ✅ 已落地；限速未做（后置）
- **v0.9**：设置页落地（代理/UA/并发/限速）、分享
- **v1.0**：打包发布（Windows 安装包 + 图标 + 托盘 + 自动更新）

## 许可

本项目基于 [MIT 许可证](LICENSE) 开源（Copyright © 2026 BaiduPcs contributors）。
协议实现参考自 MIT 许可的开源项目 **BaiduPCS-Go**（qjfoidnh/BaiduPCS-Go），向原项目作者致谢。

## 致谢

- **BaiduPCS-Go**：协议细节的参考实现（MIT）
- **DeepSeek**：开发过程中的 AI 辅助（架构设计 / 协议分析 / 排障）
- **ZCode**：开发过程中的 AI 编程助手（代码实现 / 测试 / 文档）

## 免责声明

本项目仅用于学习和个人使用。**请勿用于商业用途**。使用本工具可能违反百度网盘服务条款，存在账号风控风险，请自行评估。涉及的一切知识产权归原权利人所有。

## 支持

如果这个项目对你有帮助，欢迎 **⭐ Star** 支持一下——你的 star 是持续维护的最大动力！
也欢迎加入交流群反馈问题、提出建议：

**QQ 群：801154677**
