# 下载引擎改造方案（v0.8 前瞻：加速 · 稳定 · 任务管理）

> **状态**：✅ 全部落地（2026-08-08）。M1 引擎层 → M2 协议层 → M3 任务管理+UI → M4 全量测试（118 个用例全绿）→ M5 文档归档。
> 设计决策与踩坑已按既有格式归档进 `DEVELOPMENT_LOG.md`（三-bis 节），`README.md` 功能清单已更新。
> 遗留：下载限速（方案 5.9）后置；UI 端到端下载需真实账号验证。

---

## 一、背景与目标

用户反馈 v0.5 下载引擎的四个问题：

1. **下载速度慢** —— 单线程串流，无分片并发。
2. **有概率报错** —— 网络抖动一次即整任务失败；403（转存文件）无兜底；断线可永久卡死。
3. **无法支持会员加速** —— 客户端没有任何会员相关的加速逻辑。
4. **不支持暂停和删除任务** —— 传输面板只有展示，无任何操作能力。

对照开源参考 **BaiduPCS-Go**（`BaiduPCS-Go-main/`），本次改造的五个目标：

| # | 目标 | 对应 Go 依据 |
|---|------|-------------|
| 1 | 多线程分片并发下载，显著提速 | `requester/downloader`（默认 5 并发） |
| 2 | 失败重试 + 错误分类 + 卡死兜底，消除偶发报错 | `monitor.go` / `resetcontroler.go` / `worker.go` 状态分类 |
| 3 | 并发加速落地：统一并发 8（会员直链 8 并发 ≈ 20MB/s，与官方客户端一致；非会员服务端总量限速 ~100KB/s，并发无增益） | 用户实测（2026-08-07）+ Go README「svip 建议 max_parallel 10~20」 |
| 4 | 任务管理：暂停 / 恢复 / 取消 / 删除 + 错误详情可见 | `Downloader.Pause/Resume/Cancel`（引擎已完备） |
| 5 | 通道切换：locatedownload 签名直链为主，method=download 降级备选 | `baidupcs/download.go`（locate 为默认模式） |

---

## 二、现状梳理：v0.5 下载链路 12 个问题点

> 锚点均为当前仓库文件:行号（已核实）。

| # | 问题 | 证据（C# 侧） | 根因推断 |
|---|------|--------------|---------|
| 1 | **速度慢：单连接单线程** | `BaiduPcs.Core/Download/DownloadService.cs:86-103` 一条读写循环串行执行 | 无 Range 分片、无并发 worker，速度受单 TCP 流上限与 CDN 单流限速约束 |
| 2 | **无任何失败重试** | `DownloadService.cs:78-113` 全流程仅一次请求，无 try/catch 重试、无退避 | 网络抖动 / 403 一次即整任务失败 |
| 3 | **可能永久卡死** | `DownloadService.cs:27` `Timeout = InfiniteTimeSpan`；`BaiduPcsAPP/ViewModels/FileBrowserViewModel.cs:310` 未传 CTS，UI 无取消入口 | 服务器断流（TCP 半开）后 `ReadAsync` 永不返回；`HttpClient.Timeout` 只覆盖到响应头，对流读取无效 |
| 4 | **断点续传静默损坏** | `DownloadService.cs:63-66` 请求 Range 但 `:78-79` 不校验是否 206，`:83` 直接 `FileMode.Append` | 服务端忽略 Range 返回 200 全量时，内容追加到 `.part` 末尾造成重复，随后静默重命名"成功" |
| 5 | **chunked 响应进度恒 0** | `DownloadService.cs:81` `(ContentLength ?? 0)`；`DownloadProgress.Percent`（`DownloadService.cs:12`）TotalBytes=0 时恒 0 | 重定向后最终响应为 chunked（百度直链常见），无 Content-Length → 进度条 0%，用户感知"卡住" |
| 6 | **错误详情不可见** | `Views/TransfersView.axaml:16-34` 模板未绑定 DetailText；`FileBrowserViewModel.cs:316-317` 写了却无处显示 | 用户只见"失败"二字；且 `EnsureSuccessStatusCode`（`DownloadService.cs:79`）抛裸异常，绕过错误码 → 中文消息映射管道（`BaiduPcsApiClient.cs:181-195`） |
| 7 | **UA 与官方客户端不一致** | `DownloadService.cs:69` 用 Chrome UA（`ApiConstants.DefaultUserAgent`） | Go 原版直链与下载请求均用 netdisk 安卓 UA（panUA）；C# 用 Chrome UA 身份不统一（Go changelog 记载「更换默认panUA解决svip限速」场景），应统一 |
| 8 | **下载不走基础设施配置** | `DownloadService.cs:25-28` `new HttpClient()` 裸客户端，未走 `BaiduHttpClient.BuildHandler`（`BaiduPcs.Core/Infrastructure/BaiduHttpClient.cs:47-70`） | 代理（ProxyAddress）、`MaxConnectionsPerServer`、SSL 绕过对下载链路全部无效 |
| 9 | **同文件并发互踩** | `DownloadService.cs:83` `FileShare.None`；`FileBrowserViewModel.cs:288` 无任务去重 | 同一文件点两次下载 → 后写者 IOException 或竞态，是"有概率报错"来源之一 |
| 10 | **速度统计虚高/不准** | `FileBrowserViewModel.cs:296-307`：`lastBytes` 初值 0、`sw` 计时起点错乱、delta 跨窗口累积 | 小文件（<1s）SpeedText 恒空；多回调累积在同一 delta 上导致数值虚高 |
| 11 | **进度回调线程上下文** | `FileBrowserViewModel.cs:298` `new Progress<...>()` 在 `Task.Run` 内创建，捕获不到 UI SynchronizationContext；`TransfersViewModel.cs:37` 却刻意 `Dispatcher.UIThread.Post` | 任务属性从线程池线程变更，跨线程绑定通知可靠性依赖框架，且 `RefreshCounters`（`TransfersViewModel.cs:42-48`）在回调线程执行 |
| 12 | **下载引擎零测试** | `BaiduPcs.Core.Tests/` 59 个用例无一条覆盖 DownloadService（仅 ApiClientTests 覆盖到 getinfo/list 等） | 断点续传、Range 头、200/206 分支、取消/异常路径全部裸奔 |

**总结**：v0.5 的下载是「单线程官方通道 + .part 总字节续传 + 无重试/无取消/无并发」的最小实现。

---

## 三、Go 原版参照（BaiduPCS-Go-main）

### 3.1 三层架构（C# 侧照搬此分层）

```
命令层   internal/pcscommand/download.go + internal/pcsfunctions/pcsdownload/  任务队列、下载模式、校验
协议层   baidupcs/download.go + baidupcs/netdisksign/                          locatedownload 签名直链
引擎层   requester/downloader/ + requester/transfer/ + requester/rio/speeds/  通用多线程下载器（与百度协议解耦）
```

关键：`requester/downloader` 是**与百度协议完全解耦的通用下载器**，只接收 `durl + cookie + 大小`。
C# 侧照搬：引擎层不依赖 `BaiduPcsApiClient`，直链获取（协议层）与下载执行（引擎层）分离。

### 3.2 关键文件速查（已核实锚点）

| Go 文件 | 锚点 | 内容 |
|---------|------|------|
| `requester/downloader/config.go` | :10-15 | `ParallelSize=5` 默认并发、`MinParallelSize=256KB` 最小片 |
| `requester/downloader/downloader.go` | :127-144 | `SelectParallel` 并发决策（不支持 Range→1；有断点→按剩余 range 数） |
| `requester/downloader/downloader.go` | :147-180 | `SelectBlockSizeAndInitRangeGen` 分片档位 |
| `requester/downloader/downloader.go` | :401-418 | 初始分配：循环 `GenRange()` 装满 parallel 个 worker |
| `requester/downloader/downloader.go` | :498-522 | `Pause()/Resume()/Cancel()`（引擎完备，CLI 未暴露） |
| `requester/downloader/worker.go` | :258-265 | Range 请求头构造 |
| `requester/downloader/worker.go` | :290-313 | HTTP 状态码分类（403/404/416 致命；429/509 限流重连；406/其他 网络错误重连） |
| `requester/downloader/worker.go` | :347-456 | 读取循环：写锁 + `WriteAt` 绝对偏移写最终文件 |
| `requester/downloader/monitor.go` | :188-209 | 失败/网络错误 worker 自动 `Reset()` 重连 |
| `requester/downloader/monitor.go` | :235-266 | 空闲 worker 补新 range（99ms tick） |
| `requester/downloader/monitor.go` | :269-307 | 速度 < 峰值/6 → 剩余最大片折半拆分给空闲 worker |
| `requester/downloader/monitor.go` | :310-342 | 0 速度非暂停 worker 强制重置连接（CDN 断流恢复） |
| `requester/downloader/monitor.go` | :376-421 | 990ms 监控循环：重置 + 速度监控 + 断点持久化 |
| `requester/downloader/resetcontroler.go` | :39-47 | 新连接限流：9s 滑动窗口最多 80 次 |
| `requester/downloader/instance_state.go` | :27-32, :127-135 | 断点文件（proto3/JSON，Truncate+WriteAt 覆盖写） |
| `requester/transfer/rangelist.go` | :156-219 | Range 生成器（按 blockSize 顺序切块，原子推进） |
| `requester/rio/speeds/ratelimit.go` | :78-89 | 1s 窗口令牌计数限速（读取循环内阻塞） |
| `requester/transfer/download_status.go` | :96-124 | 已下载原子累加、`SpeedsPerSecond` 速度、剩余时间 |
| `internal/pcsfunctions/pcsdownload/pcsdownload.go` | :22-33 | 大小探测：`GET Range: bytes=0-32767` + 读 `x-bs-file-size` 头，响应体复用为 0 号分片 |
| `internal/pcsfunctions/pcsdownload/download_task_unit.go` | :75, :453 | 任务级重试 3 次；重试时重新取直链/读断点 |
| `internal/pcsfunctions/common.go` | :8-13 | 重试退避：`2s/4s/6s` 线性，封顶 6s |
| `baidupcs/download.go` | :106-151 | `DownloadFile`（method=download）与 `LocateDownload` 入口 |
| `baidupcs/prepare.go` | :452-472 | locatedownload POST 参数 |
| `baidupcs/netdisksign/locatedownloadsign.go` | :33-52 | 签名算法（C# `NetdiskSign` 已对齐，无需改动） |
| `internal/pcsconfig/pcsconfig.go` | :233-237 | 默认 CacheSize=64KB / MaxParallel=1 / MaxDownloadLoad=1 |
| `README.md` | :944, :1064 | 「SVIP 建议 max_parallel 10~20」「普通用户 max_parallel>1 会触发限速」 |

### 3.3 Go 侧可直接借鉴的结论

1. **大小探测**用 `GET Range: bytes=0-32767` + `x-bs-file-size` 头，且该响应体**复用为 0 号分片**（省一次请求，规避 HEAD 405）。
2. **分片落盘**直接 `WriteAt` 绝对偏移写最终文件，**无合并步骤**；"已完成 = 已写字节数"，天然幂等。
3. **断点文件** = worker 剩余 `[begin,end)` 列表 + gen 进度 + 文件大小，**每秒持久化一次**；恢复时按剩余 range 数定并发。
4. **片级自动恢复**：速度 < 峰值/6 → 最大剩余片折半拆分；0 速度 worker 强制重连；429/509 判限流重连（不判死）。
5. **403/404/416 判死** → 换下一个 dlink 或换通道；31626（user not authorized）→ 重试。
6. **VIP 加速没有 API 魔法（服务端按账号限速）**：会员直链每连接 ≈ 2.4MB/s、8 并发 ≈ 20MB/s（与官方客户端一致）；非会员直链 ≈ 100KB/s 且并发不叠加（服务端总量限速）。并发只对会员账号有意义（用户实测 2026-08-07，与 README「svip 建议 max_parallel 10~20」「普通用户 max_parallel>1 会触发限速」相互印证）。
7. **暂停/取消**引擎层机制完备（pauseChan + context + 断点文件），C# 只需把 UI 命令接到对应实现。
8. **任务队列**：小文件优先、按同时下载文件数均分并发（`AverageParallel`）。

---

## 四、总体架构设计

### 4.1 三层对应（C# 侧）

| 层 | C# 落位 | 对齐 Go |
|----|---------|---------|
| 引擎层（新增） | `BaiduPcs.Core/Download/` | `requester/downloader/` + `transfer/` + `rio/speeds/` |
| 协议层（扩展） | `BaiduPcs.Core/Api/BaiduPcsApiClient.Download.cs` | `baidupcs/download.go` |
| 命令/UI 层（改造） | `BaiduPcsAPP/Services/` + `ViewModels/` + `Views/` | `pcscommand/` + `pcsdownload/` |

### 4.2 引擎层新文件清单（`BaiduPcs.Core/Download/`）

| 新文件 | 职责 | 对齐 Go |
|--------|------|---------|
| `RangeSet.cs` | 分片范围数据结构 + 生成器（`GenRange` 原子推进）+ 断点恢复构造 | `rangelist.go` |
| `ChunkWorker.cs` | 单分片下载：Range 请求 → 206 校验 → `WriteAt` 偏移写 → 读超时/重连 | `worker.go` |
| `Downloader.cs` | 主控：大小探测 → 定并发/分片 → 启动 workers → 监控循环（补片/拆分/重连/断点持久化）→ 完成/取消/暂停 | `downloader.go` + `monitor.go` |
| `DownloadCheckpoint.cs` | 断点元数据 JSON 读写（`name.part.json`） | `instance_state.go`（proto3 → JSON） |
| `RetryPolicy.cs` | HTTP 状态分类（致命/限流/网络错误）+ 任务级重试退避 + 连接限流 | `worker.go:290-313` + `common.go` + `resetcontroler.go` |
| `SpeedMeter.cs` | 滑动窗口速度/剩余时间统计 | `speeds.go` + `download_status.go` |
| `RateLimiter.cs`（后置可选） | 1s 窗口令牌计数限速 | `ratelimit.go` |

### 4.3 协议层扩展（`BaiduPcsApiClient.Download.cs`）

- 新增 `GetDownloadLinksAsync(path)` → 返回**多备选直链列表**（`urls[]` 中 `encrypt==0` 且非空，跳过 `nb.cache` 开头主机，对齐 Go `DlinkPrefer`）；签名复用现有 `NetdiskSign.LocateDownloadUrlParam`（`Crypto/NetdiskSign.cs:50-55`，已对齐 Go 向量，**不改**）。
- 保留 `GetDownloadLinkAsync`（单链接，内部改调多链接版取第 0 个）。
- 降级通道：现有 `SendDirectAsync`（`BaiduPcsApiClient.Download.cs:36-46`，**已存在但无调用方**）+ `GeneratePcsUrl`（`BaiduPcsApiClient.cs:79-94`）→ method=download，作为直链获取失败 / 403 时的备选。

### 4.4 UI 层（`BaiduPcsAPP`）

| 文件 | 改造点 |
|------|--------|
| `Services/TransferManager.cs`（新增） | 任务队列：同时下载文件数上限（默认 1~2）、状态机驱动、暂停/恢复/取消/删除命令分发、同远程路径去重 |
| `ViewModels/TransferTaskViewModel.cs` | 绑定引擎状态/进度/速度/错误消息；新增 `PauseCommand/ResumeCommand/CancelCommand/DeleteCommand` |
| `ViewModels/TransfersViewModel.cs` | 挂接 TransferManager；计数与状态刷新统一 UI 线程封送 |
| `ViewModels/FileBrowserViewModel.cs` | `DownloadSelectedAsync` 改为经 TransferManager 提交任务（去掉 fire-and-forget `Task.Run`）；速度统计改滑动窗口 |
| `Views/TransfersView.axaml` | 每行加操作按钮（暂停/继续/取消/删除）与错误详情（Failed 行内展开） |

---

## 五、关键设计决策（每项含 Go 出处）

### 5.1 通道策略：locatedownload 为主，method=download 降级（用户已确认）

- **默认**：`GetDownloadLinksAsync`（locatedownload 签名直链），多备选链接逐个尝试（对齐 Go `DlinkPrefer`：失败则 `+1` 换下一个，`download_task_unit.go:542-544`）。
- **降级**：直链获取失败、403/404 判死且无更多 dlink 时 → method=download（`SendDirectAsync`，已有实现）重试一次；仍失败则任务失败并展示错误。
- 依据：Go 默认 `-mode locate`（`main.go:1078-1089`）；开发日志 3.2 记载 locatedownload「对任意文件可稳定获取」。
- 保留「复制链接」独立按钮不动（`FileBrowserViewModel.cs:236-260`）。

### 5.2 分片与并发（对齐 `SelectParallel` + `SelectBlockSizeAndInitRangeGen`）

- 分片档位：`<5MB → 256KB`；`<10MB → 512KB`；`<15MB → 1MB`；其余 `→ 4MB`（`downloader.go:26, :159-170`）。
- 并发数：`min(MaxParallel, totalSize/256KB + 1)`；服务端不支持 Range（探测非 206）→ 1（`downloader.go:127-144`）。
- `MaxParallel`：统一默认值 **8**（见 5.8），常量先行，后续接设置页。
- 缓存缓冲：每 worker 复用 128KB 读缓冲（可调），不做 Go 的 cachepool 缓冲池（简化）。

### 5.3 大小探测（对齐 `pcsdownload.go:22-33`）

- 第一个请求：`GET dlink` + `Range: bytes=0-32767`；
- 从响应头 `x-bs-file-size` 读总大小（缺省用 `Content-Range` 的 total 兜底）；
- 该响应体**复用为 0 号分片数据**（剩余部分按 0 号 range 的余量继续读），省一次网络往返；
- 探测请求失败且 `x-bs-file-size` 缺失 → 单线程模式按流式下载（对齐 Go 单线程分支，不记断点）。

### 5.4 落盘（对齐 `worker.go:347-456`）

- 数据写 `name.part`：`FileStream(FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)` + `RandomAccess.Write`（或 `FileStream.Position=offset` + 写）按**绝对偏移**写入；
- 各 worker 共享一把写锁（`SemaphoreSlim(1,1)`），对齐 Go `writeMu`（减轻磁盘压力）；
- **无合并步骤**：片间无缝隙（range 生成器保证覆盖 `[0,total)`），写完校验文件长度 = totalSize；
- 完成：删 `name.part.json` → 覆盖确认后重命名 `name.part` → 正式文件（保留现有覆盖行为，覆盖确认后置可选）。

### 5.5 断点续传（对齐 `instance_state.go` + `transfer.proto`，JSON 化）

- 元数据文件：`name.part.json`，结构：
  ```json
  { "TotalSize": 123456789, "BlockSize": 4194304, "GenBegin": 8388608,
    "Ranges": [ { "Begin": 0, "End": 4194304 }, ... ], "Dlink": "https://..." }
  ```
  （对齐 proto 字段：TotalSize / GenBegin / BlockSize / Ranges / RangeGenMode；C# 用 System.Text.Json，UTF-8，无 BOM）
- 写入时机：监控循环每 **~1s** 持久化一次（对齐 monitor 990ms tick，`monitor.go:382-388`），`FileMode.Create` 覆盖写；写入失败仅记日志不中止下载（断点尽力而为）。
- 恢复：启动时发现 `name.part.json` → 读入 → 并发数 = 剩余 range 数（`SelectParallel :132`）→ 从各 range `Begin` 续传；已下载量 = `GenBegin - Ranges 剩余字节`（对齐 `download_instanceinfo.go:30`）。
- **兼容旧 .part**：存在 `name.part` 但无 `.json` → 走整文件续传模式（`Range: bytes=existing-`，即 v0.5 行为），成功后自动升级为分片断点。
- 取消/删除任务：删 `.part` + `.part.json`；暂停保留。
- 成功后：删 `.part.json`（对齐 `downloader.go:466-468` removeInstanceState）。

### 5.6 错误分类与重试（对齐 `worker.go:290-313` + `common.go` + `resetcontroler.go`）

| 状态/场景 | 分类 | 处理 |
|-----------|------|------|
| 200 / 206 | 正常 | 校验 `Content-Range` total == TotalSize；200 且请求过 Range → 单线程降级（重启下载器，不记断点） |
| 403 / 404 / 416 | **致命** | 片级停止；任务级：换下一个 dlink（`DlinkPrefer+1`）重试；dlink 耗尽 → 降级 method=download 一次；仍失败 → 任务失败 |
| 429 / 509 | 限流 | 指数退避（1s/2s/4s）后重连该 worker；不判死 |
| 406 / 其他 4xx、5xx | 网络错误 | 重连该 worker（受连接限流约束） |
| 网络异常 / 读超时 / 断流 | 网络错误 | 重连该 worker |
| 响应体提前 EOF（长度不符） | 网络错误 | 重连该 worker，从当前 range Begin 续传 |
| PCS 错误码 31626（user not authorized） | 通道问题 | 换 method=download 通道重试 |

- **卡死兜底**（对齐 `monitor.go:310-342`）：worker 级读超时（默认 30s 无数据）强制断连重连；监控循环检测 0 速度 worker 同样处理。
- **动态拆分**（对齐 `monitor.go:269-307`，实现后置可选）：总速度 < 峰值/6 且有空闲 worker → 剩余最大片折半拆分。
- **任务级重试**：3 次，退避 `2s/4s/6s`（对齐 `common.go:8-13`）；重试时重新取直链、重新读断点（对齐 `download_task_unit.go:453`）。
- **连接限流**：9s 窗口最多 80 次新连接（对齐 `resetcontroler.go:39-47`），防风控。

### 5.7 暂停 / 恢复 / 取消（对齐 `downloader.go:498-522`）

- **暂停**：置全局 `Paused` 标志（`AsyncManualResetEvent` 或 `SemaphoreSlim` 门控），worker 读取循环轮询 → 挂起；**当前 range 保留**（不回收）；暂停瞬间立即持久化断点。
- **恢复**：放行门控，worker 从各自 range 的当前 `Begin` 重新发 Range 请求（对齐 `worker.go:159-164`）。
- **取消**：`CancellationTokenSource.Cancel()` + 关闭各响应流 → 删除 `.part` 与 `.part.json`（对齐 `worker.go:167-176` + `StatusCodeCanceled`）。
- 暂停/取消均通过 `TransferManager` 路由，UI 只调命令。

### 5.8 并发策略：统一并发，不做会员判断

**实测结论（用户实测 2026-08-07，与 Go README「svip 建议 max_parallel 10~20」「普通用户 max_parallel>1 会触发限速」相互印证）**：

- 会员生成的直链：单连接 ≈ **2.4MB/s**，**8 并发 ≈ 20MB/s** —— 与官方客户端限速一致（官方按"每连接"限速，并发叠加）；
- 非会员直链：单连接 ≈ **100KB/s**，并发后仍 ≈ **100KB/s**（服务端按账号总量限速，并发无增益、也无额外惩罚）。

**结论**：

- **取消会员等级判断** —— 不做 `getinfo` 的 `vip`/`vip_type` 解析，不感知账号等级；
- **统一并发默认值 8** —— 会员实测甜点值（8 并发 ≈ 20MB/s 且不过度请求）；非会员无论多少并发都是 ~100KB/s，无副作用；
- 并发值先以常量落地（`DownloaderConfig.MaxParallel = 8`），后续设置页（v0.9）开放调整；
- **UA 仍全链路统一 netdisk 安卓 UA**（直链获取与 CDN 下载请求均用 `ApiConstants.PanUserAgent`）——与 Go 原版及官方客户端身份一致，无副作用。

### 5.9 限速（后置可选，本期只设计）

- 对齐 `ratelimit.go:78-89`：全局限速器 = 1s 窗口内已放行字节计数，worker 每读一段先 `await limiter.AddAsync(n)`（超出限额则阻塞至下个窗口）。
- 默认 0（不限速）；接入时机：设置页落地（v0.9）时一并开放。

### 5.10 重启恢复（后置可选）

- APP 启动时扫描默认下载目录与任务记录中的 `.part.json` → 弹窗询问是否续传（对齐 Go「重启后手动执行同名下载命令即自动续传」的体验）。
- 本期先保证：进程内暂停/取消后**下次启动同一文件**能识别 `.part.json` 续传。

---

## 六、任务管理 UI 交互设计

### 6.1 状态机（复用现有 `TransferTaskState` 枚举）

```
Queued ──调度──▶ Running ──暂停──▶ Paused ──恢复──▶ Running
  │                │  │                             │
  │                │  └──完成──▶ Completed           │
  │                └──取消──▶ Canceled ◀──取消───────┘
  └──取消──▶ Canceled      （Failed 可删除；取消即删除 .part）
  Running ──重试耗尽──▶ Failed ──(重试中内部转 Running)
```

### 6.2 按钮可用性矩阵

| 状态 \ 按钮 | 暂停 | 继续 | 取消 | 删除 |
|-------------|------|------|------|------|
| Queued | ✗ | ✗ | ✓ | ✓ |
| Running | ✓ | ✗ | ✓ | ✗（或二次确认后=取消） |
| Paused | ✗ | ✓ | ✓ | ✓ |
| Completed | ✗ | ✗ | ✗ | ✓ |
| Failed | ✗ | ✓（重试） | ✗ | ✓ |
| Canceled | ✗ | ✗ | ✗ | ✓ |

- 「删除」仅移除任务行（对未完成任务先取消再移除）；「失败 → 继续」= 重试（重置重试计数）。

### 6.3 错误详情展示

- `TransferTaskViewModel.DetailText` 已存在（`TransferTaskViewModel.cs:32`），补 UI 绑定：Failed/Canceled 状态行下方展开一行错误说明（`TransfersView.axaml` 模板内 `IsVisible="{Binding State, Converter=...}"` 或状态字符串相等比较）。
- 错误消息统一映射：引擎抛 `DownloadException(阶段, 原因, 内部异常)`，UI 展示中文（如「直链获取失败：缺失 UID」「限流，等待重试」）。

### 6.4 并发与队列

- `TransferManager`：`MaxConcurrentDownloads`（默认 1~2）→ 队列按提交序调度（小文件优先后置可选）；同一远程路径去重（已在队列/进行中则提示）。

---

## 七、测试计划

### 7.1 测试基础设施（新增）

- `BaiduPcs.Core.Tests/Infrastructure/TestHttpServer.cs`：基于 `HttpListener`（或 Kestrel 测试宿主）的本地测试服务器，可编程行为：
  - 支持 `Range`/`206`、`Content-Range`、`x-bs-file-size` 头；
  - 模拟 429/509 限流、403 致命、断流（响应中途关闭连接）、chunked 响应；
  - 记录收到的请求序列（断言 Range 头与并发数）。

### 7.2 用例清单

| 主题 | 用例 |
|------|------|
| Range 生成器 | 各档位切分正确；边界（total 恰为 blockSize 整数倍 / 余数）；恢复断点构造剩余 range；并发 = min(配置, total/256KB+1) |
| 并发分片 | 大文件（>2MB）触发 ≥2 并行请求；各片偏移写后整文件内容与源一致；写锁串行化不产生竞态 |
| 大小探测 | `x-bs-file-size` 读取；缺省时 Content-Range 兜底；探测响应复用为 0 号分片 |
| 断点 | 下载中段停止 → `.part.json` 内容正确；恢复后只请求剩余 range；完成后 `.part.json` 删除、`.part` 重命名；旧 `.part`（无 json）整文件续传兼容 |
| 状态分支 | 请求 Range 收到 200 → 单线程降级；收到 206 → 正常；Content-Range total 不符 → 报错重连 |
| 重试 | 429 两次后成功（断言退避与重连次数）；403 换下一个 dlink；dlink 耗尽降级 method=download；3 次任务级重试耗尽 → Failed |
| 卡死兜底 | 模拟断流（发一半断开）→ worker 重连从当前 Begin 续传 |
| 暂停/恢复/取消 | 暂停后请求挂起、range 保留；恢复后从 Begin 续传；取消删除 `.part`/`.part.json`、状态 = Canceled |
| 进度/速度 | 回调序列单调递增、TotalBytes 正确（含 x-bs-file-size 场景）；速度滑动窗口单测 |
| 状态机 | TransferManager：队列调度、同路径去重、按钮可用性矩阵对应状态转换 |

### 7.3 约束

- 现有 59 个用例（含密码学 Go 向量）**保持不动、必须全绿**；
- 网络相关测试全部走本地 TestHttpServer，不访问真实百度。

---

## 八、实施里程碑与验收标准

### M1 引擎层（Core，含单测）

`RangeSet` / `ChunkWorker` / `Downloader` / `DownloadCheckpoint` / `RetryPolicy` / `SpeedMeter` + 对应测试。

### M2 协议层

`GetDownloadLinksAsync`（多备选直链）+ 降级 method=download 接线。

### M3 任务管理 + UI

`TransferManager` + `TransferTaskViewModel` 命令 + `TransfersView.axaml` 按钮/错误详情 + `FileBrowserViewModel` 改造（去 fire-and-forget、速度统计修复、UI 封送、去重）。

### M4 全量测试

补齐 7.2 用例，`dotnet test` 全绿（新旧用例）。

### M5 文档与验收

- `README.md`：功能清单更新（多线程分片下载、暂停/恢复/取消/删除、locatedownload 主通道 + 自动降级、统一并发 8）；
- `DEVELOPMENT_LOG.md`：追加「下载引擎改造」设计决策与踩坑（200/206 校验、429 判限流、panUA 防限速、WriteAt 免合并、.part.json 断点格式、WriteAt 与写锁经验）；
- **验收标准**：
  1. 大文件（≥100MB）多线程下载速度显著提升（对照单线程）；
  2. 断点续传跨进程重启有效（`.part.json` 恢复）；
  3. 暂停/继续/取消/删除全部可用，状态与按钮矩阵一致；
  4. 失败任务展示可读中文错误，重试机制生效（限流/断流自动恢复）；
  5. 引擎相关单测全绿，旧测试不回归。
