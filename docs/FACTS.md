# 项目事实表（唯一事实来源）

> **本文件是项目所有数字与配置的唯一来源。** 其它文档（README / CONTRIBUTING / SECURITY / llms.txt / docs/*）只**引用**本文件，不再复述这些值。
>
> - 维护规则：改这些值 → 只改本文件（+ 代码常量），然后跑 `tools/check-docs.ps1`（Windows：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-docs.ps1`；Linux / macOS：`pwsh -NoProfile -File tools/check-docs.ps1`）。
> - 自动校验：`tools/check-docs.ps1` 会核对本表的**版本号**与**测试用例总数**是否与代码一致，并检查全仓文档的相对链接。CI 已接入。
> - 表内带「🔒 自动校验」标记的行有固定格式，机器会解析，**不要改标签文字**。
> - 历史文档里的数字是当时的快照，标注「当时」，不在本表管辖范围。

最后核对：2026-11-25（`main @ f56313a`）

---

## 一、版本与身份

| 项目 | 值 |
|------|-----|
| 当前版本 | v0.6.0 |
| 版本号来源 | `BaiduPcsAPP/BaiduPcsAPP.csproj` 的 `<Version>`（🔒 自动校验：与本行一致） |
| 许可证 | MIT |
| 项目定位 | 非官方、MIT 开源的百度网盘桌面客户端，与百度公司无关联 |
| 协议参考 | [BaiduPCS-Go](https://github.com/qjfoidnh/BaiduPCS-Go)（Go，MIT） |
| 仓库（主） | <https://github.com/usre13243296/BaiduPcs-CS> |
| 仓库（镜像） | <https://gitee.com/xnbd5665/BaiduPcs-CS>（由 workflow 自动镜像） |

## 二、技术栈

版本号以各 `csproj` 为准；升级依赖时同步改本表与 `SECURITY.md` 的依赖说明。

| 层 | 组件 | 版本 |
|----|------|------|
| 运行时 | .NET | `net10.0` |
| UI | Avalonia（含 Desktop / Themes.Fluent / Fonts.Inter） | 12.1.1 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| 测试框架 | xUnit | 2.9.3 |
| 测试宿主 | Microsoft.NET.Test.Sdk | 17.14.1 |
| 覆盖率 | coverlet.collector | 6.0.4 |
| 开发用 SDK | .NET SDK（本机已验证） | 10.0.404 |

> `BaiduPcs.Core` **零第三方运行时依赖**（只有 BCL），这是「可当库用」的前提，见 `CONTRIBUTING.md` 的分层约定。

## 三、构建与测试

| 项目 | 值 |
|------|-----|
| 构建 | `dotnet build BaiduPcs.slnx -c Release` → 基线 0 警告 0 错误 |
| 测试 | `dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj` |
| 测试用例总数 | 164（🔒 自动校验：110 个 `[Fact]` + 54 个 `[InlineData]`） |
| 本机实测耗时 | 约 17 秒（Windows，仅供参考，不作承诺） |
| 测试覆盖范围 | 密码学（对齐 Go 测试向量）、协议层（mock handler）、下载引擎、上传引擎 |
| **未覆盖** | `BaiduPcsAPP`（桌面层）目前 **0 个用例**——见 `docs/CODE_REVIEW_2026-11-25.md` T8 |

## 四、发布产物（6 个 RID）

| RID | 平台 | 场景 |
|-----|------|------|
| `win-x64` | Windows x64 | 绝大多数 Windows 10/11 |
| `win-arm64` | Windows arm64 | 骁龙 X 系列等 ARM Windows |
| `linux-x64` | Linux x64 | 常规 x86_64 发行版 |
| `linux-arm64` | Linux arm64 | NAS / 软路由 / 树莓派 |
| `osx-x64` | macOS Intel | Intel Mac |
| `osx-arm64` | macOS Apple Silicon | M 系列 Mac |

发布包为**自包含单文件**（`--self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`），无需预装 .NET 运行时。命令见 `docs/MAINTENANCE.md` §三。

## 五、传输参数

> 改这些值 = 同时改「本表」与「代码常量」两处。代码常量是唯一实现，本表是对外说明。

### 5.1 下载

| 参数 | 值 | 代码位置 |
|------|-----|----------|
| 并发分片数 | 8（统一，不区分会员） | `DownloaderOptions.MaxParallel` |
| 分片档位 | <5 MB → 256 KB；5–10 MB → 512 KB；10–15 MB → 1 MB；≥15 MB → 4 MB | `BlockSizes.Select` |
| 落盘方式 | `RandomAccess` 绝对偏移写 `.part`，无合并步骤 | `DownloaderContext.WriteAtAsync` |
| 断点文件 | `<文件名>.part`（数据）+ `<文件名>.part.json`（元数据） | `DownloadCheckpoint` |
| 断点持久化间隔 | 约 1 秒 | `Downloader.CheckpointIntervalMs` |
| 单分片重试上限 | 8 次 | `DownloaderOptions.MaxChunkRetries` |
| 退避 | 网络错误 1/2/4 秒；限流（429/509）1/2/4 秒 | `DownloadRetryPolicy` |
| 断流判定 | 30 秒无数据即重连 | `DownloaderOptions.ReadTimeout` |
| 新建连接限流 | 9 秒窗口内最多 80 次 | `ConnectionRateLimiter` |
| 进度刷新 | 500 ms | `Downloader.MonitorIntervalMs` |
| 致命状态码 | 403 / 404 / 416 → 换直链；耗尽后降级官方通道 | `DownloadErrorClassifier` |

### 5.2 上传

| 参数 | 值 | 代码位置 |
|------|-----|----------|
| 并发分片数 | 4 | `UploaderOptions.MaxParallel` |
| 分片档位 | <8 GB → 4 MB；8–32 GB → 16 MB；≥32 GB → 64 MB | `Uploader.GetBlockSize` |
| 单文件上限 | 128 GB | `BaiduPcsApiClient.MaxUploadSize` |
| 校验和 | 全文件 MD5 + 前 256 KB MD5 + 逐块 MD5，一次读文件同时计算 | `UploadChecksum` |
| 秒传片段 | `data_offset` 随机 + 4 KB `data_content`（base64 去 `=`） | `DataOffsetCalculator` |
| 单分片重试上限 | 8 次 | `UploaderOptions.MaxChunkRetries` |
| 会话失效 | 错误码 31363 **在分片阶段**触发：清会话 → 重新 precreate → 整体重传（最多 2 轮）。`create` 阶段不触发（已知缺陷，见 CODE_REVIEW P1-10） | `Uploader.UploadAsync` |
| 重名策略 | 目标已存在则自动递增 `文件2.ext`、`文件3.ext`… | `Uploader.ResolveTargetPathAsync` |
| 上传服务器 | `locateupload` 动态获取，失败降级 `pcs.baidu.com` | `Uploader.PickHostAsync` |

### 5.3 队列

| 参数 | 值 | 代码位置 |
|------|-----|----------|
| 同时传输任务数 | 2（下载与上传**共用**同一配额） | `TransferManager` 构造参数 |
| 去重键 | 下载 = 远端路径；上传 = 本地文件路径 | `TransferManager.EnqueueDownload/EnqueueUpload` |

## 六、数据存放位置

| 内容 | 文件名 | 位置 |
|------|--------|------|
| 账号配置 | `accounts.json`（**明文 JSON**） | Windows：`%APPDATA%\BaiduPcsApp`<br>Linux / macOS：`~/.config/BaiduPcsApp` |
| 异常日志 | `app.log` | 同上 |
| 默认下载目录 | — | `~/Downloads/BaiduPcs`（Windows 为 `%USERPROFILE%\Downloads\BaiduPcs`） |

> **关于 macOS**：代码只调用 .NET 的 `SpecialFolder.ApplicationData`，**没有平台分支**。按 .NET 的 Unix 实现，Linux 与 macOS 都落到 `~/.config`（macOS 上并不是 Apple 规范的 `~/Library/Application Support`）。
> 若需改成 Apple 规范路径，要修改 `AppPaths` 增加平台分支——列入路线图。**本行待实机确认**（在 Mac 上执行 `dotnet run --project BaiduPcsAPP/BaiduPcsAPP.csproj` 后查看配置目录即可）。

## 七、性能实测（仅供参考）

| 账号类型 | 结果 |
|----------|------|
| 会员 | 8 并发约 20–30 MB/s（与官方客户端同量级） |
| 非会员 | 服务端按账号总量限速，约 100 KB/s，增加并发不叠加 |

来源：2026-08-07 用户实测 + `docs/DOWNLOAD_ENGINE_DESIGN.md` 5.8。服务端策略随时可能变化，本表不是承诺。

## 八、即将变更（改动落地后请把对应行并入上文，并删除本节对应条目）

| # | 现状（v0.6.0） | 目标 | 关联 |
|---|----------------|------|------|
| 1 | TLS 证书校验**默认关闭**（`BaiduHttpClient.BypassSslValidation = true`） | 默认开启，需显式 opt-in 才关闭 | 评审 P0-3 / 阶段 0 |
| 2 | **取消**下载会删除 `.part` 与 `.part.json`（丢弃进度） | 取消保留断点，仅「删除任务」才清理文件 | 评审 P1-3 / 阶段 1 |
| 3 | 上传重名探测失败即按「不存在」处理 → `rtype=3` **覆盖**远端同名文件 | 探测失败即失败重试；覆盖需显式确认 | 评审 P0-6 / 阶段 0 |
| 4 | 下载完成时**静默覆盖**本地同名文件 | 自动递增或询问 | 评审 P0-6 / 阶段 0 |
| 5 | 上传任务失败后**只能重试一次**（去重键 bug） | 可反复重试 | 评审 P1-2 / 阶段 1 |
| 6 | `Mac 数据目录` 为推断值 | Mac 实机确认后定稿 | §六 |

## 九、只看不抄（避免复制后过期）

| 需要什么 | 去哪里看 |
|----------|----------|
| 错误码 → 用户可读文案 | `BaiduPcs.Core/Api/ErrorMessageCatalog.cs` |
| AppID / UA / 域名常量 | `BaiduPcs.Core/Api/ApiConstants.cs` |
| 分片、并发、超时、重试的具体实现 | §五 表中「代码位置」列 |
| 架构分层与依赖方向 | `README.md` §架构设计（摘要）+ `docs/DEVELOPMENT_LOG.md` §二 |
| 历史决策与踩坑 | `docs/DEVELOPMENT_LOG.md` |
| 当前待办与已知缺陷 | `README.md` §已知问题 + `docs/CODE_REVIEW_2026-11-25.md` |
