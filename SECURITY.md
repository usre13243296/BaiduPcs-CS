# 安全策略

## 支持范围

安全修复只针对**最新一个 Release**（当前为 v0.6.0）。更早的版本请先升级再报告问题。

## 报告安全问题

请**不要**在公开 Issue 中披露漏洞细节或任何凭证。使用 GitHub 的私密报告渠道：

1. 打开仓库 **Security** 标签页 → **Advisories** → **Report a vulnerability**；
2. 描述影响范围、复现步骤与建议修复方式；
3. 我会在 7 天内确认收到，并在修复发布后致谢（如你愿意署名）。

对于**不涉及安全**的普通 Bug，请走 [Issue 模板](https://github.com/usre13243296/BaiduPcs-CS/issues/new/choose)。

## 本项目会接触哪些敏感数据

| 数据 | 位置 | 说明 |
|------|------|------|
| `BDUSS` / `STOKEN` 等登录 Cookie | 本机 `accounts.json`（**明文 JSON**） | 等同账号登录凭证，拿到即可免密登录 |
| 账号 UID、昵称 | 同上 | 用于多账号区分 |
| 下载 / 上传临时数据 | 下载目录 `*.part`、`*.part.json` | 文件内容与分片进度 |
| 运行日志 `app.log` | 配置目录 | 异常堆栈、远端路径、错误码 |

配置目录位置（以 [docs/FACTS.md](docs/FACTS.md) §六 为准）：

- Windows：`%APPDATA%\BaiduPcsApp`
- Linux / macOS：`~/.config/BaiduPcsApp`

> macOS 同样落到 `~/.config`：客户端只调用 .NET 的 `SpecialFolder.ApplicationData`，没有平台分支（**待 Mac 实机确认**）。

**重要**：

- `accounts.json` 以明文保存 BDUSS，**不要**把它提交到仓库、上传网盘或发给他人；公共电脑上使用后请退出登录并删除该文件。
- 该文件**未做额外权限加固**（`AccountStore.Save` 只做原子写入，未设置 0600 或使用系统密钥库）。Linux / macOS 上它通常继承 umask 得到 `0644`，同一台机器上的其它本地用户可读；介意的话自行 `chmod 600 ~/.config/BaiduPcsApp/accounts.json`。加密存储（DPAPI / Keychain / libsecret）在路线图中。
- 提 Issue 时请先清理日志中的 BDUSS、Cookie、UID 与文件名。
- 本项目**没有**任何遥测、上报或中间服务器：所有请求直连 `*.baidu.com`，账号数据不出本机。

## 已知风险（非漏洞，但需要知情）

1. **第三方接口风险**：客户端调用百度网盘面向 Web / 官方客户端的私有接口，接口可能随时变更；账号存在被风控（限速、验证、封禁）的可能。
2. **未签名发布包**：Release 中的可执行文件没有代码签名证书，Windows SmartScreen 与 macOS Gatekeeper 会提示。请只从本仓库的 Releases 页面下载，并核对文件大小。
3. **依赖供应链**：`BaiduPcs.Core` 零第三方运行时依赖；`BaiduPcsAPP` 仅依赖 Avalonia 与 CommunityToolkit.Mvvm 官方包，版本固定在 `csproj` 中（版本清单见 [docs/FACTS.md](docs/FACTS.md) §二）。
4. ~~**TLS 证书校验默认关闭**~~ **已于 v0.6.1 修复**：`BaiduHttpClient.BypassSslValidation` 现在默认 `false`（开启证书校验），仅在调用方显式 opt-in 时才关闭——该开关只用于企业代理自签证书等特殊场景。若需关闭，请确认你知道这会把 BDUSS 暴露给链路上的任何人。历史版本（≤ v0.6.0）请勿在不可信网络下使用。

## 不在安全范围内

- 因账号被限速、被风控或服务条款纠纷产生的问题；
- 从非本仓库渠道下载的「修改版」程序；
- 用户主动泄露 `accounts.json`、日志或截图导致的账号风险。
