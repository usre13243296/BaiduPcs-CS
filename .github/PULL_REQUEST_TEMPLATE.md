## 变更说明

<!-- 这个 PR 做了什么、为什么这么做；涉及协议或引擎改动时说明依据 -->

## 关联 Issue

<!-- 例如：Closes #12 -->

## 变更类型

- [ ] 新功能（feat）
- [ ] 缺陷修复（fix）
- [ ] 重构（refactor）
- [ ] 测试（test）
- [ ] 文档（docs）
- [ ] 构建 / CI（chore）

## 自查清单

- [ ] `dotnet build BaiduPcs.slnx` 通过
- [ ] `dotnet test BaiduPcs.Core.Tests/BaiduPcs.Core.Tests.csproj` 全绿
- [ ] 新增或修改的协议 / 引擎行为已补充测试（不访问真实百度接口）
- [ ] `BaiduPcs.Core` 未引入 Avalonia 或任何 UI 依赖
- [ ] 涉及用户可见行为时，已同步更新 `README.md` 与 `CHANGELOG.md`
- [ ] 版本 / 测试数 / 并发 / 分片 / 数据目录等事实**只改了 `docs/FACTS.md`**，未在多份文档里复述
- [ ] `tools/check-docs.ps1` 通过（文档链接、版本号、用例数一致）
- [ ] 提交内容不含 BDUSS / Cookie / `accounts.json` / 未脱敏日志与截图
- [ ] 提交内容不含 `bin/`、`obj/`、`publish/` 等构建产物

## 验证方式

<!-- 怎么验证的：单测用例、手工操作步骤、在哪个平台验证 -->

## 截图（可选，涉及 UI 改动时）
