# 维护手册

> 面向**维护者与贡献者**。目标：改代码时 30 秒内知道「该同步哪份文档」，以及怎么验证、怎么发版。
>
> 使用者请直接看 [README](../README.md)；所有事实与数字见 [FACTS](FACTS.md)。

---

## 一、文档地图（谁面向谁、状态是什么）

| 文档 | 读者 | 状态 | 什么时候必须改 |
|------|------|------|----------------|
| [README](../README.md) | 使用者 / 新访客 | **现行**（用户向） | 用户可见功能、安装方式、FAQ、路线图变化时 |
| [FACTS](FACTS.md) | 所有人 | **现行**（**唯一事实源**） | 任何版本号 / 参数 / 平台 / 数据目录 / 测试数量变化时——**只改这里** |
| [MAINTENANCE](MAINTENANCE.md)（本文） | 维护者 | **现行** | 流程、命令、发布步骤变化时 |
| [CONTRIBUTING](../CONTRIBUTING.md) | 贡献者 | **现行** | 环境要求、代码约定、提交流程变化时 |
| [CHANGELOG](../CHANGELOG.md) | 使用者 / 贡献者 | **现行** | 每个用户可见变更都要进 `[Unreleased]` |
| [SECURITY](../SECURITY.md) | 所有人 | **现行** | 敏感数据位置、安全默认值、依赖构成变化时 |
| [DEVELOPMENT_LOG](DEVELOPMENT_LOG.md) | 维护者 | **历史时间线**（不代表现状） | 解决新坑、做出新决策后追加一节 |
| [DOWNLOAD_ENGINE_DESIGN](DOWNLOAD_ENGINE_DESIGN.md) | 维护者 | **历史设计**（已落地；仅重大改版时加新章节） | 下载引擎再重构时 |
| [CODE_REVIEW_2026-11-25](CODE_REVIEW_2026-11-25.md) | 维护者 | **时间点快照**（不重写，只在顶部加进度横幅） | 下次评审另开 `CODE_REVIEW_<日期>.md` |
| [llms.txt](../llms.txt) | 机器 / LLM | **现行**（自包含摘要，数字指向 FACTS） | 项目定位、能力清单、文档列表变化时 |

**两条铁律**

1. **数字只在 FACTS 里写一遍。** README、CONTRIBUTING、llms.txt 里出现「164 个用例」「16 秒」「并发 8」这类值都是历史包袱——新写文档请写成「见 [FACTS](FACTS.md)」。
2. **历史文档不改写，只加横幅。** 已作废的小节打 `⚠️ 已作废` 标记并指向现状，不要直接删（踩坑过程本身有价值）。

---

## 二、改什么代码 → 同步什么文档

| 你改了什么 | 必须同步 | 顺带检查 |
|------------|----------|----------|
| 版本号（`<Version>`） | `FACTS.md` §一、`CHANGELOG.md` | `README.md` 的「当前版本」行、`llms.txt` |
| 并发数 / 分片档位 / 超时 / 重试 / 限流 | `FACTS.md` §五 | `README.md` 功能清单里若提到具体数字，改成指向 FACTS |
| 新增或删除用户可见功能 | `README.md` §功能特性、`CHANGELOG.md` `[Unreleased]` | `llms.txt` §核心能力、路线图状态 |
| 数据目录 / 凭据处理 / 安全默认值 | `FACTS.md` §六、`SECURITY.md` | `README.md` §数据存放位置 |
| 依赖升级（Avalonia / MVVM / xUnit…） | `FACTS.md` §二 | `SECURITY.md` §依赖供应链 |
| 发布 RID / 打包参数 | `FACTS.md` §四 | 本文 §三、`.github/workflows/` |
| 测试数量或新增测试项目 | `FACTS.md` §三（脚本会自动核对） | — |
| 协议层/引擎的踩坑与决策 | `DEVELOPMENT_LOG.md`（追加一节，带日期） | 若推翻旧结论，给旧小节打 `⚠️ 已作废` |
| CI、发布流程、工具脚本 | 本文 §三 / §四 | `CONTRIBUTING.md` §本地开发 |

> 逻辑起点：**改完之后问自己一句「哪个数字现在不成立了？」** 只去 `FACTS.md` 找它。

---

## 三、命令速查

```powershell
# 构建（Release 基线：0 警告 0 错误）
dotnet build BaiduPcs.slnx -c Release

# 测试（基线见 docs/FACTS.md）
dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj

# 桌面端运行
dotnet run --project BaiduPcsAPP/BaiduPcsAPP.csproj

# 启动冒烟：3 秒后自动退出，验证初始化不崩溃（Windows 需 GUI 会话）
$env:BP_SMOKE_TEST='1'; dotnet run --project BaiduPcsAPP/BaiduPcsAPP.csproj

# 文档一致性检查（CI 会跑；改了文档 / 版本 / 测试后本地也跑一次）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-docs.ps1   # Windows PowerShell 5.1
pwsh -NoProfile -File tools/check-docs.ps1                                 # PowerShell 7 / Linux / macOS

# 打小版本备份（源码快照 + git 历史包 + 清单），见 §四「版本备份」
powershell -NoProfile -ExecutionPolicy Bypass -File tools\backup-version.ps1 -Label v0.6.1 -Note "阶段 0 修复"

# 单平台发布
dotnet publish BaiduPcsAPP/BaiduPcsAPP.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

6 个 RID 清单见 [FACTS §四](FACTS.md)。

---

## 四、发布流程

1. 确认 `main` 上 `dotnet build` / `dotnet test` / `pwsh tools/check-docs.ps1` 全绿；
2. 改 `BaiduPcsAPP/BaiduPcsAPP.csproj` 的 `<Version>`（语义化版本）；
3. `CHANGELOG.md`：把 `[Unreleased]` 的内容提为 `## [x.y.z] - YYYY-MM-DD`，并补上比较链接；
4. `docs/FACTS.md` §一「当前版本」同步（脚本会校验这两处一致）；
5. `README.md` 的「当前版本」行与 `llms.txt` 同步；
6. 提交 `chore(release): vX.Y.Z`，打 tag（**推荐小写 `v`**）并推送：

   ```bash
   git tag v0.6.1
   git push origin v0.6.1
   ```

   推送 tag 会触发两条流水线：`release`（按 6 个 RID 出包，产物为 `BaiduPcs-vX.Y.Z-<rid>.zip` + 同名 `.sha256`）与 `mirror-to-gitee`（同步镜像仓库）；
7. 到 Actions 页面下载这 6 个 artifact，至少验证 Windows x64 包能启动后，再新建 GitHub Release 并把 zip 与 sha256 一起挂上；Release notes 从 CHANGELOG 对应段落复制；
8. 首次跑 `release` 时留意矩阵日志：跨平台交叉发布（`win-arm64`、`osx-x64` 等）若某 RID 报错，可在对应平台单独 `dotnet publish` 兜底。

> 现有状态：`release.yml` 自动出包，但**不自动创建 Release**——对外发布保留人工确认环节。

### 版本备份（每个小版本）

**约定**：主版本目录 `BaiduPcs\` 始终留在工作区根目录；每完成一个小版本（v0.6.0、v0.6.1 …），在工作区根目录的 **`version-backups\`** 下打一份独立快照。备份在仓库**之外**，因此不进 git、不重复占仓库体积。

```powershell
# 在仓库根目录执行
powershell -NoProfile -ExecutionPolicy Bypass -File tools\backup-version.ps1 `
  -Label v0.6.1 -Note "阶段 0：界面可用性 + 安全默认值 + 覆盖保护"
```

产出目录 `<工作区>/version-backups/v0.6.1_2026-11-25_1830/`，包含三样东西：

| 内容 | 作用 |
|------|------|
| 源码快照（含 `.github`、`docs`、`tools`、`*.csproj`） | 直接翻阅或整目录还原；排除 `bin/ obj/ publish/ .git/ BaiduPCS-Go-main/` 等 |
| `repo.bundle` | git **全量历史**，可用 `git clone repo.bundle restored` 还原成带历史的仓库 |
| `BACKUP.md` | 清单：csproj 版本、git 提交、工作区是否干净、文件数体积、备注、恢复与验证步骤 |

**时机**：一个阶段的改动**验证通过后**立刻打（`dotnet build` + `dotnet test` + `check-docs` 全绿）；不确定的中间状态不必备份。
**为什么还要 git**：两者互补——`git` 管提交粒度的回溯，`version-backups` 管「这个版本当时跑得起来的完整样子」，即使仓库目录被误删也能恢复。

---

## 五、文档防漂移机制

| 机制 | 说明 |
|------|------|
| `tools/check-docs.ps1` | ① `FACTS.md` 的「当前版本」== `csproj` 的 `<Version>`（并核对 README / llms.txt 的「当前版本」行）；② 全仓 markdown 相对链接目标存在；③ `FACTS.md` 的「测试用例总数」== 代码里 `[Fact]` + `[InlineData]` 数量。任一不符即 exit 1 |
| CI | `.github/workflows/build.yml` 已加入「文档一致性检查」步骤 |
| PR 模板 | 自查清单包含「事实/参数变化是否只改了 FACTS」与「check-docs 是否通过」 |
| 复核节奏 | 每次发版前跑一遍；每季度人工过一遍 `README.md` 与 `FACTS.md` |

**脚本抓不到、只能靠人**的三类：语义正确性（文案说反了）、功能是否真的实现、时效性数字（性能实测、耗时）。

---

## 六、坑索引（找历史原因时从这里跳）

| 你要查的事 | 去哪 |
|------------|------|
| 为什么登录要先访问首页再 `getinfo` | `DEVELOPMENT_LOG.md` §3.1 |
| 为什么 UA 必须 `TryAddWithoutValidation` | `DEVELOPMENT_LOG.md` §3.1、§3.7 前后 |
| 为什么下载用独立无限超时 `HttpClient` | `DEVELOPMENT_LOG.md` §3.4 |
| `charset=utf8` 崩溃怎么修的 | `DEVELOPMENT_LOG.md` §3.5 |
| 为什么两个下载通道「主 + 降级」（与早期说法不同） | `DEVELOPMENT_LOG.md` §三-bis（§3.2 已作废） |
| 断点续传为什么从「字节续传」改成「分片级」 | `DEVELOPMENT_LOG.md` §三-bis（§3.3 已作废） |
| `Task.WhenAny` 不传播取消的坑 | `DEVELOPMENT_LOG.md` §三-bis 踩坑 1 |
| multipart 误发整文件的坑 | `DEVELOPMENT_LOG.md` §三-ter 踩坑 6 |
| 上传进度/速度虚高的排查过程 | `DEVELOPMENT_LOG.md` §三-ter 踩坑 7 |
| 下载引擎的完整方案与 Go 对照 | `DOWNLOAD_ENGINE_DESIGN.md` |
| 当前已知缺陷与优先级 | `CODE_REVIEW_2026-11-25.md`、`README.md` §已知问题 |

---

## 七、已知维护债务（写在这里，避免下次重新发现）

| # | 债务 | 影响 |
|---|------|------|
| 1 | 桌面层（`BaiduPcsAPP`）零测试 | 界面缺陷无法被 CI 发现，见 CODE_REVIEW T8 |
| 2 | ~~CI 测试门槛只在 Windows 强制~~ **已解决（2026-11-25）**：Linux 腿首轮观察全绿后已升级为强制门槛 | 双平台测试成为硬门槛，平台差异类缺陷（如 `SpeedMeter` tick）现在会被 CI 拦住 |
| 3 | macOS 配置目录为推断值 | 见 FACTS §六「待实机确认」 |
| 4 | 发布流程手工 | 6 个 RID 靠人肉打包，易漏 |
| 5 | `DEVELOPMENT_LOG.md` 内仍有若干「当时」表述 | 已加定位声明与作废横幅，阅读时以 FACTS 为准 |
