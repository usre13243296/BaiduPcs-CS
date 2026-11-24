# 文档索引

本目录存放项目的设计文档与开发记录。面向使用者的说明请先读根目录的 [README](../README.md)。

## 文档清单

| 文档 | 内容 | 目标读者 |
|------|------|----------|
| [README](../README.md) | 项目介绍、功能特性、安装使用、架构概览、FAQ | 所有人 |
| [CHANGELOG](../CHANGELOG.md) | 各版本变更记录 | 使用者 / 贡献者 |
| [CONTRIBUTING](../CONTRIBUTING.md) | 环境准备、代码约定、测试要求、提交流程 | 贡献者 |
| [SECURITY](../SECURITY.md) | 敏感数据说明、漏洞报告渠道、已知风险 | 所有人 |
| [DEVELOPMENT_LOG](DEVELOPMENT_LOG.md) | 架构决策与踩坑记录：登录链路、上传 / 下载引擎、Avalonia 迁移、性能排查 | 维护者 / 深入贡献者 |
| [DOWNLOAD_ENGINE_DESIGN](DOWNLOAD_ENGINE_DESIGN.md) | 下载引擎的完整设计方案：分片、断点、重试、暂停恢复、测试计划 | 维护者 |

## 资源文件

| 文件 | 用途 | 说明 |
|------|------|------|
| [screenshot.png](screenshot.png) | README 主界面截图 | 1488 × 982，替换功能后建议同步更新 |
| [social-preview.png](social-preview.png) | GitHub 社交分享卡片 | 1280 × 640（1.91:1），需在仓库 Settings → Social preview 手动上传 |
| [../tools/make-social-preview.ps1](../tools/make-social-preview.ps1) | 预览图生成脚本 | 由 screenshot.png 合成，改文案后重跑即可 |

## 建议阅读顺序

1. **想用起来**：README → 快速开始 → 常见问题 FAQ。
2. **想改代码**：CONTRIBUTING → README 的「项目结构」→ DEVELOPMENT_LOG 对应章节。
3. **想动下载 / 上传引擎**：DOWNLOAD_ENGINE_DESIGN（方案与 Go 对照）→ DEVELOPMENT_LOG 三-bis / 三-ter（落地记录与踩坑）。
4. **想接其他前端（移动端 / Web）**：README「架构设计」→ `BaiduPcs.Core/Transfer/ITransferEngine.cs` → DEVELOPMENT_LOG 三-ter 第 7 条（引擎如何接入任务层）。

## 术语表

| 术语 | 含义 |
|------|------|
| BDUSS | 百度网盘登录 Cookie，等价于账号登录凭证 |
| locatedownload / 签名直链 | 通过签名接口获取的 CDN 直链，不依赖账号会员身份，可分享 |
| method=download | 官方下载通道，服务端按账号会员等级签发直链，支持会员加速 |
| 秒传 | 服务端已存在相同文件内容（MD5 命中）时跳过数据传输，直接创建文件 |
| `.part.json` | 下载断点元数据：总分片、块大小、各分片剩余范围 |
| RID | .NET 运行时标识符，如 `win-x64`、`linux-arm64` |
