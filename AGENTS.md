# 仓库协作约定

## ACP Core 二进制边界（强制）

- `Goldfish.Acp` 的唯一源码位于 AgentFree；本仓库不得恢复或直接维护 `src/Goldfish.Acp`。
- `Goldfish.Harness.AcpHost` 只能引用 Orbit 的 `src/Orbit.Acp` 生成的 DLL，不得添加跨仓库或本仓库 ACP `ProjectReference`。
- 不保存 ACP 二进制快照；提交前必须运行 `scripts/verify-acp-binary.sh`，它会构建并校验 Orbit 的公共 DLL。
