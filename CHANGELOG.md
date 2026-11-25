# 更新日志

本项目的所有重要变更都记录在此文件。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 修复

- **断点恢复后文件损坏**：worker 在写盘之前就推进分片进度指针（`_rangeBegin`），而断点快照读的正是该值；取消或暂停恰好落在「已宣告、未写盘」的窗口时（8 个 worker 共用一把写锁，排队窗口并不小），断点会把未落盘的区间记为已完成，恢复时直接跳过 → `.part` 出现空洞。因文件长度仍等于总大小，长度校验查不出来，只表现为内容损坏。现在改为「先按偏移写盘、成功后再推进进度与字节计数」，并让「从生成器取片 + 分配到 worker」与断点快照互斥，堵住「已分配、未落 worker」的更窄空隙。

### 文档

- 新增 [`docs/FACTS.md`](docs/FACTS.md) 作为**唯一事实源**（版本、技术栈、测试基线、传输参数、数据目录），README / CONTRIBUTING / llms.txt 不再复述这些数字。
- 新增 [`docs/MAINTENANCE.md`](docs/MAINTENANCE.md)：文档地图、「改代码 → 同步哪份文档」对照表、命令速查、发布流程、坑索引。
- 新增 [`docs/CODE_REVIEW_2026-11-25.md`](docs/CODE_REVIEW_2026-11-25.md)：全量代码与文档评审（P0/P1/P2）及分阶段改善方案。
- 修正文档与实现不一致：
  - 取消下载会丢弃 `.part` / `.part.json`（原文称「取消后可续传」），暂停 / 异常退出才保留断点；
  - macOS 配置目录为 `~/.config/BaiduPcsApp`（原文为 `~/Library/Application Support`，代码无平台分支；待实机确认）；
  - 下载通道为「locatedownload 签名直链为主 + 官方 `method=download` 自动降级」（`DEVELOPMENT_LOG` §3.2 已标注作废）；
  - 断点续传为 `.part.json` 分片级（`DEVELOPMENT_LOG` §3.3 已标注作废）；
  - 限流退避为 1/2/4 秒封顶（原文称「指数退避」）；
  - 异常日志不再声称「不静默崩溃」；
  - 多账号：切换需重新登录（原文称「退出登录可切换账号」）。
- `README.md` 新增「已知问题」小节（7 条），并标注桌面层无自动化测试；路线图补 v0.6.1 稳定性修复行。
- `SECURITY.md` 补充 TLS 证书校验默认关闭（v0.6.1 计划改为默认开启）与 `accounts.json` 文件权限说明。
- `DEVELOPMENT_LOG.md` 增加「历史时间线」定位声明；§五 Roadmap 标注已过期并指向 README；修正重复的踩坑编号。
- `DOWNLOAD_ENGINE_DESIGN.md` 标题去掉「v0.8 前瞻」，补充「文中行号为改造当时」的时效提示。

### 构建 / CI

- 新增 [`tools/check-docs.ps1`](tools/check-docs.ps1)：校验 ① `FACTS.md` / `README.md` / `llms.txt` 的版本号与 `csproj` 一致；② `FACTS.md` 的用例总数与 `[Fact]+[InlineData]` 实数一致；③ 全仓 markdown 相对链接有效；④ README / CONTRIBUTING / llms.txt 未写死用例数。兼容 PowerShell 5.1 与 7。
- 新增 [`tools/backup-version.ps1`](tools/backup-version.ps1)：每个小版本在仓库外（`version-backups/`）留一份独立快照（源码 + `repo.bundle` + `BACKUP.md` 清单），约定见 [docs/MAINTENANCE.md](docs/MAINTENANCE.md) §四「版本备份」。
- `build.yml`：启用 NuGet 缓存；增加 Linux 观察性测试腿（`continue-on-error`，稳定后转强制）；增加文档一致性检查步骤。
- `PULL_REQUEST_TEMPLATE.md`：自查清单增加「事实只改 FACTS」与「check-docs 通过」两项。

### 测试

- 修复 `Download_PauseResume_Completes` 的时序竞态：本地测试服务器过快时，下载可能在暂停生效前就完成（断点随之被删除），导致 CI 偶发失败。现在测试内给响应加延迟并改成轮询等待断点落地。

## [0.6.0] - 2026-08-08

首个「下载 + 上传」双链路可用的版本。

### 新增

- **上传引擎**：秒传预检（`precreate` 二合一，命中 `return_type=2` 直接完成）→ 分片并发上传（`superfile2`，4 并发，4MB / 16MB / 64MB 档位）→ 合并（`create`）。
- **三份校验和单趟计算**：全文件 MD5、前 256KB MD5、逐块 MD5 一次读文件同时累计。
- **重名自动递增**：目标目录已存在同名文件时自动改名 `报告.txt` → `报告2.txt` → `报告3.txt`，不覆盖、不询问。
- **上传断点续传（进程内）**：复用 `uploadid` 跳过已传分片；`31363` 会话失效时自动重新预创建并整体重传。
- **传输任务管理**：队列调度（同时 2 个）、暂停 / 恢复 / 取消 / 删除、同路径去重、失败重试、错误详情展示。
- **Linux ARM64 发布包**：可部署在 NAS、软路由、树莓派等 ARM 设备。
- **多文件上传**：一次选择一个或多个本地文件，独立入队。

### 修复

- 上传进度条不走、速度显示虚高（`xxxTB/s`）：补上 500ms 监控心跳，`SpeedMeter` 改用平台无关计时基准，本地瞬间完成的字节不计入网络速度。
- checksum 阶段进度提前打满 100%：预计算阶段不再计入上传进度，进度从分片上传开始推进。
- 秒传命中与真实上传无法区分：`return_type=2` 时进度瞬间读满并标注「秒传命中」。
- multipart 误发文件剩余部分：分片上传改用限长子流，只发送 `[offset, offset+length)` 区间。

### 测试

- 全量 **164 个用例**全部通过（协议层 + 引擎层新增 44 个）。

## [0.5.9] - 2026-08-07

下载功能彻底可用的版本。

### 新增

- **多线程分片下载**：统一 8 并发，`WriteAt` 绝对偏移写入 `.part`，无合并步骤。
- **双下载通道**：locatedownload 签名直链为主（多备选链接逐个尝试），失败自动降级官方 `method=download`。
- **分片级断点续传**：`.part.json` 记录各分片剩余范围，监控循环每秒持久化，中断 / 取消后可恢复。
- **错误分类与重试**：403/404/416 判死换直链，429/509 限流指数退避，断流 30 秒读超时重连，任务级重试 3 次。
- **传输任务面板**：暂停 / 继续 / 取消 / 删除、进度与速度实时刷新、错误详情可见。
- **「复制链接」**：独立于下载，输出 locatedownload 签名直链到剪贴板。

### 修复

- 大文件下载被 50 秒总超时截断（改用独立无限超时 HttpClient，由 `CancellationToken` 负责取消）。
- 百度返回非标准 `charset=utf8` 导致 UI 线程崩溃（按字节读取 + 显式 UTF-8 解码，并注册 `CodePagesEncodingProvider`）。
- 下载速度统计虚高 / 小文件速度为空。

## [0.5.0] - 2026-08-07

第一个可用版本。

### 新增

- **BDUSS 登录**：粘贴 BDUSS 登录，高级模式可附加完整 Cookie 串；先访问网盘首页完成 Cookie 初始化再验证。
- **Cookie 收集与持久化**：运行期吸收的服务器 Cookie 合并回账号并落盘。
- **文件浏览**：目录列表、面包屑、进入 / 返回 / 刷新、新建文件夹、重命名、删除、空间配额。
- **单通道下载**：官方 `method=download` 通道，支持进度与速度显示。
- **多账号**：账号本地持久化，重启自动恢复登录态。
- **浅色 UI**：淡灰蓝配色。
- **全局异常日志兜底**。

### 测试

- 59 个单元测试，含密码学算法对齐 Go 测试向量。

[Unreleased]: https://github.com/usre13243296/BaiduPcs-CS/compare/V0.6...main
[0.6.0]: https://github.com/usre13243296/BaiduPcs-CS/compare/V0.5.9...V0.6
[0.5.9]: https://github.com/usre13243296/BaiduPcs-CS/compare/v0.5.0...V0.5.9
[0.5.0]: https://github.com/usre13243296/BaiduPcs-CS/releases/tag/v0.5.0
