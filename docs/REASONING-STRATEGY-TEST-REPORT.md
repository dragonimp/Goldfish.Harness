# Reasoning Strategy 改造测试案例报告

## 测试时间

2026-07-22

## 本次实施范围

本次同步开启方案实施，完成 P0 级别的最小可验证改造：

- 新增 `ReasoningOptions`；
- 新增 `ReasoningStrategyKind`；
- 新增 `ReasoningStrategySelector`，Auto fallback 使用结构特征打分，不依赖业务关键字；
- 新增 `ReasoningStrategySelection`；
- 新增 `IReasoningStrategyDecider` / `DefaultReasoningStrategyDecider`，策略选择以 Harness 内部 preflight 组件实现，不暴露为模型可调用业务 tool；
- `GoldfishHarnessRequest` 支持传入 `ReasoningOptions`；
- `GoldfishHarnessRunner.BuildMessages` 将当前推理策略合并到第一条 `system` message；
- 非流式和流式 runner 均输出 `ReasoningStrategySelected` 事件；
- Auto 模式运行时优先识别用户本轮明确策略控制指令；
- Auto 模式在无用户明确策略控制指令时，每轮都调用模型分类器输出结构化策略 JSON，低置信度或异常时再降级到结构特征 fallback；
- session 中保存的上一轮选择结果只用于状态展示，不参与 Auto 决策；
- 扩展 `GoldfishEventKind`，并落地 Plan/ReWOO/Reflexion 过程事件。

当前阶段不改变已有 ReAct 工具循环语义，默认行为仍保持 ReAct-compatible。

## 新增测试案例

### TC-RS-001：Auto 模式结构特征 fallback 识别复杂任务

目标：

- 验证 `Strategy=Auto` 的本地 fallback 不依赖业务关键字，而是根据行数、列表、路径、代码/JSON 等结构特征选择 `PlanAndExecute`。

断言：

- `Requested == Auto`
- `Effective == PlanAndExecute`
- `ReflexionEnabled == true`
- `Reason` 以 `auto-structural:` 开头

覆盖：

- `ReasoningStrategySelector.Select`
- 结构特征 fallback 选择

### TC-RS-002：显式 ReWOO 覆盖 Auto 规则

目标：

- 验证调用方显式指定 `Strategy=ReWOO` 时，不再受 Auto 规则影响。
- 验证 `EnableReflexion=false` 能保留到 selection。

断言：

- `Requested == ReWOO`
- `Effective == ReWOO`
- `ReflexionEnabled == false`
- `Reason == request-explicit`

覆盖：

- 显式策略优先级
- Reflexion 开关传递

### TC-RS-003：推理策略注入唯一开头 system

目标：

- 验证 `BuildMessages` 会把推理策略上下文合并到第一条 `system` message。
- 验证没有新增中途 system message。

断言：

- 全部 messages 中只有一个 `ChatRole.System`
- system message 是 `messages[0]`
- system 文本包含 `## 推理策略`
- system 文本包含 `Effective: ReWOO`
- system 文本包含 `Reflexion: 关闭`

覆盖：

- `GoldfishHarnessRunner.BuildMessages`
- 单一 leading system 约束
- Reasoning prompt section 注入

### TC-RS-004：策略选择事件携带有效策略

目标：

- 验证 `GoldfishHarnessEvent.ReasoningStrategySelected` 可以表达当前策略选择结果，包括 classifier 来源和置信度。

断言：

- `Kind == ReasoningStrategySelected`
- `Result == PlanAndExecute`
- `Success == true`
- `Delta` 包含 `auto-classifier:0.91`

覆盖：

- `GoldfishEventKind.ReasoningStrategySelected`
- `GoldfishHarnessEvent.ReasoningStrategySelected`

### TC-RS-005：Auto 模式通过 classifier 选择 Plan-and-Execute

目标：

- 验证 `Strategy=Auto` 运行时先调用推理策略分类器。
- 验证 classifier 返回高置信度 `PlanAndExecute` 后，会继续创建计划并把计划注入执行上下文。

断言：

- 模型调用顺序为：classifier call -> planning call -> execution call
- classifier call 的 system message 包含 `推理策略分类器`
- planning call 的最后一条消息包含计划生成提示
- execution call 的 leading system 包含 `## 当前执行计划`
- 事件流包含 `ReasoningStrategySelected` 且 `Result == PlanAndExecute`
- 事件流包含 `PlanCreated`

覆盖：

- `GoldfishHarnessRunner.SelectReasoningStrategyAsync`
- `ReasoningStrategySelector.SelectFromClassifier`
- Auto classifier 到 Plan-and-Execute 的运行时链路

### TC-RS-006：Auto 模式即使存在 session 旧选择也会调用 classifier

目标：

- 验证当前 session 已有 Auto 旧选择时，仍会调用 classifier 让大模型给出本轮决策。

断言：

- `Effective` 采用本轮 classifier 输出
- classifier 模型调用次数为 1
- `Reason` 以 `auto-classifier:` 开头

覆盖：

- `DefaultReasoningStrategyDecider`
- Auto 每轮 classifier 决策

### TC-RS-007：Auto 模式每轮重新评估兼容旧配置

目标：

- 验证即使存在 session 旧选择，也会重新调用 classifier。

断言：

- classifier 模型调用次数为 1
- `Effective` 采用本轮 classifier 输出
- `Reason` 以 `auto-classifier:` 开头

覆盖：

- Auto 重评估路径

### TC-RS-008：Plan-and-Execute 创建计划并注入执行上下文

目标：

- 验证显式 `Strategy=PlanAndExecute` 时，Runner 会先调用模型创建执行计划。
- 验证计划会追加到后续执行模型调用的第一条 `system` message。
- 验证事件流包含 `PlanCreated` / `PlanStepStarted` / `PlanStepCompleted`。

断言：

- 模型调用顺序为：planning call -> execution call
- planning call 的最后一条消息包含计划生成提示
- execution call 的 leading system 包含 `## 当前执行计划`
- `PlanCreated.Delta` 等于计划 summary
- 至少一个 plan step started/completed 事件

覆盖：

- `ReasoningPlanParser`
- `GoldfishHarnessRunner.CreatePlanIfNeededAsync`
- `GoldfishHarnessEvent.PlanCreated`
- `GoldfishHarnessEvent.PlanStepStarted`
- `GoldfishHarnessEvent.PlanStepCompleted`

### TC-RS-009：Planner 非结构化输出 fallback

目标：

- 验证规划模型没有输出合法 JSON 时，不中断执行链路。
- 验证 fallback 为单步计划，描述沿用用户请求。

断言：

- `ReasoningPlan.Steps` 只有 1 条
- `Title == 完成请求`
- `Description == 用户请求`

覆盖：

- `ReasoningPlanParser.ParseOrFallback`
- planner 输出容错

### TC-RS-010：ReWOO 创建工具图、执行工具节点并回灌观察

目标：

- 验证显式 `Strategy=ReWOO` 时，Runner 会先调用模型创建 ReWOO 工具图。
- 验证 Harness 会按工具图节点逐个执行工具，且仍复用工具授权/审计路径。
- 验证工具观察会追加到后续执行上下文。

断言：

- 事件流包含 `ReWooGraphCreated`
- 工具节点被真实执行
- 事件流包含 step=0 的 `ToolResult`
- 后续模型调用的 leading system 包含 `## 当前 ReWOO 工具图`
- 后续模型调用的最后一条消息包含 `[ReWOO 工具图观察]`

覆盖：

- `GoldfishHarnessRunner.CreateReWooGraphIfNeededAsync`
- `GoldfishHarnessRunner.ExecuteReWooGraphAsync`
- `GoldfishHarnessEvent.ReWooGraphCreated`
- ReWOO 工具观察回灌

### TC-RS-011：Reflexion 对违反约束的最终答案进行修正

目标：

- 验证开启 `EnableReflexion=true` 且用户请求包含明确约束时，Runner 会在最终返回前调用 Reflexion 校验器。
- 验证校验器返回 `action=revise` 时，最终答案被替换为修正版。

断言：

- 最终 `Answer` 等于 Reflexion 修正版
- 事件流包含 `ReflectionCompleted`
- `ReflectionCompleted.Delta` 包含“Reflexion 已修正最终答案”
- 第二次模型调用包含 `Reflexion 校验器`

覆盖：

- `GoldfishHarnessRunner.ReflectFinalAnswerIfNeededAsync`
- `ReasoningReflectionParser.ParseOrKeep`
- `GoldfishHarnessEvent.ReflectionCompleted`

### TC-RS-012：Auto 模式遵从用户本轮自然语言指定的 Plan 策略

目标：

- 验证在 `Strategy=Auto` 时，如果用户明确说“我希望你走plan模式”，系统应选择 `PlanAndExecute`。
- 验证该逻辑属于用户控制指令，不是普通任务复杂度关键词判断。

断言：

- `Requested == Auto`
- `Effective == PlanAndExecute`
- `Reason == auto-user-directed:PlanAndExecute`

覆盖：

- `ReasoningStrategySelector.TrySelectUserDirectedStrategy`
- Auto 用户控制指令优先级

### TC-RS-013：Auto 模式不会把策略对比类问题误判为策略切换

目标：

- 验证“解释 react 和 plan 模式有什么区别”这类问题不会被识别为用户指定策略。

断言：

- `TrySelectUserDirectedStrategy(...) == null`

覆盖：

- 用户控制指令识别的误判保护

### TC-RS-014：用户本轮明确指定策略时跳过 classifier

目标：

- 验证当前会话存在 Auto 旧选择时，用户本轮明确要求 `plan` 仍应直接生效。
- 验证该路径不调用 classifier。

断言：

- `Effective == PlanAndExecute`
- `Reason == auto-user-directed:PlanAndExecute`
- classifier 模型调用次数为 0

覆盖：

- `DefaultReasoningStrategyDecider`
- Auto 用户控制指令优先于 classifier

### TC-RS-015：Auto classifier prompt 明确告知用户指定策略优先

目标：

- 验证发送给大模型分类器的 system prompt 明确说明：用户本轮要求走某个策略时，应遵从用户要求。

断言：

- classifier call 的 system message 包含“如果用户本轮明确要求”
- classifier call 的 system message 包含“我希望你走 plan 模式”

覆盖：

- Auto classifier prompt 约束

## 回归测试范围

本次完整运行现有测试套件，覆盖：

- Plan-and-Execute 计划创建、计划注入、计划步骤事件；
- prompt/memory 合并到单一 leading system；
- context compression 预算裁剪；
- skill session 隔离；
- tool authorization deny；
- SQLite harness state store；
- SQLite memory manager；
- in-memory memory manager；
- session queue。

## 测试命令

```bash
dotnet test Goldfish.Harness.slnx -c Release
```

## 测试结果

```text
已通过! - 失败: 0，通过: 48，已跳过: 0，总计: 48
```

## 结论

P0 改造已完成，P1 Plan-and-Execute 第一版已落地并通过测试。

当前代码已经具备：

- 推理策略配置入口；
- Auto / ReAct / PlanAndExecute / ReWOO 策略选择；
- Auto 模式下用户本轮自然语言明确指定策略；
- Auto classifier prompt 对用户指定策略优先的说明；
- Auto 每轮 classifier 决策；
- Reflexion 开关；
- 策略上下文注入第一条 system message；
- 策略选择事件输出；
- Plan-and-Execute 计划创建；
- Plan state 合并到 leading system message；
- Plan step started/completed/failed 事件；
- ReWOO 工具图创建；
- ReWOO 工具节点真实执行，并复用工具授权/审计路径；
- ReWOO 工具观察回灌到后续执行上下文；
- Reflexion 最终答案校验与必要修正；
- ReflectionCompleted 事件输出；
- 不破坏现有 ReAct 执行路径。

后续继续增强：

- 增加真正的 `ReasoningOrchestrator`；
- 把当前 ReAct loop 抽成 `ReActStrategy`；
- 增加 plan state 持久化和暂停恢复；
- 增加 Reflexion 经验沉淀和 memory admission 持久化；
- 增强 ReWOO 工具图的并发、依赖表达和暂停恢复。
