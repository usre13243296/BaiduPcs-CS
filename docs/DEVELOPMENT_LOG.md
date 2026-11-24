# BaiduPcs 开发日志与核心思路

> 本文件记录 v0.5 → v0.6 开发过程中的核心架构决策、关键踩坑与后续规划，供维护者与贡献者阅读。
> 面向使用者的功能说明见根目录 `README.md`，文档索引见 `docs/README.md`。

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

## 三-bis、下载引擎改造（2026-08-08 落地，v0.8 前瞻）

> 详细方案见 `docs/DOWNLOAD_ENGINE_DESIGN.md`。本文记录落地过程中的设计决策与踩坑。

### 架构（三层对齐 Go BaiduPCS-Go）

```
BaiduPcs.Core/Download/      引擎层（与百度协议解耦）：Downloader 主控 / ChunkWorker 分片 /
                             RangeGenerator / DownloadCheckpoint(.part.json) / RetryPolicy / SpeedMeter
BaiduPcs.Core/Api/           协议层：GetDownloadLinksAsync（多备选直链）+ BuildDownloadUrlsAsync
                             （method=download 自动降级兜底）
BaiduPcsAPP/                 任务层：TransferManager（队列/并发2/暂停/取消/删除/去重）
```

### 关键设计决策

1. **分片并发**：档位 <5MB→256KB … >15MB→4MB；并发统一 8（不做会员判断，见 5.8 实测结论——会员 8 并发 ≈ 20MB/s 与官方一致，非会员服务端总量限速 100KB/s 并发无增益）。
2. **通道**：locatedownload 签名直链为主（encrypt=0、跳过 nb.cache 主机），403 判死后换下一个 dlink，全部耗尽自动降级 method=download 官方通道。
3. **落盘**：`RandomAccess.WriteAsync` 绝对偏移写 `.part`，免合并、天然幂等；写锁串行化减轻磁盘压力。
4. **断点**：`.part.json` 元数据（TotalSize/BlockSize/GenBegin/Ranges[]），每 ~1s 持久化；恢复时并发数 = 剩余分片数，进度不丢。
5. **错误分类**（对齐 Go worker.go）：403/404/416 致命判死换链；429/509 限流退避重连；读超时（默认 30s）断流重连；单分片连续失败 8 次升级致命。
6. **UA**：全链路统一 netdisk 安卓 UA（`ApiConstants.NetdiskUa`），与官方客户端身份一致。

### 踩坑记录（重要）

1. **`Task.WhenAny` 不传播取消**（最严重的坑）：`await Task.WhenAny(Task.Delay(500, ct), needWork)` 只等待 WhenAny 自身——它总是成功完成（返回先完成的任务），**取消后 Delay/needWork 以 canceled 状态立即完成 → 监控循环空转（实测 3 亿轮）→ 任务永不结束**。修复：循环头 `ct.ThrowIfCancellationRequested()` + 检查 `winner.IsCanceled`。**任何"等待组内任一"的场景都必须显式检查取消**。
2. **`AsyncManualResetEvent` 初始状态**：暂停门控初始必须放行（`signaled: true`），否则所有 worker 挂死在第一行等待——表现为"下载无任何请求发出"。
3. **GNU `timeout` 强杀 dotnet test 会留孤儿 testhost**（Windows 下 TerminateProcess 不递归清理进程树）：孤儿进程锁着测试 DLL，下次运行 vstest 报"测试主机进程崩溃"。正确做法：不用 GNU timeout、不接 `| tail` 管道（非流式缓冲制造"无输出"假象），必要时 `taskkill /F /T /PID` 清整棵进程树。
4. **`HttpListener.Response.Abort()` 在 Windows 偶发 testhost 崩溃**：断流模拟改用"声明 Content-Length 大于实际写入后 Close"。
5. **测试断流行为不能按"第一个请求"匹配**：并发下分片请求可能先于探测到达服务器，行为必须按请求特征（Range=bytes=0-32767）匹配。

### 测试

- 引擎单元 + 集成共 **52 个新用例**（Range 切分/并发偏移写/断点取消恢复/429 重试/403 换链/断流重连/暂停恢复/进度单调/200 单线程降级），本地 TestHttpServer 模拟，不访问真实网络。
- 全量 **118 个用例全绿**（旧 59 + 协议层 7 + 引擎 52）。

---

## 三-ter、上传引擎落地（2026-08-08，v0.6 完成）

> 设计对齐 Go BaiduPCS-Go `internal/pcsfunctions/pcsupload/` + `baidupcs/upload.go`。
> 范围：多文件选择 → 秒传预检（precreate 二合一）→ 分片并发上传（superfile2）→ 合并（create）；
> 同名自动递增重命名；进程内断点续传；接入现有传输面板。目录递归/跨进程恢复后置。

### 架构（三层与下载对称）

```
BaiduPcs.Core/Api/BaiduPcsApiClient.Upload.cs   协议层：PrecreateAsync（秒传+会话二合一）/ UploadTmpFileAsync
                                                 （superfile2 multipart）/ CreateAsync（合并）/ LocateUploadHostsAsync
BaiduPcs.Core/Upload/                           引擎层：Uploader 主控 / UploadChecksum 三份 MD5 /
                                                 DataOffsetCalculator（data_offset）/ UploadSession（进程内断点）/ UploadException
BaiduPcsAPP/                                    任务层：TransferManager.EnqueueUpload + RunUploadAsync 分支；
                                                 TransferTaskViewModel.Engine 抽象为 ITransferEngine（Core/Transfer/）
```

### 关键设计决策

1. **秒传与 precreate 二合一**（对齐 Go rapidUploadV2）：一次 POST `pan.baidu.com/api/precreate`，服务器命中返回 `return_type=2` 直接完成，否则返回 `uploadid` 进入分片上传；参数含 `data_offset`（`md5("{uk}{md5}{时间}")` 前 8 hex 取模）+ `data_content`（偏移处 4KB base64 去 `=`）+ `block_list` + `autoinit=1`。
2. **三份校验和单趟流式计算**：全文件 MD5（content-md5）+ 前 256KB MD5（slice-md5）+ 按分片大小的逐块 MD5（block_list），一次读文件同时喂三个增量哈希（块哈希按块边界切分缓冲）。
3. **分片与并发**：`<8GB→4MB / 8~32GB→16MB / ≥32GB→64MB`（对齐 Go getBlockSize）；并发统一 4（Go MaxUploadParallel）；host 经 locateupload 动态获取随机选一，失败降级 pcs.baidu.com。
4. **重名策略（用户决策）**：上传前 meta 探测目标，存在则自动递增重命名（`报告.txt` → `报告2.txt` → `报告3.txt`…），不做询问/覆盖/跳过；探测失败按不存在处理（rtype=3 兜底覆盖）。
5. **进程内断点续传**：`UploadSession{uploadid, 已传分片 md5}` 由任务层持有（跨 Uploader 实例存活）；失败重试/暂停恢复时复用 uploadid，已传分片跳过；`31363`（上传状态过期）→ 清会话 → 重新 precreate → 整体重传一轮（对齐 Go 删库重传）。
6. **错误分类**（对齐 Go upload_task_unit.go:338-397）：HTTP 400/401/403/413 与 31061/31112 致命不重试；429/509 限流退避；其余网络错误/远端错误重试（单分片上限 8 次）；退避复用 `DownloadRetryPolicy`。
7. **引擎接入 UI**：`TransferTaskViewModel.Engine` 由 `Downloader` 硬编码抽象为 `ITransferEngine`（Pause/Resume 两方法，取消走 Cts）；TransferManager 的 `RunAsync` 抽公共调度框架（槽位/状态机/封送），下载/上传各成 `RunDownloadAsync`/`RunUploadAsync` 分支；上传去重键 = 本地文件路径，下载 = 远端路径，互不冲突。

### 踩坑记录（重要）

1. **BCL multipart 无法表达空 filename**（最大坑）：`MultipartFormDataContent.Add(content, name, fileName)` 校验 fileName 非空白（.NET 8+）；而 `ContentDispositionHeaderValue.FileName = ""` 序列化时被**直接省略**（body 里没有 filename 属性），与 Go 的 `filename=""` 不一致。解决：手写 `UploadMultipartContent : HttpContent`（流式序列化，对齐 Go multipartreader 的逐字节格式）。
2. **URL 编码大小写分裂**：`HttpUtility.ParseQueryString(...).ToString()` 生成的 query 用**小写** hex（`%2f`），而 `FormUrlEncodedContent`（`Uri.EscapeDataString`）用**大写** hex（`%2F`）——上传同时用了两种（superfile2 的 URL query vs precreate/create 的 form body），测试断言必须区分大小写。
3. **HttpContent 生命周期**：协议层 `using` 释放 multipart（会释放传入的上传流）；Mock handler 同步返回后测试再读 `request.Content` 会 `ObjectDisposedException`——测试必须在 responder 内捕获 body。
4. **并发任务抛异常**：3 块并发全部 403 时每个 worker 各发 1 次请求（不是重试）；`Task.WhenAll` 只抛第一个异常——断言请求数时按「每块各一次」理解，不要误判为重试。
5. **传文件流所有权**：`UploadTmpFileAsync` 内 `using` 释放 `UploadMultipartContent`（含 FileStream）——引擎每片新建 FileStream 传入即可，双重释放幂等无碍。
6. **multipart 误发文件剩余部分**（2026-08-08 修复，用户实测暴露）：直接把整个 FileStream 传给 multipart，流式序列化会从当前 Position 读到 **EOF**——分片 1 会把「分片 1 到文件尾」全部发出。单测当时只断言 multipart 字段名、未断言分片数据长度，未暴露。解决：`ProgressReadStream` 限长子流（只暴露 `[offset, offset+length)`，对齐 Go ReaderLen64 限长语义），并补「分片数据长度 + 偏移内容一致」断言。
7. **进度/速度统计（用户实测两轮反馈，2026-08-08 深挖）**：
   - 现象：进度条不走（直到完成瞬满），速度显示 xxxTB/s → 修复后仍 xxxMB/s。
   - 排查（三路子代理并行，结论互证）：
     a) **上传侧无时间驱动刷新**：所有 UI 更新挂在读回调上，停顿（等待响应/重试退避/慢网络）期间零刷新；速度一旦算出就冻结在 UI——下载侧有 500ms monitor 心跳（Downloader.cs:294），上传没有。这是「进度不走 + 速度冻结」的总根因。
     b) **秒传/续传跳过块一次性批量累计**：跳过 300MB 恰逢 1s 重算边界 → speed = 300MB/1s = 285MB/s 并冻结（旧滑动窗口模型同源）。本地瞬间完成的字节不应计入网络速度。
     c) **重试时进度回调置 null 静默**（此前为防虚高做的设计）——重试期间完全黑屏。
     d) **checksum 预计算阶段零进度**——大文件开头长时间 0%。
     e) **SpeedMeter 平台单位混淆**：`_periodTicks = TimeSpan.TicksPerSecond` 与 `Stopwatch` 原始 tick 比较，仅 Frequency==10MHz 时正确；Linux（1e9）上 1s 闸门坍缩为 10ms → 瞬时爆发持续 xxxMB/s。
   - 修复：
     - `Uploader` 新增 **RunMonitorAsync 心跳**（500ms，对齐下载侧）：周期刷新进度/速度、让 SpeedMeter 累计按真实时间衰减；
     - `ReportProgress` 增加 `countSpeed` 参数：checksum 读取/续传跳过/秒传等本地瞬间完成的字节**只计进度不计速度**，杜绝批量累计虚高；
     - 重试不再静默（每次尝试都插桩，clamp 兜底虚高）；
     - checksum 阶段插桩进度（单调不倒退：checksum 读字节计入总进度）；
     - `SpeedMeter._periodTicks = Stopwatch.Frequency`（平台无关）；
     - `TransferManager.RunUploadAsync` 开头 `await Task.Run(...)` 确保 Progress<T> 在线程池创建（槽位空闲时 RunAsync 可能同步继续在 UI 线程，捕获 DispatcherSynchronizationContext 导致回调双跳）。
   - 另确认（Agent 排查排除项）：请求体是 SocketsHttpHandler 直写 socket 的流式发送（Content-Length 已知走 ContentLengthWriteStream），**不存在**请求体缓冲/预读；307/308 重定向会重序列化已耗尽流导致空 body 悬挂（上限 50s 超时后重试）——百度 superfile2 不重定向，列为已知风险。
8. **秒传与真实上传的进度区分（用户实测证实秒传猜想后落地）**：用户上传内容 MD5 在百度全网已存在 → precreate 命中 `return_type=2` → 服务器零接收、瞬间完成、无速度（先前"极快 + 无速度"现象即此）。同时发现上一版「checksum 阶段插桩进度」的缺陷：checksum 读一遍 = 总字节 → 进度提前打满 100%，真实上传阶段反而冻结不动。修复：
   - checksum 预计算**不再计入上传进度**（进度从分片上传开始走；checksum 阶段由监控心跳保持 UI 刷新）；
   - 秒传命中时进度**瞬间读满**（0→100，无渐进中间值），`Uploader.WasRapidUpload` 标记命中；
   - `TransferManager` 完成时 DetailText 标注「秒传命中：{远端路径}」，与真实上传「{远端路径}」可一眼区分。
8. **测试时序竞态（既有下载测试偶发）**：`Download_Cancel_PreservesCheckpoint` 在快机器上偶发——取消依赖断点监控循环（~1s tick）落地，小文件瞬时下完导致「断点未写」或「取消不生效」。修复：等待「请求数 ≥5 且断点文件已存在」再取消（断点存在 = 下载未完成且取消后必然保留），彻底消除竞态；恢复阶段的「请求计数达到 N」类条件同理需注意分片档位（16MB → 4MB 块仅 4 块，总请求数 5）。

### 测试

- 协议层 14 个用例（precreate 参数全断言/return_type/uploadid 复用/错误码；superfile2 URL+multipart 字段；create 顺序；locateupload 过滤规则；31112/31363 映射）。
- 引擎层 30 个用例（三份 MD5 向量/跨块边界/分片档位/data_offset 取模；秒传命中无分片请求；单块/多块全流程与进度单调；单片失败重试；403 致命；31363 自动重传；会话续传跳过已传片；暂停无请求/恢复续传；取消；重名递增 2/3；本地文件缺失/空文件）。
- 全量 **164 个用例全绿**（当前基线；本文早期记录的 162 为当时快照，之后又补了 2 个用例）。

### 遗留（后置，README/文档已注明）

- 目录递归上传（v0.7 与搜索/移动一起）；跨进程重启恢复（需任务持久化 + uploadid 会话探测）；
- >32GB 提示、>64MB 前置空间检查（靠 31112 错误映射兜底）；host 每 256 片轮换；上传限速。

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

### v0.6 — 上传与传输管理 ✅ 已落地（2026-08-08）
- ~~上传引擎：单文件 → 分片（precreate + superfile2）→ 秒传（md5 预检）→ 断点续传~~ ✅
- ~~传输任务管理：队列、并发数、暂停/取消、任务持久化（重启恢复）~~ ✅（暂停/取消/队列已落地；**任务持久化（跨进程恢复）后置**）

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