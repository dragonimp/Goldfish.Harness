# Goldfish Harness 推理策略培训与演示材料

本文用于培训和现场演示 Goldfish Harness 当前支持的推理策略、会话级切换方式、Reflexion 开关，以及每种选项能带来的可观察效果。

适用对象：

- 需要理解 Goldfish Harness 策略机制的研发同学；
- 需要向业务/产品演示 Agent 行为差异的同学；
- 需要排查企业微信会话中策略状态的运维/研发同学。

## 1. 一句话说明

Goldfish Harness 当前把“怎么推理”拆成可观测、可切换的会话策略：

- `auto`：默认模式，由系统自动选择 `react` / `plan` / `rewoo`。
- `react`：边想边做，适合短任务和单链路工具调用。
- `plan`：先规划再执行，适合多步骤长任务。
- `rewoo`：先形成工具调用图再汇总，适合多信息源、多工具依赖明显的任务。
- `reflexion=on|off`：控制失败/异常/用户纠错后的反思修正层。

用户在企业微信或其他网关会话里可以用 `/strategy` 命令查看或切换当前会话的策略。

## 2. 当前支持的命令

### 2.1 查看当前状态和支持清单

```text
/strategy list
```

预期会返回：

- 当前会话 ID；
- 当前 Requested / Effective 策略；
- SelectionReason；
- Reflexion 是否开启；
- 支持的策略清单；
- 支持的开关；
- 常用命令示例。

如果会话尚未跑过 Goldfish Harness 请求，也会返回支持清单，只是当前状态会显示“尚未选择”。

### 2.2 切换策略

```text
/strategy use auto
/strategy use react
/strategy use plan
/strategy use rewoo
```

说明：

- `auto`：恢复自动选择；
- `react`：强制当前会话使用 ReAct；
- `plan`：强制当前会话使用 PlanAndExecute；
- `rewoo`：强制当前会话使用 ReWOO。

### 2.3 切换 Reflexion

```text
/strategy reflexion on
/strategy reflexion off
```

也可以在切换策略时一起指定：

```text
/strategy use plan reflexion=off
/strategy use rewoo reflexion=on
```

## 3. 实现核心逻辑

### 3.1 策略选择入口

Goldfish Harness 每次执行都会先选择推理策略，然后把策略选择结果写入事件流：

```text
已选择推理策略：PlanAndExecute（Auto / auto-classifier:0.82:multi-step task / Reflexion=开启）
```

事件中包含：

- `Requested`：用户请求的策略，例如 `Auto`；
- `Effective`：最终生效的策略，例如 `PlanAndExecute`；
- `SelectionReason`：选择原因，例如 `auto-user-directed`、`auto-classifier`、`auto-structural`、`session-command`；
- `Reflexion`：是否开启。

### 3.2 Auto 策略不是写死关键词

`auto` 的选择顺序是：

1. 如果用户本轮明确要求“走/使用/采用某种策略”，优先按用户要求生效；
2. 否则，每一轮都调用大模型分类器，让它只输出策略 JSON；
3. 如果分类器失败、置信度不足或输出不可解析，则使用结构特征兜底。

示例：

```text
我希望你走plan模式，进行mcp服务的获取和分析。
```

在 `auto` 模式下，这类明确控制意图会选择 `PlanAndExecute`，并显示：

```text
SelectionReason: auto-user-directed:PlanAndExecute
```

分类器候选：

```text
ReAct | PlanAndExecute | ReWOO
```

结构特征兜底关注：

- 请求长度和估算 token；
- 非空行数；
- 列表/步骤/段落边界；
- URL/路径/JSON/code fence；
- 工具密度；
- 计划复杂度。

因此它不是靠“写死几个业务关键词”判断任务复杂度，而是“用户明确控制指令优先，LLM 分类其次，结构特征兜底”。普通提问如“解释 react 和 plan 的区别”不会被当成策略切换。

### 3.3 会话级策略切换

`/strategy use ...` 不进入模型自由解释，而是走网关命令控制面：

```text
企业微信/网关消息
  -> GatewayDispatchService 识别 /strategy
  -> GatewayServer runtime-gateway-command
  -> 目标 AgentNode /runtime/gateway-command
  -> GoldfishSessionHistoryStore 写入当前 session 的显式策略指令
  -> 下一轮 Harness 请求按显式策略指令生效
```

这样做的好处：

- 可审计；
- 不依赖模型是否“理解”切换指令；
- 不污染普通业务对话；
- 可以通过 `/strategy list` 明确查询当前状态。

### 3.4 PlanAndExecute 的核心行为

当 Effective 策略为 `PlanAndExecute`：

1. Harness 会先创建 JSON plan；
2. 生成 `PlanCreated` 事件；
3. 把计划追加到第一条 system message 的 `## 当前执行计划`；
4. 执行时按步骤产生：
   - `PlanStepStarted`
   - `PlanStepCompleted`
   - `PlanStepFailed`

用户可观察到的效果是：回复前会先出现“已创建执行计划”，然后每一步有进度。

### 3.5 ReWOO 的核心行为

ReWOO 面向多工具/多信息源场景：

1. 先形成工具调用图或工具执行结构；
2. 工具节点仍然逐个走授权 hook；
3. 再综合多个工具结果形成最终答案；
4. 过程事件里会出现 `ReWooGraphCreated`。

用户可观察到的效果是：它更像“先列任务图，再批量收集证据，最后汇总”。

### 3.6 Reflexion 的核心行为

Reflexion 是纠错层，不是独立主策略。

开启后，它会在明确约束、失败、验证不通过、工具异常或用户纠错等场景触发：

- 工具异常；
- 验证不通过；
- 用户指出错误；
- 计划步骤失败；
- 输出和目标不一致。

关闭后，系统不做这一层反思修正，会更直接、更快，但失败后的自我修正能力下降。

## 4. 演示前准备

建议演示使用企业微信当前 Goldfish 会话。

先确认当前支持项：

```text
/strategy list
```

如果要恢复干净状态：

```text
/strategy use auto
/strategy reflexion on
```

注意：策略切换通常对“下一轮 Goldfish Harness 请求”生效。

## 5. 演示案例总览

| 案例 | 要证明的点 | 建议策略 | 可观察效果 |
|---|---|---|---|
| A | `/strategy list` 能显示当前状态和支持项 | 任意 | 返回当前状态、支持策略、Reflexion 开关和示例命令 |
| B | ReAct 更适合短链路任务 | `react` | 没有显式计划，直接回答或少量工具调用 |
| C | PlanAndExecute 会先规划再执行 | `plan` | 出现策略选择和计划事件，步骤感明显 |
| D | ReWOO 适合多信息源/多工具汇总 | `rewoo` | 先形成工具图/多路收集，再汇总 |
| E | Auto 会根据任务自动选择 | `auto` | 简单任务偏 ReAct，复杂任务偏 Plan，工具密集任务偏 ReWOO |
| F | Reflexion 开关影响失败后的修正 | `plan reflexion=on/off` | on 时失败/纠错后会反思修正，off 时更直接 |
| G | 显式会话策略生效 | `plan` 后连续任务 | `/strategy list` 显示 session-command |

## 6. 详细演示案例

### 案例 A：查看支持的策略和开关

命令：

```text
/strategy list
```

预期输出重点：

```text
当前推理策略
- 会话ID: ...
- Requested: ...
- Effective: ...
- SelectionReason: ...
- Reflexion: 开启/关闭

支持的推理策略
- auto
- react
- plan
- rewoo

支持的开关
- reflexion=on|off
```

讲解点：

- 这是面向用户的可观测入口；
- 即使当前没有策略缓存，也能看到支持项；
- 后续所有演示都用这个命令确认状态。

### 案例 B：ReAct 短任务演示

设置：

```text
/strategy use react
```

演示 Prompt：

```text
请用三句话解释什么是家庭积分系统，并给一个适合小学生的例子。
```

预期效果：

- `/strategy list` 显示 `Effective: ReAct`；
- 过程里不应该出现“已创建执行计划”；
- 回复直接、短、没有多步骤计划；
- 适合说明 ReAct 是基础工具循环/短任务策略。

讲解点：

- ReAct 不代表没有推理，而是没有强制先生成完整计划；
- 对简单问答、短链路任务，ReAct 的响应成本最低。

### 案例 C：PlanAndExecute 多步骤任务演示

设置：

```text
/strategy use plan
```

演示 Prompt：

```text
请帮我设计一个家庭积分系统的一周运营方案。要求包括：
1. 积分规则；
2. 每天的任务安排；
3. 奖励兑换表；
4. 家长复盘方式；
5. 最后给出一个可直接执行的清单。
```

预期效果：

- `/strategy list` 显示 `Effective: PlanAndExecute`；
- 过程里出现：
  - `已选择推理策略：PlanAndExecute`
  - `已创建执行计划`
  - 计划步骤开始/完成；
- 最终输出结构化程度明显高于 ReAct；
- 用户能看到任务拆分和执行顺序。

讲解点：

- PlanAndExecute 不是简单“输出一个计划”，而是 Harness 先生成计划，再把计划压缩进 system message，后续执行围绕计划进行；
- 对长任务、分阶段产物、需要过程可观测的场景更合适。

### 案例 D：ReWOO 多信息源/多工具密集演示

设置：

```text
/strategy use rewoo
```

演示 Prompt：

```text
请帮我比较家庭积分系统里三类奖励机制：
1. 每日小奖励；
2. 周末兑换；
3. 长期目标奖励。

请分别从激励效果、执行成本、容易失败的点、适合年龄段四个角度比较，最后给一个组合方案。
```

如果现场有工具可用，可以换成工具密集版：

```text
请检查当前项目中家庭积分系统相关的需求文档、接口定义和前端页面，总结奖励机制目前实现到什么程度，并列出缺口。
```

预期效果：

- `/strategy list` 显示 `Effective: ReWOO`；
- 工具密集版更容易出现多路信息收集；
- 过程里会出现 `已创建 ReWOO 工具图`；
- 最终答案更像“多来源证据汇总”。

讲解点：

- ReWOO 适合先规划多个信息收集节点，再综合；
- 所有工具节点仍然走授权 hook，不绕过安全控制；
- 它不是“更强的 Plan”，而是更偏多工具图的策略。

### 案例 E：Auto 自动选择演示

设置：

```text
/strategy use auto
```

演示 1：简单任务

```text
用一句话解释 ReAct 和 PlanAndExecute 的区别。
```

预期：

- 倾向 `ReAct`；
- `SelectionReason` 可能显示 `auto-classifier` 或 `auto-structural:simple`。

演示 2：复杂计划任务

```text
请制定一个两周的家庭积分系统上线计划，包含需求确认、规则设计、页面验收、试运行、复盘优化和风险清单。
```

预期：

- 倾向 `PlanAndExecute`；
- 可能出现计划事件。

演示 3：工具密集任务

```text
请检查项目里的后端接口、前端页面、配置文件和文档，汇总家庭积分系统当前可演示能力、缺口和下一步改造建议。
```

预期：

- 倾向 `ReWOO` 或 `PlanAndExecute`；
- 如果工具密度高，结构兜底倾向 `ReWOO`。

讲解点：

- Auto 优先使用 LLM 分类器；
- 分类器不可用或置信度不足时，结构特征兜底；
- 不是硬编码关键字。

### 案例 F：Reflexion 开关演示

#### F1：关闭 Reflexion

设置：

```text
/strategy use plan reflexion=off
```

演示 Prompt：

```text
请制定一个家庭积分系统规则，要求最终奖励表里故意不要出现“电子屏幕时间”。
然后检查你自己的输出是否违反这个限制。
```

预期效果：

- `/strategy list` 显示 `Reflexion: 关闭`；
- 如果模型输出中出现限制冲突，系统不会额外触发 Reflexion 纠错层；
- 输出更直接。

#### F2：开启 Reflexion

设置：

```text
/strategy use plan reflexion=on
```

演示 Prompt：

```text
请制定一个家庭积分系统规则，要求最终奖励表里不要出现“电子屏幕时间”。
如果你的输出违反限制，请主动修正并说明修正点。
```

预期效果：

- `/strategy list` 显示 `Reflexion: 开启`；
- 当出现失败、限制冲突、工具异常或用户纠错时，系统会校验并在必要时修正最终答案；
- 更适合高准确性/高约束任务。

讲解点：

- Reflexion 是纠错层，不是主策略；
- 开启更稳，关闭更直接；
- 它不直接写长期记忆，持久化经验仍需 memory admission。

### 案例 G：显式会话策略持久，Auto 每轮重新决策

设置：

```text
/strategy use plan
```

确认：

```text
/strategy list
```

预期：

```text
Effective: PlanAndExecute
SelectionReason: session-command:PlanAndExecute
```

然后发送一个新任务：

```text
请继续把刚才的方案整理成会议纪要格式。
```

再次确认：

```text
/strategy list
```

预期：

- 当前会话仍然保持手动选择的策略；
- 下一轮 Goldfish Harness 请求会沿用当前会话策略；
- 如果使用 `auto`，每轮都会重新走 classifier 或结构兜底；上一轮选择结果只用于状态展示，不参与决策。

讲解点：

- 策略不是全局变量，是会话级；
- 不同企业微信会话可以有不同策略；
- Auto 模式不复用上一轮选择做决策，每轮都会重新让 classifier 判断；
- `/strategy use auto` 或 `/strategy clear` 可恢复自动。

## 7. 推荐现场演示脚本

### 7.1 开场：能力清单

```text
/strategy list
```

讲解：

- 当前系统支持 4 种策略入口：`auto`、`react`、`plan`、`rewoo`；
- 支持 `reflexion` 开关；
- 所有策略状态都可查询。

### 7.2 简单任务对比 ReAct

```text
/strategy use react
```

```text
请用三句话解释什么是家庭积分系统，并给一个适合小学生的例子。
```

讲解：

- 直接输出；
- 无显式计划；
- 适合短链路。

### 7.3 复杂任务对比 Plan

```text
/strategy use plan
```

```text
请帮我设计一个家庭积分系统的一周运营方案。要求包括积分规则、每日任务、奖励兑换表、家长复盘方式和可执行清单。
```

讲解：

- 会看到计划事件；
- 输出分阶段；
- 适合长任务。

### 7.4 多信息源对比 ReWOO

```text
/strategy use rewoo
```

```text
请从激励效果、执行成本、风险点、适合年龄段四个角度比较每日小奖励、周末兑换、长期目标奖励，并给组合方案。
```

讲解：

- 更适合多维度收集与综合；
- 工具密集任务更明显。

### 7.5 Auto 自动选择

```text
/strategy use auto
```

```text
请制定一个两周的家庭积分系统上线计划，包含需求确认、规则设计、页面验收、试运行、复盘优化和风险清单。
```

讲解：

- Auto 会选择 Effective 策略；
- Auto 每轮都会调用 classifier；如果用户本轮明确说“走 plan 模式”，则直接选择 `PlanAndExecute`，SelectionReason 为 `auto-user-directed:PlanAndExecute`；
- 看 `/strategy list` 或过程事件确认结果。

### 7.6 Reflexion 开关

```text
/strategy use plan reflexion=off
```

```text
/strategy list
```

```text
/strategy reflexion on
```

```text
/strategy list
```

讲解：

- Reflexion 可以独立开关；
- 适合展示“策略”和“纠错层”是两个维度。

## 8. 观察点和验收标准

每个演示都建议观察 4 个点：

1. `/strategy list` 是否显示预期 Effective 策略；
2. 过程消息里是否出现 `已选择推理策略`；
3. Plan 场景是否出现计划事件；
4. Reflexion 状态是否按命令改变。

最小验收矩阵：

| 操作 | 预期 |
|---|---|
| `/strategy list` | 返回当前状态、支持策略、支持开关 |
| `/strategy use react` | `Effective: ReAct` |
| `/strategy use plan` | `Effective: PlanAndExecute` |
| `/strategy use rewoo` | `Effective: ReWOO` |
| `/strategy reflexion off` | `Reflexion: 关闭` |
| `/strategy reflexion on` | `Reflexion: 开启` |
| `/strategy use auto` | 恢复自动选择 |

## 9. 常见问题

### Q1：为什么我发 `/strategy list` 看到“尚未选择”？

说明当前会话还没有跑过 Goldfish Harness 请求，也没有手动指定策略。

可以先手动指定：

```text
/strategy use plan
```

或直接发一个业务任务，让 Harness 自动选择。

### Q2：为什么切换策略后当前这一条没有变化？

策略切换写入当前会话状态，通常从下一轮 Goldfish Harness 请求开始生效。

### Q3：Auto 是不是靠关键词？

不是。普通任务选择优先走 LLM 分类器，只要求分类器输出 JSON；分类失败或置信度不足时，才走结构特征兜底。

但用户明确说“走 plan 模式 / 使用 rewoo 策略 / 采用 react”属于控制指令，应优先于 classifier。

### Q4：Reflexion 开了是不是每次都会多跑一步？

不是。

它只在失败、验证不通过、工具异常或用户纠错时触发。

### Q5：ReWOO 是否绕过工具授权？

不绕过。

ReWOO 的工具图节点仍必须逐个经过授权 hook。

## 10. 部署和排查提醒

涉及 `/strategy`、runtime command、Goldfish Harness 策略能力的改动时，不能只更新 `zz` 上的 AgentNode。

必须检查 GatewayServer DB 中当前 Agent 实际绑定的执行节点。

已验证案例：

- 会话：`gw-84-a36-wecom-ws-84-wengzhishan`
- Agent：`36`
- 绑定节点：`mac-goldfish-harness`
- 地址：`http://mac.t.impx.net:8651`
- 更新命令：

```bash
bash deploy/local/start-goldfish-harness-agent-node.sh
```

验证链路：

1. 本机直连 `http://127.0.0.1:8651/runtime/gateway-command`；
2. zz 访问 `http://mac.t.impx.net:8651/runtime/gateway-command`；
3. zz 走 GatewayServer `/api/tunnel-runtime/runtime-gateway-command`。

如果只更新了 `zz`，但会话实际绑定到 `mac-goldfish-harness`，GatewayServer 仍会转发到旧节点，可能出现 404/空 body/500。
