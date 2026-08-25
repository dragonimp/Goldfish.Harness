# 仓库协作约定

## 提交纪律（强制）

- 完成一组可验证的变更后必须立即创建 Git 提交，不得把已完成变更长期留在工作区。
- 交付或结束任务前必须检查 `git status`，确保除明确说明的本地敏感文件或运行期文件外工作树干净。
- 提交前检查变更内容，不得提交密钥、凭据、本地数据库、缓存或其他运行期数据。
- 每个提交应聚焦于同一目的，并在提交信息中准确概括已完成的变更。

## ACP Core 二进制边界（强制）

- `Goldfish.Acp` 的唯一源码位于 AgentFree；本仓库不得恢复或直接维护 `src/Goldfish.Acp`。
- `Goldfish.Harness.AcpHost` 只能引用 `lib/Goldfish.Acp/Goldfish.Acp.dll`，不得添加跨仓库或本仓库 ACP `ProjectReference`。
- `lib/Goldfish.Acp` 只能通过 AgentFree 的 `scripts/sync-acp-core-to-harness.sh --write` 更新；提交前必须运行 `scripts/verify-acp-binary.sh`。
