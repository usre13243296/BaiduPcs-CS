# 文档索引

> 本目录存放设计文档、维护手册与评审记录。**所有数字与参数的唯一来源是 [FACTS](FACTS.md)**；面向使用者请先读根目录 [README](../README.md)。
>
> 「改了代码该同步哪份文档」的规则见 [MAINTENANCE](MAINTENANCE.md) §二。

## 文档清单

| 文档 | 内容 | 目标读者 | 状态 |
|------|------|----------|------|
| [README](../README.md) | 项目介绍、功能、安装使用、架构概览、FAQ、已知问题 | 所有人 | **现行**（用户向） |
| [FACTS](FACTS.md) | 版本、技术栈、测试基线、传输参数、数据目录 | 所有人 | **现行 · 唯一事实源**（改数字只改这里） |
| [MAINTENANCE](MAINTENANCE.md) | 文档地图、同步规则、命令速查、发布流程、坑索引 | 维护者 | **现行** |
| [CHANGELOG](../CHANGELOG.md) | 各版本变更记录 | 使用者 / 贡献者 | **现行** |
| [CONTRIBUTING](../CONTRIBUTING.md) | 环境准备、代码约定、测试要求、提交流程 | 贡献者 | **现行** |
| [SECURITY](../SECURITY.md) | 敏感数据说明、漏洞报告渠道、已知风险 | 所有人 | **现行** |
| [DEVELOPMENT_LOG](DEVELOPMENT_LOG.md) | 架构决策与踩坑记录（按时间线） | 维护者 / 深入贡献者 | **历史归档**：记录当时，不代表现状 |
| [DOWNLOAD_ENGINE_DESIGN](DOWNLOAD_ENGINE_DESIGN.md) | 下载引擎的完整设计方案（已落地） | 维护者 | **历史设计**：改造当时快照 |
| [CODE_REVIEW_2026-11-25](CODE_REVIEW_2026-11-25.md) | 全量代码与文档评审：P0/P1/P2 + 分阶段改善方案 | 维护者 | **时间点快照**（只读，不回改） |
| [llms.txt](../llms.txt) | 面向 LLM 的项目摘要 | 机器 / LLM | **现行**（自包含摘要，数字指向 FACTS） |

## 维护约定（三条）

1. **数字只写一处**：版本号、测试用例数、并发 / 分片参数、数据目录等，只在 [FACTS](FACTS.md) 里写；其它文档用「见 FACTS」代替复述。
2. **历史文档只加横幅，不改写**：作废的小节打 `⚠️ 已作废` 标记并指向现状，保留踩坑过程。
3. **提交前跑检查**：

   ```powershell
   # Windows PowerShell
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-docs.ps1
   # PowerShell 7 / Linux / macOS / CI
   pwsh -NoProfile -File tools/check-docs.ps1
   ```

   它校验：版本号三处一致、`[Fact]+[InlineData]` 数量与 FACTS 一致、全仓相对链接有效、README/CONTRIBUTING/llms.txt 未写死用例数。CI 已接入。

## 建议阅读顺序

1. **想用起来**：[README](../README.md) → 快速开始 → 常见问题 FAQ → 已知问题。
2. **想改代码**：[CONTRIBUTING](../CONTRIBUTING.md) → [MAINTENANCE](MAINTENANCE.md) §二（改什么代码同步什么文档）→ [FACTS](FACTS.md)。
3. **想动下载 / 上传引擎**：[DOWNLOAD_ENGINE_DESIGN](DOWNLOAD_ENGINE_DESIGN.md)（方案与 Go 对照）→ [DEVELOPMENT_LOG](DEVELOPMENT_LOG.md) 三-bis / 三-ter（落地记录与踩坑）→ [FACTS](FACTS.md) §五（参数与代码位置）。
4. **想知道现在哪里有问题、下一步做什么**：[CODE_REVIEW_2026-11-25](CODE_REVIEW_2026-11-25.md) → [README](../README.md) 路线图。
5. **想接其他前端（移动端 / Web）**：[README](../README.md)「架构设计」→ `BaiduPcs.Core/Transfer/ITransferEngine.cs` → [DEVELOPMENT_LOG](DEVELOPMENT_LOG.md) 三-ter 第 7 条。

## 资源文件

| 文件 | 用途 | 说明 |
|------|------|------|
| [screenshot.png](screenshot.png) | README 主界面截图 | 1488 × 982，替换功能后建议同步更新 |
| [social-preview.png](social-preview.png) | GitHub 社交分享卡片 | 1280 × 640（1.91:1），需在仓库 Settings → Social preview 手动上传 |
| [../tools/make-social-preview.ps1](../tools/make-social-preview.ps1) | 预览图生成脚本 | 由 screenshot.png 合成，改文案后重跑即可 |
| [../tools/check-docs.ps1](../tools/check-docs.ps1) | 文档一致性检查 | CI 与本地共用，见上「维护约定」 |
| [../tools/backup-version.ps1](../tools/backup-version.ps1) | 小版本备份 | 源码快照 + git 历史包 + 清单，写到仓库外的 `version-backups\`，见 [MAINTENANCE](MAINTENANCE.md) §四 |

## 术语表

| 术语 | 含义 |
|------|------|
| BDUSS | 百度网盘登录 Cookie，等价于账号登录凭证 |
| locatedownload / 签名直链 | 通过签名接口获取的 CDN 直链，不依赖账号会员身份，可分享 |
| method=download | 官方下载通道，服务端按账号会员等级签发直链，支持会员加速 |
| 秒传 | 服务端已存在相同文件内容（MD5 命中）时跳过数据传输，直接创建文件 |
| `.part.json` | 下载断点元数据：总分片、块大小、各分片剩余范围 |
| RID | .NET 运行时标识符，如 `win-x64`、`linux-arm64` |
