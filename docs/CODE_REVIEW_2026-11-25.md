# BaiduPcs-CS 代码与文档评审报告

> 评审日期：2026-11-25 ｜ 基线：`main` @ `f56313a`（v0.6.0）｜ 评审人：DeepSeek Harness
>
> **进度（v0.6.1，2026-11-25）**：§二 P0 中 **P0-1 / P0-2 / P0-3 / P0-5 / P0-6 已修复**（见 [CHANGELOG](../CHANGELOG.md) `[0.6.1]`）；**P0-4（上传 50s 超时）与 §三 的 P1 全部仍未修复**，属阶段 1 / 2。§4.2 的 14 条中 11 条已在文档侧修正；剩 #6 是历史 CHANGELOG 条目（历史不改写）、#13 / #14 是代码里的过时注释与版本兜底，随代码阶段一并处理。
>
> 本文是**建议与方案**，不含任何代码改动。所有结论都附可核对的 `文件:行号` 与实测证据。
> 目标读者：维护者。面向使用者的说明仍是根目录 [README](../README.md)。

---

## 〇、评审方法与实测证据

| 项目 | 做法 | 结果 |
|------|------|------|
| 通读范围 | 3 个项目全部源码（82 个文件 / 8127 行 `.cs` + `.axaml`），8 份文档（84 KB） | 100% 覆盖，未读 `bin/`、`obj/`、`BaiduPCS-Go-main/` |
| 构建 | `dotnet build BaiduPcs.slnx -c Release` | ✅ 0 警告 0 错误 |
| 测试 | `dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj` | ✅ **164/164 通过，17 秒**（README 声称的 164 属实） |
| 静态核查 | 逐条比对文档声明与实现行为；对可疑 API 语义（`RelayCommand<T>` 参数转换、`HttpClient.Timeout` 覆盖范围、`Encoding.GetEncoding("utf8")`）做交叉验证 | 见各条「证据」 |
| 交叉复核 | 协议层、上传引擎、桌面层、测试四路独立评审 + 主评审逐条回读源码验证 | 本报告只收录**已回读确认**的结论 |

**一句话结论**：核心协议与引擎的质量明显高于外壳——`BaiduPcs.Core` 有真实的测试、真实的踩坑记录、真实的协议对齐；但 **Avalonia 外壳从未被端到端跑通过**，存在多处「点一下就废」的缺陷，且默认配置里有一个影响账号安全的开关。当前 v0.6.0 属于「引擎可用、界面半成品」。

---

## 一、总体结论（按质量分层）

| 层 | 代码量 | 评价 | 说明 |
|----|--------|------|------|
| `BaiduPcs.Core/Crypto` | 2 文件 | **A** | 对齐 Go 测试向量，有单测，无问题 |
| `BaiduPcs.Core/Download` | 9 文件 | **B+** | 分片/断点/重试设计扎实；缺 `Content-Range` 校验、`AsyncManualResetEvent` 有挂死窗口 |
| `BaiduPcs.Core/Upload` | 6 文件 | **B** | 主链路正确、注释到位；失败路径（重试/31363/覆盖）有实质缺陷，且受 50s 超时牵连 |
| `BaiduPcs.Core/Api` | 9 文件 | **B** | 协议忠实、UA/编码坑处理正确；`BaiduSession` 非线程安全、错误模型缺 HTTP 状态码 |
| `BaiduPcs.Core/Infrastructure` | 4 文件 | **C** | **TLS 校验默认关闭**；`AppPaths` 文档与实现不符 |
| `BaiduPcsAPP`（UI 层） | 30 文件 | **D** | **零测试**；对话框按钮未接线、导航参数类型错误、传输状态机三处漏洞 |
| 测试 | 12 文件 | **B−** | 核心覆盖不错；**桌面层 0 覆盖**，测试基础设施有端口竞态 |
| 文档 | 8 文件 | **B−** | 质量高、信息密度大；但与实现有 10+ 处不一致（见 §4.2） |

**风险分布**（按「用户会不会立刻踩到」排序）：

```
立即可见的功能损坏  ██████████  P0（6 条）
数据安全 / 静默损坏  ████████    P0-P1
长时间使用才暴露    ██████      P1
可维护性 / 工程化    ████        P2
```

---

## 二、P0：阻断级问题（建议 1 天内修完）

以下 6 条会造成「功能完全不可用」「用户数据被覆盖」「凭证可能被窃取」，且都是一行到十行的修复。

### P0-1　对话框的「确定 / 取消」按钮没有接线 —— 新建文件夹、重命名、删除全部不可用

**证据**

- `BaiduPcsAPP/Views/ConfirmDialog.axaml:11-12`：`<Button x:Name="CancelButton" Content="取消" />`、`<Button x:Name="OkButton" Content="确定" Classes="accent" />` —— **没有 `Click=`**
- `BaiduPcsAPP/Views/ConfirmDialog.axaml.cs:23-31`：`OnOkClick` / `OnCancelClick` 已写好，但全工程无任何引用
- `BaiduPcsAPP/Views/InputDialog.axaml:15-16`：同样缺失；`InputDialog.axaml.cs:24-32` 的处理函数未被调用
- `BaiduPcsAPP/Services/DialogService.cs:23,33,43`：`await dialog.ShowDialog<string?>(owner)` / `ShowDialog<bool>(owner)` —— 只有 `Close(value)` 才会返回，用标题栏 X 关闭返回 `null` / `false`

**影响（用户视角）**

| 操作 | 结果 |
|------|------|
| 新建文件夹 | 输入框能打开、能输入，点「确定」无反应；只能点 X 关闭 → 返回 `null` → 直接 return，**永远建不出来** |
| 重命名 | 同上，**永远改不了名** |
| 删除 | 确认框点「确定」无反应 → 只能点 X → 返回 `false` → **永远删不掉** |
| 所有错误提示（`ShowMessageAsync`，「知道了」按钮） | 只能点标题栏 X 关闭 |

README:66 承诺的「新建文件夹、重命名、删除」在 v0.6.0 实际全不可用。这是**整个文件管理半边功能的失效**，也解释了为什么 `dotnet test` 全绿也没人发现——164 个测试无一触碰 APP 层。

**建议**

1. 三个按钮补 `Click="OnOkClick"` / `Click="OnCancelClick"`，并加 `IsDefault="True"` / `IsCancel="True"`（Enter/Esc 键也能用）；
2. 删掉两个无参构造（目前只服务于设计器），或保留但加注释；
3. **补一个桌面层测试项目**（见 §5.4），哪怕只做「窗口能构造 + 命令能执行」的冒烟测试。

---

### P0-2　侧边栏把字符串参数传给 `RelayCommand<int>` —— 传输 / 设置页进不去

**证据**

- `BaiduPcsAPP/Views/MainWindow.axaml:55,57,59`：`CommandParameter="0"` / `"1"` / `"2"`（XAML 字面量 → `string`）
- `BaiduPcsAPP/ViewModels/MainViewModel.cs:70-74`：`private void NavigateTo(int index)` → 生成 `RelayCommand<int>`
- `CommandParameter` 的类型是 `object`，Avalonia **不会**把 `"0"` 转换成 `0`；`RelayCommand<T>` 对非 `T` 参数要么判定不可执行、要么在 `Execute` 时抛类型异常

**影响**：「传输」和「设置」两个导航项点击无效（按钮置灰或点击抛异常）；只有「文件」页可用。因此「传输管理页」「设置页」在产品上等于不存在——底部传输停靠栏成了唯一能看到任务的地方。

**建议**（任选其一）

- 最小改动：`CommandParameter="{x:Int32 0}"`；
- 更清晰：三个无参命令 `ShowFilesCommand` / `ShowTransfersCommand` / `ShowSettingsCommand`，删掉 `int` 参数；
- 最干净：侧边栏换成 `ListBox`/`TabControl`，`SelectedIndex` 双向绑定到 `SelectedNavIndex`，彻底不传参。

---

### P0-3　TLS 证书校验默认关闭，且带着 BDUSS 一起发请求

**证据**

- `BaiduPcs.Core/Infrastructure/BaiduHttpClient.cs:24`：`public bool BypassSslValidation { get; set; } = true;`（**默认 true**）
- 同文件 `:64-67`：`handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;`
- `BaiduPcsAPP/Services/AppServices.cs:16-20`：只设置 `MaxConnectionsPerServer` 与 `Timeout`，**没有覆盖该开关** → 发布版全程关闭证书校验
- 每个请求都带完整 Cookie：`BaiduPcsApiClient.cs:167` → `BaiduSession.BuildCookieHeader()`（含 `BDUSS` / `STOKEN` / `PTOKEN`），且 `AllowAutoRedirect = true`（`BaiduHttpClient.cs:51`）

**影响**：任何能截获链路的一方（公共 Wi-Fi、恶意代理、被污染的 DNS）都可以冒充 `pan.baidu.com`，直接拿到等价于**账号密码**的 BDUSS（改密码也不失效），并可篡改上传内容。XML 注释称「与 Go 端 InsecureSkipVerify 行为一致」是误导——Go 的 `InsecureSkipVerify` 默认是 `false`。

**建议**

1. 默认值改为 `false`；
2. 若确实需要（企业代理自签证书），放进设置页，勾选时弹风险确认，并在日志里记录「本次会话证书校验已关闭」；
3. 设置页未落地前（`SettingsViewModel` 只有 23 行，见 §4.3），直接删掉这个开关更安全。

---

### P0-4　上传分片走 50 秒「整请求」超时 —— 大文件在慢速上行上必然失败

**证据**

- `BaiduHttpClient.cs:18,41-44`：`Timeout = 50s`，作用于**整个请求**（含请求体发送），且 `SendAsync` 用的是默认的 `ResponseContentRead`
- `BaiduPcsAPP/Services/AppServices.cs:19`：`Timeout = TimeSpan.FromSeconds(50)`
- 分片上传走同一个客户端：`BaiduPcsApiClient.Upload.cs`（superfile2）→ `BaiduPcsApiClient.cs:176`
- 分片档位：`BaiduPcsApiClient.Upload.cs:13-19` + `Uploader.cs:65-70`：`<8GB→4MB / 8~32GB→16MB / ≥32GB→64MB`

**影响**：一个 64 MB 分片必须在 50 秒内发完 → 需要持续 **≥1.31 MB/s** 的上行；16 MB 分片需要 **≥0.33 MB/s**。非会员/家用宽带上行普遍低于此值，于是**每一次尝试都超时 → 退避重试 8 次（约 27 秒等待）→ 任务失败**。这不是「慢」，是「永远传不完」。下载侧作者已经踩过这个坑并改用 `Timeout.InfiniteTimeSpan`（`Downloader.cs:528-542`、`DownloadService.cs:22-28`），上传侧漏了。

**建议**

1. 上传用**独立 `HttpClient`**：`Timeout = InfiniteTimeSpan`，由 `CancellationToken` 负责取消；
2. 加「停滞看门狗」而不是「总时长」：每次请求体写入前进就重置一个 30s 计时器（链接活跃就一直允许）；
3. 补一条测试：假 handler 让请求体延迟 2 秒、客户端超时设 1 秒，断言不会误杀（目前所有引擎测试用的是 ≤9 MB 文件 + 4 MB 分片，永远碰不到这个分支）。

---

### P0-5　删除「排队中」的任务不会取消它 —— 行消失了，文件照旧下载

**证据**

- `BaiduPcsAPP/Services/TransferManager.cs:144-152`：`Delete` 只在 `State is Running or Paused` 时 `Cts?.Cancel()`；排队中只 `RemoveTask`
- `BaiduPcsAPP/Services/TransferManager.cs:159-162`：`RunAsync` 拿到槽位后只判断 `State == Canceled` 才跳过

**影响**：对一个排队中的下载点「删除」→ 界面行消失 → 等到有槽位时**它仍然开始下载**：写磁盘、占并发槽、继续给已移除的 VM 投递进度、`_activePaths` 里还留着键（再次下载同一文件会提示「已在队列中」）。用户唯一的止损手段是杀进程。

**建议**：`Delete` 无条件先取消（`Queued` → 直接置 `Canceled`；其余 → `Cts.Cancel()`），再移除行；`RunAsync` 拿到槽位后除了看状态，还应确认任务仍在列表里。

---

### P0-6　覆盖写得过于随意 —— 远端与本地都可能静默丢文件

**证据（远端）**

- `BaiduPcs.Core/Upload/Uploader.cs:406-418`：`ExistsAsync` 捕获 `BaiduApiException` 后一律 `return false`（当作「不存在」）
- `Uploader.cs:132,157`：`rtype: 3`，而 `BaiduPcsApiClient.Upload.cs:99` 注明 **3 = overwrite**
- 结论：一次 meta 探测因鉴权过期/5xx 失败 → 判定「不存在」→ 用覆盖语义上传 → **远端同名文件被覆盖**（用户无感）

**证据（本地）**

- `TransferManager.cs:255-262`：`if (File.Exists(finalPath)) File.Delete(finalPath); File.Move(partPath, finalPath);`
- 上传遇到重名会自增成 `报告2.txt`（README:83），**下载却直接删掉已有文件**

**建议**

1. 探测失败 → 抛可重试错误（或重试探测），不要假设「不存在」；
2. 用 `rtype: 2`（skip）+ 处理服务端「已存在」（`-8` / `31061`）→ 自增序号，把覆盖变成显式选择；
3. 下载完成前检查目标是否存在：询问用户，或像上传一样自动改名 `报告2.docx`；
4. `File.Move(src, dst, overwrite: true)` 代替「先删后移」，避免中途失败时两边都没了。

---

## 三、P1：高优先级（建议 2–3 天）

### 3.1 传输状态机

| # | 问题 | 证据 | 影响 |
|---|------|------|------|
| P1-1 | **暂停仍占用并发槽** | `TransferManager.cs:92-100` 暂停只是 `engine.PauseAsync()`（`Downloader.cs:119-124` 重置门控），`RunAsync` 仍在 `await`，`_slot.Release()`（`:218`）不执行 | 默认 2 个槽（`App.axaml.cs:113`）；把两个任务都暂停 → **整个队列永久卡死**，且 `_activePaths` 键泄漏（同文件再也入不了队） |
| P1-2 | **上传「继续」按钮第一次之后永久失效** | `TransferManager.cs:114-127` 对失败任务执行 `_activePaths.Add(task.RemotePath)`，但上传任务的 `RemotePath` 是 `string.Empty`（`EnqueueUpload` 从不设置），而 `finally`（`:219-229`）只从 `_activeUploads` 移除 | 第一次重试插入 `""` 且永不移除 → 之后每次重试都在 `:120-122` 静默 return。README:90「失败任务可继续」对上传不成立 |
| P1-3 | **取消会删掉断点** | `TransferManager.cs:190-203` → `CleanPartFiles`（`:294-313`）删除 `.part` 与 `.part.json` | 与引擎契约（`Downloader.cs:83`「失败/取消时保留」）、README:74/286、CHANGELOG:44 **直接矛盾**；误点取消 = 白下几小时 |
| P1-4 | **VM 状态在非 UI 线程被改写** | `TransferManager.cs:263,291`（`task.DetailText = …`）、`:245,279`（`task.Engine = …`）都在 `ConfigureAwait(false)` 之后执行 | 类注释 `:12` 声称「统一经 Dispatcher 封送」被自己违反；绑定更新跨线程可能被丢弃或抛异常 |
| P1-5 | `_slot.Release()` 在 `WaitAsync` 失败时也会执行 | `TransferManager.cs:158,218` | 目前 `WaitAsync()` 无 ct 不会失败，但一旦传入 ct 就会并发度虚高 |
| P1-6 | `CancellationTokenSource` 存在「取消 vs 释放」竞态 | `:172-173` 创建、`:216` 释放、`:139/:148` 由 UI 线程 `Cancel()` | `ObjectDisposedException` 落在 UI 线程（见 §3.3 第 2 条，会被日志吞掉但仍终止进程） |

**建议**：把「`SemaphoreSlim` + 两个 `HashSet`」换成显式调度器（见 §5.1）。这是**一处改动同时消灭 P0-5、P1-1、P1-2** 的最高性价比动作。

### 3.2 引擎正确性

| # | 问题 | 证据 | 影响 |
|---|------|------|------|
| P1-7 | **`BaiduSession` 不是线程安全的** | `BaiduSession.cs:9` 是 `List<>`；`SetCookie`（`:40-57`）、`AbsorbCookiesFromResponse`（`:72-91`）写，`BuildCookieHeader`/`GetCookie`（`:93-97`）读 | 4 个上传分片并发 + 2 个传输 + UI 浏览同时发生：`AbsorbCookiesFromResponse` 在 `BaiduPcsApiClient.cs:179` 对**每个响应**都执行 → 并发 `List.Add` 与枚举重叠 → `InvalidOperationException: Collection was modified`。更糟：`BuildCookieHeader()` 在 `BaiduPcsApiClient.cs:167` 位于 `try` **之外**，该异常绕过全部错误映射，被上传层当网络错误重试 8 次 |
| P1-8 | **下载不校验 `Content-Range`** | `ChunkWorker.cs:166-186` 只用状态码 206 判定成功，然后按**本地计算的 `begin`** 偏移写盘 | 设计方案 `DOWNLOAD_ENGINE_DESIGN.md:198` 明确要求「校验 Content-Range total == TotalSize」，实现未做。若 CDN 返回与请求不一致的区间，数据会被写到错误偏移 → **文件静默损坏**（长度检查也会通过，因为 `RandomAccess` 会补零） |
| P1-9 | **上传进度重复累计** | `Uploader.cs:264-265` 每次尝试都 `ReportProgress(read, countSpeed: true)`；`:445-458` 用 `Interlocked.Add` 累加后 `Math.Min` 截断 | 分片重试 N 次就多算 N 倍字节；`31363` 整体重传再翻倍 → 进度条**提前打满 100% 并冻结**，正是 DEVELOPMENT_LOG:195-198 声称已修复的症状 |
| P1-10 | **`create` 阶段的 31363 不会触发自动重传** | `Uploader.cs:153-166`：`precreate` 在 `try` 之外；`create` 在内层 `try`，但 `catch` 只匹配 `UploadException`；`BaiduApiException`(31363) 不匹配 | 文档承诺「会话过期自动重传」只在**分片**阶段生效；合并阶段会话过期 → 任务直接失败（此时 100% 分片已上传，重试本应最廉价） |
| P1-11 | **`precreate`/`create` 无重试、无错误分类** | 同上 | 一次瞬时 5xx 就让整个上传失败；`31112`（配额满）也不会被标记为致命 |
| P1-12 | **`AsyncManualResetEvent.Reset()` 会遗弃等待者** | `AsyncManualResetEvent.cs:24-28`：`Reset() => _tcs = New();` 直接换对象 | 等待旧 TCS 的 worker 永远不会被 `Set()` 唤醒。`Pause()` 无幂等保护，连续两次暂停（或暂停与 worker 竞争）可让任务**永久挂起**在「进行中 / 0 B/s」 |
| P1-13 | **Cookie 删除语义被当成赋值** | `BaiduSession.cs:72-91` 只取 `name=value`，忽略 `Expires`/`Max-Age` | 服务端用 `BDUSS=; Expires=1970` 注销时，内存里存成空/`deleted` 值，后续所有请求带坏凭证，`locatedownload` 签名失效 → 一直到重启才恢复（落盘的账号文件是好的，所以现象像「随机报错」） |
| P1-14 | **上传流被 50s 超时 + 重定向组合击穿** | `BaiduHttpClient.cs:51` `AllowAutoRedirect = true`；multipart 不可重放 | 307/308 → 请求体已耗尽，按原 `Content-Length` 发空体 → 挂到 50s 超时 → 重试并重复累计进度。DEVELOPMENT_LOG:194 已记录为已知风险但未缓解 |
| P1-15 | **损坏的断点文件无法自愈，只会永久失败** | `DownloadCheckpoint.cs:31-47` 只校验 `TotalSize > 0`；`Downloader.cs:187-217` 无条件信任 `BlockSize`/`GenBegin`/`Ranges`；`Downloader.cs:97-116` 换直链时**复用同一个断点**，`:244-249` 的长度校验失败后同样换链再失败 | `.part.json` 里 `Ranges` 越界、`BlockSize=0`、`GenBegin>TotalSize`、或 `.part` 长度与断点不符时 → 每次重试都失败，**只能手工删文件才能恢复**。现实中磁盘写满、强杀进程都可能造成这种状态 |

### 3.3 桌面层其它必修项

1. **「全局异常兜底」名不副实**：`App.axaml.cs:57-70` 只写日志，从不设 `e.Handled = true`（`:68-69`），Avalonia 仍会终止进程；钩子在 `OnFrameworkInitializationCompleted`（`:21`）才挂，**启动阶段**的异常（XAML 解析、`AppServices` 构造、`AccountStore.Load`）连日志都进不了。README:98 / CHANGELOG:67 的「不静默崩溃」不成立。另：`WriteLog`（`:73-88`）是无锁 `File.AppendAllText`，多线程同时写会抛 `IOException` 并被 `catch` 吞掉 → **最关键的日志行可能丢失**。
2. **命令只捕获 `BaiduApiException`**：`FileBrowserViewModel.cs:165-168,192-195,218-221`；而 `LoadAsync` 捕获所有（`:125-129`）。HTTP 50s 超时、磁盘满、权限、JSON 解析异常 → 逃逸出 `IAsyncRelayCommand`，用户**什么提示都没有**（按钮点了没反应）。
3. **面包屑永不更新**：`FileBrowserViewModel.cs:47-50` 的 `Breadcrumbs` 无变更通知，`CurrentPath` 在 `:122` 变化时无人 `OnPropertyChanged`；`FileBrowserView.axaml:13` 绑定它 → 永远显示初始的 `/`。
4. **工具栏按钮永远可用**：`FileBrowserView.axaml:33-36` 未绑 `IsEnabled`；`HasSelection`（`FileBrowserViewModel.cs:52`）无人使用且无 `OnSelectedItemChanged` → 没选中文件时点「下载/删除/重命名单」静默无反应。
5. **目录加载无取消 + 竞态**：`LoadAsync`（`:108-134`）不传 `CancellationToken`（Core 支持），`CurrentPath` 在完成时写入 → 快速连点导航时，**慢的旧响应会覆盖新页面**，把用户送回上一个目录。

---

## 四、P2：可维护性与工程化

### 4.1 死代码（建议全部删除，约 300 行）

| 位置 | 说明 |
|------|------|
| `BaiduPcs.Core/Download/DownloadService.cs`（115 行） | **全工程无引用**（v0.5 遗留）。本身还带 bug：`EnsureSuccessStatusCode` 不做错误分类；`FileMode.Append` 配合 Range 但**不校验服务端是否返回 206**，服务端忽略 Range 时会把整文件追加到旧 `.part` → 文件损坏；`totalBytes = ContentLength + existingSize` 在 200 响应下算错 → 进度超 100% |
| `BaiduPcsApiClient.Download.cs:36-46` `SendDirectAsync` | 无调用方；且注释称「用于直链文件流」，实际用默认 `ResponseContentRead` **把整个文件读进内存** + 50s 超时——下一个调用者会踩坑 |
| `BaiduPcsApiClient.Auth.cs:66` `GetBdsTokenAsync` + `BaiduSession.BdsToken` | 从未调用/从未赋值（全工程不发 `bdstoken`） |
| `BaiduPcsApiClient.cs:104` `GeneratePcsUrl2` | 无调用方 |
| `Downloader.cs:33` `AllowSingleStreamFallback` | 公开选项，**从未被读取**；单线程降级是无条件的（`:161-176`） |
| `Crypto/Md5Obfuscator.cs` | 只在测试里被调用；生产代码从不校验下载文件的 MD5（见 §5.2）。另：`IsHex`（`:48-49`）只认小写 `a-f`，输入是大写十六进制时会走「去混淆」分支从而返回错误结果 |
| `PanHomeClient` 的 `Sign1/Sign3/Timestamp` | 解析出来但无人使用（`:72-78`） |
| 常量 | `ApiConstants.YunBaiduCom/DotBaiduCom/ShareBaiduCom/BaiduCom`、`ErrorMessageCatalog` 4 个 `Str*`、`BaiduPcsApiClient.Upload.cs` 的 `SliceMd5Size`（另在 `UploadChecksum.cs:57` 硬编码 256KB）、`DownloadLinkInfo.FsId` |
| 其它 | `BaiduPcsApiClient.cs:163,212` 的 `headers` 参数无人使用；`UploadChecksum.ComputeAsync` 的 `progress` 参数永远传 `null`（`Uploader.cs:109-110`）；`BaiduPcsAPP.csproj:14` `<Folder Include="Models\" />`（目录不存在）；`FileBrowserViewModel.cs:17,63` 注入但从未使用的 `_transfers` |

**为什么重要**：`DownloadService` 与 `SendDirectAsync` 不只是「多余」，它们**看起来可用、实际会损坏数据**——这比缺失更危险。

### 4.2 文档与实现不一致（逐条列出，建议一次性对齐）

| # | 文档位置 | 文档说法 | 实际行为 |
|---|----------|----------|----------|
| 1 | README:74、:286（FAQ Q5）、:155 | 取消后保留 `.part` / `.part.json`，可续传 | 取消时 `TransferManager.cs:190-203` **删除**这两个文件 |
| 2 | `DEVELOPMENT_LOG.md:53-62`（3.2） | 官方通道与签名直链「并列，不降级」，由用户选 | 实现是 locatedownload 为主、失败**自动降级**官方 `method=download`（`BaiduPcsApiClient.Download.cs:53-71`），README:73 与实现一致、与开发日志矛盾 |
| 3 | `DEVELOPMENT_LOG.md:64-67`（3.3） | 断点续传 = `.part` 已下载字节 + `Range: bytes=existing-` | 已是 `.part.json` 分片级断点（3-bis 才是现状），3.3 未标注作废 |
| 4 | `DEVELOPMENT_LOG.md:236-237`（五、Roadmap） | v0.8 计划「多线程分片下载」 | 0.5.9 已发布；该 Roadmap 小节整段过期 |
| 5 | `DOWNLOAD_ENGINE_DESIGN.md:3` | 「118 个用例全绿」 | 现为 164 |
| 6 | CHANGELOG:45 | 「任务级重试 3 次」 | 实际是「单分片 8 次重试 + 多直链轮换」（`Downloader.cs:30,97-116`） |
| 7 | README:179-183（数据位置表） | macOS 为 `~/Library/Application Support/BaiduPcsApp` | `AppPaths.cs:9-10` 只调用 `SpecialFolder.ApplicationData`，**无平台分支**；在 macOS 上 .NET 返回的是 `~/.config`，并非表中路径（建议实测确认后按实际改文档） |
| 8 | README:98 | 「全局异常兜底：未处理异常写入本地日志，不静默崩溃」 | 记录了但进程仍会崩溃（§3.3-1） |
| 9 | README:90 | 「失败任务可继续（重试）」 | 上传任务第二次以后重试被静默拦截（P1-2） |
| 10 | README:62 | 「多账号本地保存…退出登录可切换账号」 | 账号确实落盘，但 `LoginViewModel.cs:46` 的 `Accounts` **没有任何界面消费**，切换只能靠重新粘贴 BDUSS |
| 11 | README:260 / 348-351 | 分享、回收站、设置页、托盘等标为「计划中」 | ✅ 与实现一致（这条是对的，值得保留这种严谨） |
| 12 | `DEVELOPMENT_LOG.md:195` 与 `:198` | 连续两个「8.」 | 编号重复，重排即可 |
| 13 | `TransfersViewModel.cs:7` | 「当前为 UI 骨架，P4/P5 接入真实引擎」 | 已接入真实引擎 |
| 14 | `SettingsViewModel.cs:21` | 版本兜底 `"v0.5.9"` | 项目已是 0.6.0 |

### 4.3 工程化缺口

| 项 | 现状 | 建议 |
|----|------|------|
| CI 测试平台 | `.github/workflows/build.yml:41-43` **只在 Windows 跑测试**，Linux 仅编译 | 双平台都跑测试。作者踩过的 `SpeedMeter` 平台 tick 坑（DEVELOPMENT_LOG:186）正是 Linux-only 缺陷——现有 CI 永远发现不了 |
| 发布流水线 | 无 tag 触发的 release workflow，6 个 RID 靠手工 `dotnet publish` | 加 `on: push: tags: v*` 矩阵发布，自动产出 6 个 zip + checksums |
| 依赖缓存 | `setup-dotnet` 未开 `cache: true` | 一行开启，CI 省 30-60 秒 |
| 覆盖率 | `coverlet.collector` 已引用但 CI 不收集 | `--collect:"XPlat Code Coverage"` + 门槛（当前 APP 层为 0%） |
| 静态检查 | 无 `dotnet format --verify-no-changes`、无 analyzer 配置、无 `TreatWarningsAsErrors` | 加一个 `Directory.Build.props`：`EnableNETAnalyzers` + `AnalysisLevel=latest` + 建议先 `WarningsAsErrors` 白名单化 |
| 桌面层测试 | `BaiduPcs.Core.Tests.csproj:21-23` 只引用 Core → **APP 层 0 覆盖** | 新建 `BaiduPcsAPP.Tests`（见 §5.4） |
| 测试基础设施 | `TestHttpServer.cs:203-210` 先探端口再 bind（TOCTOU，并行测试下偶发冲突）；`DownloadEngineTests.cs:117-145` 用 `Thread.Sleep` 测速度并容忍 ±60% | 用 `HttpListener` 的 `Prefixes` 绑 `http://127.0.0.1:0/`（或改用 `Kestrel` 测试宿主）；速度测试改用注入时钟 |
| 日志 | 无轮转、无级别、无并发保护（`App.axaml.cs:73-88`） | 引入 `Microsoft.Extensions.Logging` + 单写线程，按大小轮转 |
| 工作区 | `publish/` 1.09 GB、`BaiduPcsAPP/bin` 1.42 GB（已被 `.gitignore` 覆盖） | 定期清理（`.gitignore` 与 workflow 均为合法 UTF-8，无需处理；早前在终端里显示乱码只是本地控制台编码问题） |

### 4.4 测试专项发现（164 个用例是真实的，但它给出的安全感不真实）

数量与通过率**完全属实**（110 `[Fact]` + 54 `[InlineData]` = 164，17 秒全绿）。问题在于**覆盖位置**与**断言强度**：

| # | 问题 | 证据 | 后果 |
|---|------|------|------|
| T1 | **断言阈值宽到抓不住原始缺陷** | `UploaderTests.cs:275,493`：`Assert.True(p.SpeedBytesPerSecond < 1024L*1024*1024)` | 历史缺陷是 ≈285 MB/s，**远低于 1 GiB/s 阈值** → 该回归测试形同虚设。应断言「秒传/续传跳过路径速度 ≈ 0」 |
| T2 | **`Progress<T>(list.Add)` 在异步线程池上执行** | `UploaderTests.cs:261,323,399,484`；`DownloaderTests.cs:361-377` | 回调不保证在 `await UploadAsync()` 返回前跑完（末条可能读到旧值），且 `List<T>.Add` 被并发调用。是**潜伏型抖动**，机器越忙越容易红 |
| T3 | **「续传」测试区分不了「续传」与「从零重下」** | `DownloaderTests.cs:143-181` 只断言「断点存在」+「最终内容正确」 | 即使引擎完全忽略断点、从头再下一遍，测试照样通过 |
| T4 | **全suite 没有任何超时** | 无 `xunit.runner.json`、无 `[Fact(Timeout=…)]`、无 `.WaitAsync(timeout)` | 历史缺陷「监控循环空转」若回归，测试会**挂死到 CI 作业超时**，而不是快速失败（且并行 collection 会连带拖垮其它用例） |
| T5 | **CI 只在 Windows 跑测试，恰好让最该防的回归失效** | `build.yml:41-43`；本机 `Stopwatch.Frequency == 10,000,000 == TimeSpan.TicksPerSecond` | `SpeedMeter` 的平台 tick 缺陷（DEVELOPMENT_LOG:186）在 Windows 上数值等价 → 回归测试**无法失败**；换 Linux 跑才会红 |
| T6 | **唯一的 UI 冒烟开关从未被使用** | `App.axaml.cs:35-49` `BP_SMOKE_TEST=1` 启动 3 秒自动退出，但没有任何 workflow 设置它 | 现成的、成本近乎为零的 UI 启动检查没跑起来 |
| T7 | **`AppPaths` 是无注入点的静态类** | `AppPaths.cs:9-17`；`AccountStore.cs:21` 默认走真实 `%APPDATA%` | 现有测试都显式传临时路径（这点做得好），但将来一旦测 `AppServices`/默认 `AccountStore`，就会写开发者真实配置目录。建议改为实例 + 可注入根目录 |
| T8 | **`tests` 只引用 Core** | `BaiduPcs.Core.Tests.csproj:21-23` | APP 层 0 覆盖——P0-1/P0-2 这类「按钮没接线」的缺陷，编译期与现有测试全都发现不了 |

**结论**：再补 50 个 Core 单测的收益，远小于 T2/T3/T4/T5/T8 这 5 项「让已有测试真正有效」的改动。优先级：T8 → T5 → T4 → T2/T3 → T1。

---

## 五、重构与架构建议（按性价比排序）

### 5.1 用显式调度器替换 `SemaphoreSlim` + 两个 `HashSet`（最高性价比）

现状：`TransferManager.cs:18-28` 用 `SemaphoreSlim(2)` + `_activePaths` / `_activeUploads` 两个集合表达队列，状态在 9 个位置被赋值，去重键与清理逻辑分散。

建议：
- `TransferJob` 不可变记录（`RemotePath`/`LocalPath`/`SaveDir`/`RemoteDir`/`IsUpload`），**去重键由它派生**，清理时必然一致；
- `Queued` / `Running` / `Paused` 显式集合；暂停 = 让出槽位并回到 `Queued`，恢复 = 重新排队；
- 单一 `Transition(job, newState)` 配合合法迁移表 + 锁，取代散落的赋值；
- 引擎回调经**一个** `IUiDispatcher` 封送（带 `CheckAccess()` 快路径）。

一次改动即可消灭 P0-5 / P1-1 / P1-2 / P1-4 / P1-5 及去重键相关的全部低危问题。

### 5.2 补上「完整性校验」这道门

- **下载**：`FileEntry.Md5` 已经拿得到（`Models/FileEntry.cs:34-36`），`Md5Obfuscator.DecryptMd5` 也写好了（目前只有测试用）。建议下载完成后流式计算 MD5 并与远端比对，不一致则标红并保留 `.part`。这是防「静默损坏」的唯一可靠手段，成本约 40 行。
- **上传**：`Uploader.cs:271` 已保存服务端返回的分片 md5，但从不比对；`create` 之后也不验证远端文件是否存在/大小是否一致。建议比对分片 md5 + `GetMetaAsync` 校验最终大小。
- **`Content-Range` 校验**（P1-8）：在 `ChunkWorker` 读到 206 后校验 `ContentRange.From == begin && ContentRange.Length == totalSize`，不符即当网络错误重连。

### 5.3 抽出 `IBaiduPcsApiClient` 接口

`BaiduPcsApiClient` 是 `sealed partial class`（约 1225 行、21 个公开方法、`PrecreateAsync` 有 11 个位置参数）。引擎只能通过真实 HTTP 栈测试——想测「31363 时是否重传」必须让假服务器吐出**完全正确**的线上 JSON。建议：

- 按域拆 `IAuthApi` / `IFileSystemApi` / `ITransferApi`，注入 `UploaderOptions.Api`；
- `PrecreateRequest` 记录取代 11 个参数；
- URL 构造器改 `internal`（`DownloadService` 是唯一外部使用者，而它是死代码）。

### 5.4 把 `BaiduPcsAPP` 纳入测试

P0-1/P0-2 这类「按钮没接线」的缺陷，编译期和 Core 测试都发现不了，只有一个最小的 UI 冒烟测试能挡住：

- `BaiduPcsAPP.Tests`（xUnit + `Avalonia.Headless`）：
  1. 每个 `Window`/`UserControl` 能 `InitializeComponent` 并绑定 VM（XAML 里任何绑定错误都会暴露）；
  2. 关键命令能执行：`ConfirmDialog` 的 OK 返回 `true`、`NavigateTo` 切页生效；
  3. `TransferManager` 用假引擎验证状态机：排队→删除→不启动、暂停→让出槽位、失败→重试可用。
- 这类测试不需要真机、不需要网络，`dotnet test` 一并跑。

### 5.5 删除界面上的死 UI 与死依赖

- 「传输」面板的「收起/展开」：`TransfersView.axaml:13` 的按钮**没有 Command**，`IsExpanded` 无人读取 → 纯装饰；
- 设置页：`SettingsViewModel`（23 行）+ `SettingsView.axaml` 三个只读文本，但导航项叫「设置」，而 Core 已支持的代理/UA/并发/证书开关全部被 `AppServices.cs:16-20` 硬编码；建议要么改名「关于」，要么落地 `appsettings.json` + `ISettingsStore`；
- 底部停靠栏高度固定 `MaxHeight="200"`（`TransfersView.axaml:16`），任务多时只能滚 200px。

---

## 六、分阶段改善方案

> 工时按「熟悉该仓库的 1 人」估算；每阶段结束都可独立发版。

### 阶段 0 —「能用」急救包（0.5–1 天，**建议立即做**）

| 任务 | 验收标准 |
|------|----------|
| P0-1 对话框按钮接线 | 手工验证：新建文件夹/重命名/删除/错误提示四类对话框的按钮都生效；Esc/Enter 可用 |
| P0-2 导航参数修复 | 三个导航项都能切页，无异常 |
| P0-3 TLS 默认值改 `false` | `BaiduHttpClient` 构造后 `BypassSslValidation == false`；指定自签证书场景时能显式打开 |
| P0-5 删除排队任务 | 排队中删除 → 不再下载、不写盘、`_activePaths` 已清理 |
| P0-6 覆盖保护 | 远端探测失败 → 任务失败并提示（不再覆盖）；下载目标已存在 → 自动改名或询问 |
| 文档校正 | §4.2 表格中第 1、2、3、4、6、8、9 条改到与实现一致 |

### 阶段 1 —「不丢数据」语义修复（1–2 天）

| 任务 | 验收标准 |
|------|----------|
| 取消保留断点（P1-3） | 取消后 `.part`/`.part.json` 仍在；再次下载同一文件进度从断点继续；「删除」才清理文件 |
| 传输调度器重构（§5.1） | 暂停两个任务后队列能继续；上传失败后可重试多次；删除任意状态任务都安全 |
| 进度与速度正确性（P1-9） | 分片重试 / 31363 重传后进度不超 100%、不提前打满；新增断言「create 返回前进度 < 100%」 |
| `AsyncManualResetEvent` 修复（P1-12） | 连续两次暂停 + 恢复后任务能继续；新增单测覆盖 |
| VM 线程与生命周期（P1-4、P1-6） | 所有 VM 状态变更经统一封送；退出/登出时不再有后台任务向已关闭窗口投递进度 |

### 阶段 2 —「传得完、传得对」（2–3 天）

| 任务 | 验收标准 |
|------|----------|
| 上传专属 `HttpClient` + 停滞看门狗（P0-4） | 用假 handler 模拟 0.2 MB/s 上行，64 MB 分片能完成；断流 30s 后重连 |
| `Content-Range` 校验（P1-8） | 假服务器返回错误区间 → 视为网络错误重连，不落盘 |
| 下载完整性校验（§5.2） | 假服务器返回错误内容 → 任务标红「校验不通过」并保留 `.part` |
| `create`/`precreate` 重试与 31363（P1-10/11） | 假服务器在 create 返回 31363 → 自动重来一轮；瞬时 5xx 重试后成功 |
| `BaiduSession` 线程安全（P1-7） | 并发 4 上传 + 浏览时无 `Collection was modified`；`BuildCookieHeader` 移入 `try` |
| Cookie 删除语义（P1-13） | 收到 `BDUSS=; Expires=1970` → 从会话移除并提示重新登录 |
| 坏断点自愈（P1-15） | 构造 `Ranges` 越界 / `BlockSize=0` / `.part` 长度不符的断点 → 引擎自动丢弃断点并从头重下，不再永久失败；顺带提供「强制重新下载」入口 |

### 阶段 3 —「可维护」（2–4 天）

`IBaiduPcsApiClient` 抽取（§5.3）→ `BaiduPcsAPP.Tests` 落地（§5.4）→ 死代码删除（§4.1）→ 文档索引更新（把本报告加入 `docs/README.md` 清单）。

### 阶段 4 —「工程化」（1–1.5 天）

1. **CI 双平台测试**（`ubuntu-latest` 也跑 `dotnet test`）——这是让 `SpeedMeter` 那条回归测试从装饰变有效的唯一办法（§4.4 T5）；
2. NuGet 缓存（`setup-dotnet` 的 `cache: true`）+ 覆盖率收集与门槛（先只对 Core 设门槛，APP 层从 0 起步）；
3. 给每个引擎测试加 `.WaitAsync(TimeSpan.FromSeconds(60))`，让「挂起型」回归快速失败（§4.4 T4）；
4. workflow 里加一步 `BP_SMOKE_TEST=1` 启动冒烟（Windows 有 GUI 会话时可行；Linux 用 `xvfb-run`）；
5. `Directory.Build.props`（`EnableNETAnalyzers` + `AnalysisLevel=latest`，逐步把告警白名单化 Errors）+ `dotnet format --verify-no-changes`；
6. tag 触发的 6-RID 发布流水线（自动产出 zip + SHA256）；
7. 日志轮转与并发写保护（§3.3-1）。

---

## 七、三条可选路线（请择一）

| 路线 | 内容 | 工时 | 产出 | 适用 |
|------|------|------|------|------|
| **A 急救** | 阶段 0 | 0.5–1 天 | 广告里承诺的功能真正可用；账号安全风险解除 | 想尽快让 v0.6.0 名副其实、可以发给别人用 |
| **A + C 稳交付**（推荐） | 阶段 0 + 阶段 4 + 文档对齐 | 1.5–2.5 天 | 上面 + CI 防回归 + 文档可信 | 准备持续维护、接受外部 Issue/PR |
| **全量 B** | 阶段 0–4 | 7–11 天 | 引擎健壮性、桌面层可测、可长期演进 | 打算继续做 v0.7（搜索/回收站/目录递归上传），或想把 Core 当库给别的前端用 |

**推荐**：`阶段 0 → 阶段 4 → 阶段 1/2`。

理由：阶段 0 修的都是「一点就废」，且几乎都是几行改动，立刻把「引擎可用、界面半成品」变成「能正常用」；阶段 4 只需半天，却能把「改坏没人知道」变成「CI 拦住」——考虑到目前 APP 层零测试，这个杠杆比最高；阶段 1/2 动的是传输语义与引擎内部，风险相对大，应当在有 CI 和 UI 测试兜底之后再做。

---

## 附录：如何自行复核本文结论

以下命令在仓库根目录（含 `BaiduPcs.slnx` 的那一层）执行，PowerShell 与 bash 均适用（第 2–5 条是 PowerShell 写法）：

```powershell
# 1. 构建与测试基线（当前：0 警告 0 错误 / 164 通过 / 17 秒）
dotnet build BaiduPcs.slnx -c Release
dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj

# 2. 确认 P0-1：对话框按钮没有任何 Click 绑定（应输出为空）
Select-String -Path BaiduPcsAPP\Views\ConfirmDialog.axaml,BaiduPcsAPP\Views\InputDialog.axaml -Pattern 'Click='

# 3. 确认 P0-3：TLS 校验默认关闭
Select-String -Path BaiduPcs.Core\Infrastructure\BaiduHttpClient.cs -Pattern 'BypassSslValidation'

# 4. 确认 P0-6：上传一律使用覆盖语义 rtype=3
Select-String -Path BaiduPcs.Core\Upload\Uploader.cs -Pattern 'rtype: 3'

# 5. 确认死代码：DownloadService / SendDirectAsync 无调用方（应只命中它们自身的定义）
Get-ChildItem -Recurse -Filter *.cs | Select-String -Pattern 'new DownloadService|\.SendDirectAsync'
```

**手工验收清单（阶段 0 完成后应全部通过）**

1. 新建文件夹 → 输入名称 → 点「确定」→ 列表里出现新目录；
2. 选中文件 → 删除 → 点「确定」→ 文件消失；
3. 点侧边栏「传输」→ 出现传输管理页；点「设置」→ 出现设置页；
4. 触发一个错误（如断网后点刷新）→ 提示框可用「知道了」关闭；
5. 排队中删除一个下载 → 观察网络/磁盘，确认没有再开始下载；
6. 目标目录已存在同名文件时下载 → 不静默覆盖；
7. 上传大文件（或用 20 MB 文件 + 把分片档位临时降到 4 MB 模拟慢速）→ 不因 50 秒超时失败。

---

*本报告只做诊断与方案，未修改任何源码。若需要我直接落地某一阶段，请指定阶段编号。*
