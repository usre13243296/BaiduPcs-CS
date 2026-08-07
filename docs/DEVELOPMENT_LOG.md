# BaiduPcs 开发日志与核心思路

> 本文件记录 v0.5 开发过程中的核心架构决策、关键踩坑与后续规划，供维护者与贡献者阅读。

---

## 一、项目起源

以 C# / Avalonia 重写开源项目 BaiduPCS-Go 的核心能力，目标：
1. 提供现代化桌面 UI（Avalonia 跨平台）。
2. 复用 Go 版已验证的协议细节，用 C# 完整实现。
3. 保持核心层（BaiduPcs.Core）与 UI 层解耦，可独立测试。

---

## 二、核心架构

```
BaiduPcs.Core（无 UI 依赖）
 ├─ Api/        HTTP 请求管道、URL 构造、错误码映射、各接口封装
 ├─ Auth/       BDUSS 会话、Cookie 收集/持久化、登录服务
 ├─ Crypto/     NetdiskSign（下载签名）、Md5Obfuscator
 ├─ Download/   下载服务（流式+断点续传+进度）
 └─ Infrastructure/ HTTP 客户端工厂、路径常量
BaiduPcsAPP（Avalonia）
 ├─ ViewModels/ 登录/文件浏览/传输/设置
 ├─ Views/      XAML
 └─ Services/   对话框/剪贴板/组合根
```

**关键原则**：
- Core 层不引用 Avalonia，所有 UI 交互（对话框、剪贴板）都经 `IDialogService` 抽象。
- 请求全部 async/await，无同步阻塞。

---

## 三、关键设计决策（含踩坑）

### 3.1 登录链路：先初始化 Cookie 再验证

**问题**：直接用裸 BDUSS 调 `pan/api/user/getinfo` 被拒（`errno=2 params error`）。

**真相**（A/B curl 实测）：
- pan 域接口（`/api/*`）**必须使用 netdisk 客户端 UA**；Chrome UA 即使参数齐全也返回 `errno=2`。
- PCS 域接口（`/rest/2.0/pcs/*`）对 Chrome UA 宽容，实测正常。

**解决**：
- 登录先访问 `pan/disk/home` 收集服务器下发 Cookie（BAIDUID 等）→ 再调 `getinfo`。
- `BaiduRequestFactory` 用 `TryAddWithoutValidation` 原样发送 UA（**坑**：`netdisk;P2SP;...` 是非标准格式，`UserAgent.TryParseAdd` 会静默解析失败导致 UA 头未发出）。
- 登录成功后把运行期吸收的 Cookie 合并回账号并持久化，避免再次登录丢失上下文。

### 3.2 下载：官方接口 vs 签名直链（并列，不降级）

实测结论：
- **官方 `method=download`**：服务端按账号会员等级签发直链并 302 重定向，**会员加速生效**；但对部分「转存文件」（owner_id 非本号）返回 403，且存在限流。
- **locatedownload 签名直链**：对任意文件可稳定获取；但 CDN 可能限速（实测 2% 断流）。

**决定**：做成两个并列独立功能——
- 「下载」→ 官方接口（会员加速）；
- 「复制链接」→ 签名直链（可分享/手动下载兜底）。
不做自动降级黑盒，由用户自主选择。

### 3.3 断点续传

- 本地 `.part` 临时文件记录已下载字节，配合 HTTP `Range: bytes=existing-` 续传。
- 完成后重命名 `.part` → 正式文件。

### 3.4 大文件下载被 50s 超时截断

**坑**：`HttpClient.Timeout` 覆盖整个请求（含流读取），API 客户端 50s 总超时会把大文件下载强行取消（`TaskCanceledException`）。

**解决**：下载使用独立的无限超时 `HttpClient`（`Timeout = InfiniteTimeSpan`），由 `CancellationToken` 负责取消。

### 3.5 百度返回非标准 `charset=utf8` 导致崩溃

**坑**：PCS 响应头是 `charset=utf8`（非标准），`HttpContent.ReadAsStringAsync` 内部 `Encoding.GetEncoding("utf8")` 抛 `ArgumentException` → UI 线程未处理异常 → 应用无响应。

**双重解决**：
1. `Program.cs` 注册 `CodePagesEncodingProvider`（治本）。
2. `SendRawAsync` 改 `ReadAsByteArrayAsync` + 显式 `Encoding.UTF8.GetString`（双保险）。

### 3.6 Avalonia 12 剪贴板 API 迁移

**坑**：Avalonia 12 废弃了 `IClipboard.SetTextAsync` / `DataObject`，改用新体系。

**解决**（反射探测确认）：
```csharp
var dataTransfer = new Avalonia.Input.DataTransfer();
dataTransfer.Add(Avalonia.Input.DataTransferItem.CreateText(text));
await clipboard.SetDataAsync(dataTransfer);
```

### 3.7 密码学算法与 Go 向量对齐

`NetdiskSign`（对应 Go `baidupcs/netdisksign`）：
- `DevUID`：大写 MD5(bduss) + "|0"。
- `LocateDownloadSign`：rand = SHA1(SHA1(bduss)hex + uid + 硬编码盐 + time + devuid) 小写 hex。
- `ShareSURLInfoSign`：MD5(shareId + "_sharesurlinfo!@#") hex。
- `Sign2`：魔改 RC4（KSA 用 key 填充 256 表，PRGA 与 Go 完全一致）。
- `Md5Obfuscator.DecryptMd5`：对应 Go `DecryptMD5` 反混淆。
- 以上算法均有单元测试对齐 Go 测试向量（`sign2_test.go` / `locatedownloadsign_test.go`）。

---

## 四、V0.5 功能清单

- [x] BDUSS 登录 + Cookie 初始化/持久化
- [x] 文件浏览（列表/面包屑/新建/重命名/删除/配额）
- [x] 官方通道下载（会员加速 + 断点续传 + 进度/速度）
- [x] 复制下载链接（签名直链）
- [x] 淡灰蓝浅色系 UI
- [x] 多账号 + 重启恢复
- [x] 全局异常日志兜底
- [x] 单元测试 59 个（含密码学 Go 向量）

---

## 五、Roadmap

### v0.6 — 上传与传输管理
- 上传引擎：单文件 → 分片（precreate + superfile2）→ 秒传（md5 预检）→ 断点续传
- 传输任务管理：队列、并发数、暂停/取消、任务持久化（重启恢复）

### v0.7 — 文件管理增强
- 搜索、回收站（列表/还原/清空）、复制/移动、多选批处理

### v0.8 — 下载加速
- 多线程分片下载（Range 并发）、失败自动重试（指数退避）、限速

### v0.9 — 设置与分享
- 设置页落地（代理 HTTP/SOCKS5、UA、并发、忽略证书）
- 分享创建/取消/列表

### v1.0 — 发布
- Windows 安装包（NSIS/Inno）、应用图标、托盘、自动更新
- 深色/浅色主题、中英双语

---

## 六、风险与合规提醒

- 本工具依赖百度网盘私有接口，可能随时变更或触发风控。
- 涉及「转存文件」的官方下载可能 403，属服务端策略，请配合「复制链接」功能兜底。
- 仅供学习与个人使用，请勿商用；请谨慎使用，注意账号安全。