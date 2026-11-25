# 贡献指南

感谢你愿意为 BaiduPcs-CS 出力。本文说明环境准备、代码约定和提交流程；没覆盖到的部分，按「与现有代码保持一致」处理即可。

## 开始之前

- 本项目是**非官方**百度网盘客户端，代码面向学习与个人使用，不接受用于绕过付费、批量薅带宽或规避风控的改动。
- 提交代码即表示同意以 [MIT 许可证](LICENSE) 授权你的贡献。
- 涉及账号凭证（BDUSS / Cookie / `accounts.json`）的内容**任何时候都不要**出现在 Issue、PR 或提交历史里。

## 环境要求

| 依赖 | 版本 | 说明 |
|------|------|------|
| .NET SDK | 10.0+ | 构建与测试的唯一必需项 |
| Git | 任意近版本 | 提交 PR |
| 操作系统 | Windows / Linux / macOS | 三平台均可开发；CI 在 Windows 与 Linux 上构建并跑测试（两者都是强制门槛） |

## 本地开发

```bash
git clone https://github.com/usre13243296/BaiduPcs-CS.git
cd BaiduPcs-CS

dotnet restore BaiduPcs.slnx          # 还原依赖
dotnet build BaiduPcs.slnx            # 构建全部项目
dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj   # 全量测试（基线见 docs/FACTS.md）
dotnet run --project BaiduPcsAPP/BaiduPcsAPP.csproj          # 启动桌面端

# 文档一致性检查（改了文档 / 版本号 / 测试后跑一次）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-docs.ps1   # Windows
pwsh -NoProfile -File tools/check-docs.ps1                                 # Linux / macOS
```

调试 GUI 时可以用 `BP_SMOKE_TEST=1` 环境变量启动：程序会在 3 秒后自动退出，用来验证初始化不崩溃。

## 代码约定

- **分层不可破**：`BaiduPcs.Core` 不得引用 Avalonia、不得出现任何 UI 类型；UI 交互一律通过 `IDialogService` 等抽象接口从 APP 层注入。
- **异步优先**：公开的 I/O 方法以 `Async` 结尾并接收 `CancellationToken`，禁止同步阻塞（`.Result` / `.Wait()`）。
- **可空引用类型**：已开启 `<Nullable>enable</Nullable>`，新增代码必须通过编译器的可空检查。
- **注释写「为什么」**：协议细节、百度接口的坑、魔数来源需要注释说明，便于后续维护者判断能否修改。
- **错误消息面向用户**：远程错误码请经 `ErrorMessageCatalog` 映射为可读中文，不要让 `errno=xxx` 直接暴露在界面上。

## 测试要求

- 新增或修改引擎 / 协议行为**必须补测试**，`dotnet test` 全绿才能提 PR。
- 测试**不得访问真实百度接口**：协议层用 mock `HttpMessageHandler`，引擎层用 `BaiduPcs.Core.Tests/Infrastructure/TestHttpServer.cs` 的本地服务器。
- 测试断言要覆盖「参数本身」而不只是「请求发生」——历史上 multipart 分片长度、URL 编码大小写都因为断言太浅而漏测。

## 提交信息

使用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)：

```
feat(upload): 支持目录递归上传
fix(download): 修正分片进度在暂停后回退
docs(readme): 补充 ARM 设备部署说明
test(download): 补 429 退避重试断言
refactor(core): 抽取 ITransferEngine
chore(ci): 升级 setup-dotnet
```

## 提交流程

1. Fork 仓库并从 `main` 切出特性分支：`git checkout -b feat/xxx`。
2. 完成改动，本地 `dotnet build` + `dotnet test` 全绿。
3. **同步文档**（详见 [`docs/MAINTENANCE.md`](docs/MAINTENANCE.md) §二）：
   - 数字（版本、测试用例数、并发 / 分片参数、数据目录）**只改 [`docs/FACTS.md`](docs/FACTS.md)**，不要在 README / 本文里复述；
   - 用户可见行为变化 → 更新 `README.md` 与 `CHANGELOG.md` 的 `[Unreleased]` 段；
   - 解决新坑或做出架构取舍 → 在 `docs/DEVELOPMENT_LOG.md` 追加一节（带日期）。
4. 跑一次文档检查：`tools/check-docs.ps1`（命令见上），全绿再提交。
5. 提交 PR，填写 PR 模板中的自查清单；说明「改了什么、为什么改、怎么验证」。
6. CI 通过后由维护者评审合并。

## 不要提交的文件

`.gitignore` 已覆盖以下内容，提交前请确认 `git status` 干净：

- 构建产物：`bin/`、`obj/`、`publish/`
- 断点续传临时文件：`*.part`、`*.part.json`
- 参考源码：`BaiduPCS-Go-main/`（仅本地对照，不随仓库发布）
- 日志与账号文件：`*.log`、`accounts.json`
- 未脱敏的截图：请先打码 UID、文件名、BDUSS

## 行为准则

讨论保持就事论事：报问题给出复现步骤与环境信息，评审意见针对代码而不是人。恶意刷 Issue、提交恶意代码或泄露他人凭证的贡献者会被直接拉黑。

## 交流

- Issue：Bug 反馈与功能建议
- QQ 群：**801154677**
