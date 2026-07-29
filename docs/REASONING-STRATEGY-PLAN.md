# Goldfish Harness Reasoning Strategy 改造方案

## 背景

当前 Goldfish Harness 已经具备基础 ReAct 能力：模型调用、工具调用、流式事件、短中长期记忆、SQLite/Vec1 向量检索、动态技能、工具授权 hook、工具执行审计、上下文压缩。

后续需要支持更复杂的任务形态：

- 简单任务继续使用 ReAct；
- 长任务使用 Plan-and-Execute；
- 工具密集任务使用 ReWOO 降低 token 成本；
- 对前面所有模式增加 Reflexion 纠错能力。

本方案目标是在不推翻现有 Harness 的前提下，增加一个可组合的推理策略层。

## 设计原则

### 1. 策略可组合，不做四套孤立 Agent

不建议实现成：

```text
ReActAgent
PlanAgent
ReWOOAgent
ReflexionAgent
```

这样会导致 prompt、工具授权、记忆注入、状态管理、事件流、上下文压缩重复实现。

建议实现成：

```text
GoldfishHarnessRunner
  -> ReasoningOrchestrator
       -> ReActStrategy
       -> PlanAndExecuteStrategy
       -> ReWOOStrategy
       -> ReflexionLayer
```

其中：

- ReAct 是基础执行单元；
- Plan-and-Execute 负责长任务拆解和进度状态；
- ReWOO 负责多工具场景的批量工具图执行；
- Reflexion 是横切层，可包裹 ReAct、Plan-and-Execute、ReWOO。

### 2. 复用现有运行时能力

新增策略层必须复用已有组件：

- `IMemoryManager`
- `ISkillRegistry`
- `ISkillSessionStore`
- `IToolRegistry`
- `IToolAuthorizationHook`
- `IToolExecutionStore`
- `GoldfishHarnessEvent`
- `GoldfishSessionQueue`
- `ContextTokenEstimator`

策略只决定“如何组织推理流程”，不重复实现底层能力。

### 3. 保持单一开头 system message

无论使用哪种策略，最终模型输入仍保持：

```text
messages[0] system
  - 基础系统提示
  - runtime context
  - 当前 reasoning strategy
  - active plan / reflection summary
  - long-term memory
  - medium-term memory
  - loaded skills

messages[1..n]
  - short-term user/assistant history

messages[n+1]
  - current user message
```

禁止在对话中途插入新的 system message。策略状态和技能内容都只能合并到第一条 system message。

## 目标架构

```text
GoldfishHarnessRunner
   |
   v
ReasoningOrchestrator
   |
   +-- StrategySelector
   |
   +-- ReActStrategy
   |
   +-- PlanAndExecuteStrategy
   |      |
   |      +-- ReActStrategy as step executor
   |
   +-- ReWOOStrategy
   |      |
   |      +-- ToolGraphExecutor
   |
   +-- ReflexionLayer
          |
          +-- Verifier
          +-- RetryPolicy
          +-- ReflectionMemoryAdmission
```

运行时依赖关系：

```text
ReasoningStrategy
  uses Prompt/Message Builder
  uses MemoryManager
  uses Skill Runtime
  uses Tool Runtime
  uses Authorization Hook
  uses Execution Store
  emits GoldfishHarnessEvent
```

## 核心抽象

### ReasoningOptions

```csharp
public enum ReasoningStrategyKind
{
    Auto,
    ReAct,
    PlanAndExecute,
    ReWOO
}

public sealed class ReasoningOptions
{
    public ReasoningStrategyKind Strategy { get; set; } = ReasoningStrategyKind.Auto;
    public bool EnableReflexion { get; set; } = true;

    public int MaxReasoningSteps { get; set; } = 12;
    public int MaxPlanSteps { get; set; } = 20;
    public int MaxReflectionRetries { get; set; } = 1;

    public int LongTaskEstimatedTokenThreshold { get; set; } = 6000;
    public int ReWooToolCountThreshold { get; set; } = 4;

    public bool PersistPlanState { get; set; } = true;
    public bool PersistReflections { get; set; } = false;
}
```

### IReasoningStrategy

```csharp
public interface IReasoningStrategy
{
    Task<ReasoningResult> RunAsync(
        ReasoningRequest request,
        ReasoningRuntime runtime,
        CancellationToken cancellationToken);
}
```

### ReasoningRuntime

```csharp
public sealed class ReasoningRuntime
{
    public required IChatClient ChatClient { get; init; }
    public required IMemoryManager MemoryManager { get; init; }
    public required ISkillRegistry SkillRegistry { get; init; }
    public required ISkillSessionStore SkillSessionStore { get; init; }
    public required IToolRegistry ToolRegistry { get; init; }
    public required IToolAuthorizationHook AuthorizationHook { get; init; }
    public required IToolExecutionStore ToolExecutionStore { get; init; }
}
```

## 策略设计

### ReActStrategy

定位：当前 Harness 的默认行为。

适用场景：

- 简单问答；
- 少量工具调用；
- 短链路交互；
- 不需要显式计划状态的任务。

流程：

```text
BuildMessages
  -> ChatCompletion
  -> tool_call?
  -> Authorize Tool
  -> Execute Tool
  -> Append Tool Result
  -> ChatCompletion
  -> Final Answer
```

要求：

- 不保存完整 chain-of-thought；
- 工具结果只进入当前 loop；
- 工具审计只保存元信息和 hash；
- 只有通过 admission 的内容才能进入中长期记忆。

### PlanAndExecuteStrategy

定位：长任务模式。

适用场景：

- 代码修改；
- 部署；
- 排障；
- 多步骤分析；
- 需要持续进度状态的任务。

流程：

```text
CreatePlan
  -> Persist PlanState
  -> Execute Step 1 with ReAct
  -> Validate Step 1
  -> Update PlanState
  -> Execute Step 2 with ReAct
  -> ...
  -> Finalize
```

PlanState：

```csharp
public sealed record ReasoningPlan
{
    public required string PlanId { get; init; }
    public required string Goal { get; init; }
    public required IReadOnlyList<ReasoningPlanStep> Steps { get; init; }
    public int CurrentStepIndex { get; init; }
    public required string Status { get; init; }
}

public sealed record ReasoningPlanStep
{
    public required string StepId { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public string? ResultSummary { get; init; }
    public string? ErrorSummary { get; init; }
}
```

模型注入策略：

- 只注入 compact plan context；
- 不注入完整工具日志；
- 不注入完整历史计划变更；
- 当前目标、当前步骤、已完成摘要、剩余步骤、验证条件合并到 `messages[0] system`。

### ReWOOStrategy

定位：工具密集场景的降 token 策略。

适用场景：

- 多源检索；
- 多工具聚合；
- 工具链依赖关系明确；
- 中间观察不需要频繁让模型重新决策；
- 上下文预算紧张。

流程：

```text
Generate ToolGraph
  -> Validate ToolGraph
  -> Authorize Each Tool Node
  -> Execute ToolGraph
  -> Compact Observations
  -> Solver Final Answer
```

工具图示例：

```json
{
  "steps": [
    {
      "id": "E1",
      "tool": "search_docs",
      "arguments": {
        "query": "Goldfish Harness memory architecture"
      }
    },
    {
      "id": "E2",
      "tool": "read_file",
      "arguments": {
        "path": "${E1.best_file}"
      },
      "dependsOn": ["E1"]
    }
  ]
}
```

硬约束：

- 每个工具节点仍然必须走 `IToolAuthorizationHook`；
- 工具图必须做 schema validation；
- 工具结果必须摘要化或截断；
- 失败节点可以局部 fallback 到 ReAct；
- ReWOO 不绕过沙箱机制。

### ReflexionLayer

定位：横切纠错层。

它不是独立主策略，而是 wrapper：

```text
ReflexionLayer(ReActStrategy)
ReflexionLayer(PlanAndExecuteStrategy)
ReflexionLayer(ReWOOStrategy)
```

触发条件：

- 工具失败；
- 验证失败；
- 模型输出不满足约束；
- 用户指出错误；
- 长任务阶段结束需要复盘；
- 达到 retry policy 允许的纠错点。

流程：

```text
Strategy Run
  -> Verify Result
  -> if pass: return
  -> if fail:
       Generate Reflection
       Build Correction Plan
       Retry within budget
       Optionally admit durable lesson
```

反思分层：

| 类型 | 生命周期 | 存储位置 |
|---|---|---|
| RunReflection | 当前 run | volatile runtime |
| SessionReflection | 当前 session | session state / medium-term |
| DurableLesson | 跨 session | long-term memory after admission |

Reflexion 不允许直接写长期记忆。必须经过 `MemoryAdmission`：

```text
Reflection
  -> Stability Check
  -> Sensitivity Check
  -> Verification Check
  -> Deduplication
  -> Long-term Memory
```

## StrategySelector

第一版使用规则选择，避免引入额外模型成本。

规则：

```text
if request explicitly specifies strategy:
    use specified strategy

else if task is long-running / coding / deploy / debug / refactor:
    PlanAndExecute

else if estimated tool count >= ReWooToolCountThreshold
     or query is multi-source retrieval / aggregation:
    ReWOO

else:
    ReAct
```

Reflexion 默认开启，但条件触发，不每轮强制执行。

## 与记忆系统的关系

```text
short-term
  - 最近 user/assistant 消息
  - 作为普通 history message

medium-term
  - 压缩后的会话摘要
  - 可包含 session-level reflection summary
  - 注入 system 的中期记忆

long-term
  - 稳定跨会话记忆
  - 可包含 durable lesson
  - 注入 system 的长期记忆

user-profile
  - 用户画像和偏好
  - 不存技能
  - 不存临时反思
```

Plan 状态不等于记忆：

```text
PlanState
  -> reasoning state store
  -> 当前任务恢复和进度展示

Memory
  -> 经过 admission 的上下文事实
  -> 后续会话可检索
```

## 与技能系统的关系

技能保持 session-scoped：

```text
SkillIndexTool
  -> LoadSkillTool
  -> ISkillSessionStore
  -> BuildMessages
  -> messages[0] system
```

约束：

- 技能不进入用户画像；
- 技能不默认进入长期记忆；
- 技能只绑定当前 session；
- 同 session 恢复已加载技能；
- 新 session 重新识别和加载。

Plan-and-Execute 可以在 planning 或 step execution 阶段触发技能加载。

## 与工具授权和沙箱的关系

所有策略共用同一套授权链路：

```text
ToolCall / ToolGraphNode
  -> IToolAuthorizationHook
       -> Allow
       -> Deny
       -> RequireApproval
  -> IToolRegistry
  -> IToolExecutionStore
```

Plan-and-Execute 和 ReWOO 必须支持暂停和恢复：

```text
RequireApproval
  -> emit approval-required event
  -> persist strategy state
  -> wait user approval
  -> resume from pending step/node
```

## 事件模型扩展

建议新增事件类型：

```text
ReasoningStrategySelected
PlanCreated
PlanStepStarted
PlanStepCompleted
PlanStepFailed
ReflectionStarted
ReflectionCompleted
ReWooGraphCreated
ReWooNodeStarted
ReWooNodeCompleted
ApprovalRequired
StrategyFallback
```

事件用于：

- UI 展示长任务进度；
- 调试策略选择；
- 恢复暂停任务；
- 审计工具和策略行为。

## 状态存储扩展

短期可以复用 `SqliteHarnessStateStore` 增加 JSON state。

建议新增逻辑表：

```text
reasoning_sessions
  - run_id
  - partition fields
  - strategy
  - status
  - created_at
  - updated_at

reasoning_plans
  - plan_id
  - run_id
  - goal
  - status
  - current_step_index
  - plan_json

reasoning_reflections
  - reflection_id
  - run_id
  - scope
  - trigger
  - summary
  - accepted_as_memory
```

第一版也可以只实现一个通用表：

```text
harness_strategy_state
  - id
  - partition fields
  - run_id
  - kind
  - state_json
  - created_at
  - updated_at
```

## 落地阶段

### P0：抽象策略层，不改变行为

目标：当前行为保持 ReAct。

任务：

- 新增 `ReasoningOptions`；
- 新增 `IReasoningStrategy`；
- 新增 `ReasoningOrchestrator`；
- 把当前 loop 抽为 `ReActStrategy`；
- `GoldfishHarnessRunner` 通过 orchestrator 调用；
- 默认 `Auto -> ReAct`；
- 测试保持现有用例通过。

### P1：Plan-and-Execute

目标：支持长任务。

任务：

- 新增 `PlanAndExecuteStrategy`；
- 新增 `ReasoningPlan` / `ReasoningPlanStep`；
- 新增 plan event；
- step 内部复用 `ReActStrategy`；
- 支持 plan state 持久化；
- 支持 approval-required 暂停恢复。

### P2：Reflexion

目标：支持验证失败后的纠错。

任务：

- 新增 `Verifier`；
- 新增 `ReflectionResult`；
- 新增 `ReflexionLayer`；
- 支持 step-level retry；
- 支持 final-answer verification；
- durable lesson 走 `MemoryAdmission`。

### P3：ReWOO

目标：降低工具密集任务 token 成本。

任务：

- 新增 tool graph DSL；
- 新增变量引用解析；
- 新增工具图执行器；
- 新增 observation compactor；
- 新增 solver 汇总；
- 支持失败 fallback 到 ReAct。

## 推荐优先级

```text
1. Strategy 抽象
2. Plan-and-Execute
3. Reflexion
4. ReWOO
```

原因：

- 当前使用场景更偏长任务、代码修改、部署、排障；
- Plan-and-Execute 对现有 Harness 改动最小，收益最大；
- Reflexion 可直接增强 Plan 和 ReAct 的稳定性；
- ReWOO 对工具图 DSL、变量绑定、错误恢复要求更高，适合后置。

## 验收标准

### P0

- 默认行为和当前 ReAct 一致；
- 原有测试通过；
- 不新增中途 system message；
- 策略选择事件可观测。

### P1

- 长任务可以生成 plan；
- 每个 step 有开始、完成、失败事件；
- step 内部可调用工具；
- 用户授权暂停后可恢复；
- plan compact context 注入 `messages[0] system`。

### P2

- 工具失败或验证失败可触发 reflection；
- retry 次数受限；
- reflection 不直接污染长期记忆；
- durable lesson 必须经过 admission。

### P3

- 可生成合法 tool graph；
- 每个工具节点单独授权；
- 工具结果被 compact；
- ReWOO 失败可 fallback；
- 多工具任务 token 使用低于同等 ReAct 链路。
