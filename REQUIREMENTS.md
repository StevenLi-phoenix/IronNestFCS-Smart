# IronNestFCS-Smart 增强需求规格 v2(clean-house 重实现)

本文档是对既有 smart-fdc 增强(36 个开发 commit)的**完整需求提炼**。重实现者以本文档
和上游 baseline 代码(origin/master)为唯一输入,**禁止阅读旧实现的 diff 或 master 分支**。
文中给出的字符串(日志、错误消息、UI 文本)是**规范本体**,逐字使用;数值常量同理。

**v2 说明**:本版把一轮逐模块审计(91 条发现)的修正全部融入正文。总原则是**忠实旧行为**:
凡规格与旧实现不符处,一律以旧实现为准修订规格。仅有三处例外,均在正文相应位置显式声明:

1. **编码**(§0、附录 C):所有 `°` 一律是 U+00B0,中文一律是正确中文。旧实现日志里出现的
   `掳` 是源文件丢 BOM 后被按 GBK 重解码的编码事故,**不要复现**。
2. **取消的可观测性**(§14、§17):`CancelPendingBySerial` 改为**调用 `RecordTaskResult`**,
   使被取消的任务以 `progress = Failed`、`failureReason = "cancelled by commander"` 进入
   `RecentTasks`。这是相对旧实现的**有意行为变更**,动机见 §14/§17。
3. **CoroutineLock 反射歧义**(§17):保留全部 `Acquire` 重载(忠实旧实现),但把外部桥
   `GetMethod("Acquire")` 抛 `AmbiguousMatchException` 这一事实写进契约,并规定由外部改用
   `GetMethod("Acquire", Type.EmptyTypes)`;FCS 侧**不**为此新增别名方法。

## 0. 实现纪律

- 语言/框架:C# net6.0,MelonLoader 0.7.x IL2CPP mod,与 baseline 一致。
- 所有协程运行在 Unity 主线程(MelonCoroutines 协作式调度,无真并发)。锁必须
  `yield return lock.Acquire(...)` + `try/finally Release()`。
- **每次 `yield return` 之后必须重查计划存活性**,但**存活性谓词按阶段不同**,不是一个统一
  三联式:未取得共享方位所有权的准备阶段(装填、pre-aim)**不得**把 `_current == plan` 纳入
  存活性判定,否则同批搭档的计划会在装填完成后立即 `yield break`。逐阶段谓词见 §8 与 §18.1。
- 公开 API 的类型名、成员名、签名必须与 §17 兼容性契约完全一致(外部 mod 通过反射调用)。
- 新代码写进 baseline 对应文件;可以自由改善内部结构,但不改 baseline 已有公开面。
- **源文件编码(硬纪律)**:所有含非 ASCII 字面量的 `.cs` 文件必须保存为 **UTF-8 with BOM**。
  缺 BOM 时,中文区 locale 下 C# 编译器会把字符串字面量按 ANSI/GBK 重新解码,静默污染日志
  与玩家可见文本(旧实现即因此把 `°`(U+00B0,UTF-8 `C2 B0`)固化成了 `掳`(U+63B3,
  `E6 8E B3`))。本规格中所有度数符号一律是 **U+00B0 `°`**;`掳` 是事故产物,**禁止复现**。
  同一纪律适用于所有中文日志/UI 字面量。

## 1. 基线架构速览(实现者需先读的 baseline 类)

| 类 | 职责 |
|---|---|
| `FSC` | 顶层:绑定场景、Update 泵、暴露 LeftTask/RightTask/QueueCan/RecentTasks 等 |
| `Scheduling/TaskDispatcher` | 待办队列、规划轮(planning round)、任务准入 |
| `Scheduling/FirePlanner` | 任务→炮位匹配的资格评估与 FirePlan 物化 |
| `Scheduling/TaskGunMatcher` | 双炮-多任务组合的代价比较 |
| `Scheduling/FirePriorityCoordinator` | 一批(batch)内两计划的开火顺序比对 |
| `Execution/FirePlanExecutor` | 装填→瞄准→击发全程执行、共享方位仲裁 |
| `Infrastructure/SharedConsoleCoordinator` | 三台共享操作台(弹道/征用/扳机)的串行化 |
| `FCS/MapTable` | 地图表:实体标记、射击诸元解算 |
| `FCS/PurchaseDeck` | 征用台买卡物理流程 |
| `FCS/CoroutineLock` | 协程互斥锁 |
| `FCS/BallisticCalculator` | 物理弹道台驱动;`MinimumCharge(distance)` 静态 |
| `FCS/FireReadyEstimator` | 就绪时间估计;含转速常量 |
| `FCS/TimeToImpactEstimator` | 实测 TTI 表;`TryEstimateSeconds(distanceKm, charge, out tti)` |
| `FcsRuntimeClock` | 任务时钟 `Now`、`WaitForSeconds`、`WaitUntilFocused`、`ResumeGeneration` |
| `FcsWindow` | IMGUI 状态窗 |

## 2. 游戏物理真值(附录 A 有数值汇总)

- **弹道模型是线性的**(实测 52 组 C1–C6、2.1–17.9km、AP/HE 台解验证,残差纯两位小数
  舍入 ±0.01°):`elevation(°) = distance(km) × 12 / charge`,上限 60°;
  即 `maxRange(charge) = charge × 5 km`。无弹种项、无阻力项。
- **火药一旦 commit 无法追加**;清膛的唯一方式是把这发打出去(游戏机制,不可绕过)。
- **坐标换算(运算次序是规范的一部分,不得改读)**:

  ```
  km    = local × 3.8164 + offset       offset = (10.016, 5.235)
  local = (km − offset) / 3.8164
  ```

  即**先缩放、后平移**。地图有效包络在 km 系是 `x ∈ [-1, 27]`、`y ∈ [-1, 16]`;
  但**所有「夹进地图包络」的运算(§7 `ApplyMotionModel` 的 aim、§14 `AdjustAim` 的入参
  x/y)一律在局部单位下夹,不先转 km**。因此规格把包络预先换算成四个局部单位常量:

  | 常量 | 定义式 | 近似值 |
  |---|---|---|
  | `MapLocalMinX` | `(-1f - 10.016f) / 3.8164f` | −2.8865 |
  | `MapLocalMaxX` | `(27f - 10.016f) / 3.8164f` | 4.4503 |
  | `MapLocalMinY` | `(-1f - 5.235f) / 3.8164f` | −1.6338 |
  | `MapLocalMaxY` | `(16f - 5.235f) / 3.8164f` | 2.8207 |

  这四个常量以字面表达式写进 `MapTable`(不写十进制近似值),避免舍入分岔。
- 炮塔转速(FireReadyEstimator 常量):方位 `AzimuthSlewDegreesPerSecond` = 4°/s,
  俯仰 `ElevationSlewDegreesPerSecond` = 2°/s,两轴并行运动。
- 物理炮塔角与地图方位角互为**相反数**(与 `FireReadyEstimator.AzimuthSeconds` 同约定)。
- 24h 世界时钟:场景里的 `GenericTimerSceneSync.CurrentTime`(秒);
  备选 `MissionStatsTracker.Instance.timerRunning ? timerValue`;再退 `Time.realtimeSinceStartup`。
- 玩家可拖动的炮塔棋子:map surface 下名为 `"Player Turret Piece"` 的子物体。该名字以
  `MapTable` 上的**公开常量** `public const string PlayerTurretPieceName = "Player Turret Piece"`
  暴露,绑定日志的对象名由该常量插值生成。
- 征用台卡槽世界坐标 `(6.4814, -2.4675, -22.0968)`;把卡 transform 摆到该点后调用其
  `DraggableItem.MoveToSlot()` 即等价玩家拖卡。
- **卡片 id 归一化 `NormalizeCardId`(规范原文,不是「整 id 判定」也不是「尾缀剥离」)**:

  ```csharp
  id.Replace("SMOKE", "SMK").Replace("PCLM", "PLCM").Replace("Shell", "").Trim()
  ```

  - 三次替换**按此固定顺序**;
  - 每次替换替换字符串中**全部出现处**而非仅尾缀(例如 `"ShellHE"` → `"HE"`);
  - 替换是**大小写敏感**的(`"smokeshell"` 不会被归一化),尽管后续的 id 比较用
    `OrdinalIgnoreCase`;
  - 最后 `Trim()` 两端空白。

  背景怪癖:游戏 id 含 `SMOKE` 对应枚举 `SMK`;游戏 id `PCLM` 对应**枚举名 `PLCM`**
  (上游枚举拼写如此)。
- 侦察类卡插入后动态生成台面控件:`DialOdometerPunchcardBridge`(`bearingDial`/
  `distanceDial` 拨盘 + `Bearing`/`Distance` 读值 + `SetBearingInternal(v, bool)`/
  `SetDistanceInternal(v, bool)`/`ForceRefreshAll()` 内部设置器)。
- 起始网格拨盘:`DialToSplitFlipDisplayBinder`(`orderedSymbols` 字符表、
  `outputRangeMin/Max`、`MapDialValueToSymbolIndex(value)`、`dial`),字母盘父物体名含
  `"Location L"`,数字盘含 `"Location N"`。
- 地图实体:fire-mission root 子物体上的 `EntityLocation` 组件(`.Entity` → `MapEntity`,
  有 `ID`/`RawID`/`IsAlive`)。
- **可见性定义(不是合取式,照字面读会全判成迷雾)**:

  ```
  visible = VisualRoot != null && VisualRoot.activeInHierarchy;
  if (visible && VisibilityGroup != null) visible = VisibilityGroup.alpha > 0.05f;
  ```

  即 `VisibilityGroup` 为 **null 的实体算可见**(不降低可见性);激活判定必须精确到
  `activeInHierarchy`,**不是** `activeSelf`。
- 装填失败时 baseline 产生的失败原因文本(是 §9 R7 恢复逻辑的触发器,不得改动 baseline 措辞):
  - 装药不符:`powder commit mismatch: expected C{n}, physical C{m}`(正则捕获两个数字);
  - 供药机暂时不可用:reason 含子串 `powder dispenser`。

## 3. R1 任务模型扩展(`FCS/ArtilleryTask.cs`)

`ArtilleryTask` 必须保留**公开无参构造函数**(外部桥用 `Activator.CreateInstance(taskType)`
凭空造任务);不得改成「只能经工厂/带参构造」。新建实例后未显式设置的字段必须是安全默认值,
尤其 `failureReason` 必须是 `""` 而**不是 null**(外部用 `Equals(reason, "")` 判有无失败原因),
`serial` 保持 0 由 dispatcher 赋值。字段类型冻结见 §17。

在 baseline `ArtilleryTask` 上新增公开字段(**名字精确**,反射契约见 §17):

| 字段 | 类型 | 语义 |
|---|---|---|
| `serial` | int | 全局唯一任务编号 #N。**首次判定用零值哨兵**:`if (task.serial == 0) task.serial = ++_serialCounter;`——非零一律原样保留(**含外部预置值**:serial 是 §17 公开字段,桥/LLM 可写)。重入队(被抢占退回、装填恢复退回)保持不变;计数器随 dispatcher 生命周期(F9/换场景)复位。`targetId` 是可回收的地图标记 id,会重复,不可作为外部句柄。 |
| `priority` | int = 50 | 调度优先级 0–100。≥90 为紧急(见 §5)。由调用方在入队前设置,入队不重置。必须是 **public int**(外部 `TrySetPriority` 用默认 BindingFlags,只看 public)。 |
| `hasAimPoint` / `aimLocal` | bool / Vector3 | 入队时固化的地图局部瞄准点。有它才能晚绑定解算(§6)。 |
| `trackEntityId` | string = "" | 非空 = FCS 自己跟踪该实体拟合运动模型(§7)。 |
| `trackingLost` | bool | 模型超过 90s 未更新仍在外推的标志。 |
| `hasMotion` | bool | 存在线性运动模型。外部(桥/LLM)可直接置模型而不跟踪。 |
| `motionOriginLocal` / `motionVelLocalPerSec` / `motionT0` | Vector3 / Vector3 / float | 模型 p(t) = origin + vel × (t − t0),t 用任务时钟(MissionNow)。 |
| `aimAdjusted` | bool | agent 入队后改过瞄(§14);使执行期各刷新阶段对静态任务也生效。 |
| `loadRetryCount` | int | 装填恢复(§9)的重试计数,**两条恢复路径共用同一个计数器**。 |
| `validForSeconds` / `firstEnqueuedAt` | float | 排队有效期(§10)。`firstEnqueuedAt` 同样用**零值哨兵**盖章:`if (task.firstEnqueuedAt == 0f) task.firstEnqueuedAt = FcsRuntimeClock.Now;`,重入队不重置(窗口从原始命令时刻起算)。 |

> 两个哨兵判定**不得**改用私有 bool「已入队过」标志——那会改变「外部预置 serial 被沿用」这一
> 可观测行为。

baseline 既有字段 `chargeCount`(int,已提交/已装装药,0 = 未定)同样在 §17 冻结之列
(外部桥读它拼进任务显示串),不得改成属性或私有化。

方法 `public string MotionSuffix(bool zh)`——HUD/外部读者共用的运动状态短语。
**分支判据是 `hasMotion` 与 `trackEntityId` 的析取,不是「有没有模型」**:

- `!hasMotion && trackEntityId.Length == 0` → **静态分支**:
  返回 `aimAdjusted ? (zh ? " · 已改瞄" : " · re-aimed") : ""`。
- `hasMotion || trackEntityId.Length > 0` → **运动分支**。即 `trackEntityId` 非空时,
  即使 `hasMotion` 仍为 false(第一次采样只存样本 vel=0、或目标一直在雾中从未采到样),
  也走运动分支;此时 vel 为零向量,速度算得 0 < 0.5 km/h,输出 `" · 跟踪 {id}(静止)"`。
- 运动分支内:速度 = `|vel| × 3.8164 × 3600` km/h;航向 = `atan2(vel.x, vel.y)` 转 0–360°。
  头部:跟踪时(`trackEntityId` 非空)`跟踪 {id}` / `track {id}`,否则 `运动模型` / `motion`;
  `trackingLost` 追加 `·失联外推` / `·extrapolating`;
  - 速度 < 0.5 km/h → `" · {头部}(静止){lost}"` / `" · {head}(static){lost}"`
    (zh=false 时括号词**逐字为 `(static)`**);
  - 否则 `" · {头部} {v:F0}km/h→{course:000}°{lost}"`。

## 4. R2 优先级协程锁(`FCS/CoroutineLock.cs`)

改造 baseline `CoroutineLock` 为**优先级队列锁**(仍单线程、无并发原语):

- `Acquire()` ≡ `Acquire(50)`。`Acquire(int priority)`:登记 (priority, 单调 seq) 票;
  等待条件 = `_held || 存在更高优先级票或同级更早票`;取锁后置 `_held`;**finally 中移除票**
  (协程被 Stop 时票不得泄漏)。同级 FIFO。
- **等待机制(不只是等待条件)**:等待循环体逐字为

  ```csharp
  while (_held || !IsNext(priority, seq))
      yield return null;
  ```

  ——**每帧重试一次**;条件初始即为假时**不产生任何 yield**(Acquire 不额外挂帧)。
  **不得**改用 `FcsRuntimeClock.WaitForSeconds(x)` 或 `WaitUntilFocused()` 轮询:那会给每次
  取锁引入额外延迟,并让锁等待被暂停/失焦阻塞,违背「人工等待期重调 P10 让路」的实时性前提。
- 可取消变体 `Acquire(Func<bool> shouldCancel, Action onAcquired)`(≡ priority 50)与
  `Acquire(shouldCancel, onAcquired, int priority)`:等待期间与真正占锁前都要再查
  `shouldCancel`(锁释放与本协程恢复之间可能发生 F9/取消);只有真取到锁才回调。
- `Reset()` 清 `_held` **和全部等待票**(热重载残留防死锁)。
- 四个 `Acquire` 重载**全部保留**(忠实旧实现)。由此产生的外部反射歧义(无参
  `GetMethod("Acquire")` 抛 `AmbiguousMatchException`)记在 §17,**不在 FCS 侧加别名方法**。

全局锁优先级约定(附录 B):规划期弹道台解算 = 任务 priority;执行期征用台/扳机台
acquire = 任务 priority;人工击发等待期的实时重调炮 = **10**(永不阻塞新任务规划);
火药自动补货 = **20**;外部买卡请求 = 请求自带 priority。baseline 中所有无参 `Acquire()`
调用点语义不变(默认 50)。

## 5. R3 火力优先级体系

**目标:高优先级任务在"抢炮、开火顺序、跨批次执行顺序"三个层面都优先。**

### 5.1 配位比较(`TaskGunMatcher.CompareSolutions`)

- 新增一个**独立的私有静态比较方法**(规范名 `CompareExplicitPriority(a, b)`)。它与 baseline
  既有的 `CompareTaskPriority(IReadOnlyList<TaskGunAssignment>, IReadOnlyList<TaskGunAssignment>, …)`
  是**两个不同的比较**——后者位于比较链更靠后的位置、做的是别的事,**保持原样、原位置不动**。
  不要误把本条读成「改造那个既有方法」。
- **插入位置精确**:`if (a.Count != b.Count) return b.Count.CompareTo(a.Count);` 之后的
  **第一条**比较,**位于 baseline 的「同一任务:已装填炮 vs 空炮」(LoadedReady)单任务例外
  判断之前**。放到例外之后会让紧急任务在某些配位上仍输给代价模型。
- 语义:两方案各取任务 priority 降序向量做字典序比较,更紧急者胜。返回值约定与
  `CompareSolutions` 一致(负数 = a 更优),**相等返回 0 继续向下走原比较链**
  (普通任务全是 50,该比较自然中立)。

### 5.2 同批比对(`FirePriorityCoordinator.ComparePair`)

- priority 不同 → 高者先打,`reason = $"priority P{first.Task.priority} over P{second.Task.priority}"`;
  相同才走 ETA/对齐逻辑。
- 该 reason **直接以原文**内插进配对日志
  `[FCS Order] batch {executionBatchId} paired once: {first.Label} first, {second.Label} second; {reason}`,
  **不经 `FcsLocalization.LogReason` / `UiReason`**(映射表里没有这个 key,套本地化会被改写或返空);
  它也**不出现在 `_statusText` 里**(配对状态文本固定为「一次性比对」措辞)。
- **`T{targetId}` → `#{serial}` 的全量替换规则**:baseline 中所有含 `T{task.targetId}` 的
  日志/状态文本,**逐字**把 `T{targetId}` 替换为 `#{serial}`,其余字符(标点、全角括号、空格、
  格式串)一律不改。受影响的具体行**至少**包括:

  - `FirePlanner`:
    - `[FCS Match] #{serial}: snapshot currentAz=…`
    - `[FCS Match] #{serial}: quick reject {side}; …`
    - `[FCS Match] #{serial}: quick reject {side} {shell} C{charge}; …`
    - `[FCS Plan] #{serial}: committed {Label}, E=…`
  - `FirePriorityCoordinator`(双语对):
    - `射击顺序：#{a} → #{b}（一次性比对）` / `Firing order: #{a} → #{b} (compared once)`
    - `射击顺序：#{n} 单独执行（{uiReason}）` / `Firing order: #{n} single commit ({uiReason})`
    - `射击顺序：#{n} 按既定顺序执行` / `Firing order: #{n} promoted in committed order`
    - `射击顺序：#{n} 未比对，等待另一个 FirePlan` / `Firing order: #{n} unpaired, waiting for another FirePlan`
    - `射击状态：#{n} 已物理击发，重新读取剩余计划` / `Fire state: #{n} physically fired; reconciling remaining plans`
    - `{sideName} #{n}: 计划ETA {eta:F1}s，E{E:F1} / Az{Az:F1}` / `{sideName} #{n}: planned ETA {eta:F1}s, E{E:F1} / Az{Az:F1}`
    - `{sideName} #{n}: ETA待测，alignment={s:F1}` / `{sideName} #{n}: ETA unavailable, alignment={s:F1}`

  这些行**不在附录 C**(附录 C 只收关键新增行);替换规则本身就是它们的规范。

### 5.3 跨批次执行顺序(`FirePlanExecutor.EvaluateScheduling`)

- **适用范围限定**:整段重排只在 baseline 前置门 `if (_current != null) return;` 通过时才评估
  ——**已占有共享方位执行权的计划永不被降级为 `_next`,也永不被主动打断**。
- 在已比对(`Compared`)的计划中取 **priority 最高者**为 committed 候选
  (`compared.OrderByDescending(p => p.Task.priority).First()`),`other` = 另一个 active 计划。
- **override 分支(唯一会打 priority override 日志的分支)**:当且仅当
  `other is { Compared: false } && other.Task.priority > committed.Task.priority`:
  `FirePriority.CommitSingle(other, "优先级高于已提交计划")`;先 `_next = committed`,
  再 `SetCurrent(other, promote: false)` 并 `return`。日志逐字:

  ```
  [FCS Order] priority override: {other.Label} (P{other.Task.priority}) fires before committed {committed.Label} (P{committed.Task.priority})
  ```

  (`P` 前**有空格**,与附录 C 一致。)
- **非 override 分支**:保持 baseline 的 `_next = other; SetCurrent(committed, promote: true);`。
  注意当两个计划**都已 Compared** 而后来者 priority 更高时,它已经通过
  `OrderByDescending` 成为 `committed` 并走 `promote: true`(PromoteCommitted)路径——
  **不**调用 `CommitSingle`、**不**打 priority override 日志。
- 同优先级维持既有 commit 顺序。指挥官的"P92 炮兵→P91 FDC"定序在上述范围内跨批成立。

### 5.4 紧急免凑对

`TaskDispatcher.WaitForMatchCoalesceWindow` 在队列存在 priority ≥ 90 的任务时立即返回
(不等第二个任务凑双炮批)。阈值常量 `UrgentPriorityThreshold = 90`。

### 5.5 紧急抢占(`FirePlanExecutor.TryPreemptForUrgent(ArtilleryTask urgent, out string detail)`,public)

**`EnqueueTask` 的精确七步时序(顺序可观测:日志顺序、被抢占任务在队列中的位置、
`TryDispatch` 触发次数)**:

1. `if (task.serial == 0) task.serial = ++_serialCounter;`
2. `if (task.firstEnqueuedAt == 0f) task.firstEnqueuedAt = FcsRuntimeClock.Now;`
3. 复位 baseline 字段:`progress = Pending`、`pendingHint = None`、`startedAt = FcsRuntimeClock.Now`。
4. `_taskQueue.Enqueue(task);`
5. `MelonLogger.Msg($"[FCS Dispatch] queued #{task.serial} P{task.priority}; pending={_taskQueue.Count}");`
6. **才**做紧急抢占尝试(此时紧急任务**已在队列里**;被抢占的 victim 通过嵌套 `EnqueueTask`
   排在紧急任务**之后**,且嵌套调用自己也会跑一次 `TryDispatch`):

   ```csharp
   if (task.priority >= UrgentPriorityThreshold && !_fcs.PlanExecutor.HasFreeGun
       && _fcs.PlanExecutor.TryPreemptForUrgent(task, out var preemptDetail))
   {
       MelonLogger.Msg($"[FCS Dispatch] urgent #{task.serial}: {preemptDetail}");
   }
   ```

   **短路 `&&` 是规范的一部分**:抢占失败时 detail 被丢弃,**不打任何日志**。
   (`!HasFreeGun` 前置守卫使 `"a gun is already free"` 从这个调用点根本不可达,但该 detail
   仍是 `TryPreemptForUrgent` 的契约返回值,供其它调用方使用。)
7. 最后 `TryDispatch();`

`TryPreemptForUrgent` 本身:

- 候选排除:计划为 null;victim priority ≥ urgent priority;victim 是当前共享方位
  执行者(`_current`)或已武装待发(`_fireWaitOwner`)或已观察到击发(`ShotObserved`);
  弹种不同;已装装药 < `BallisticCalculator.MinimumCharge(urgent.distance)`。
- **候选选取的遍历顺序与平局规则**:按 `new[] { _leftPlan, _rightPlan }` 的**左、右顺序**遍历,
  替换条件是**严格小于**(`plan.Task.priority < victim.Task.priority`);因此两门炮 priority
  相同时**选左炮**。
- 无候选/有空炮时返回 false 并给 detail
  (`"a gun is already free"` / `"no preemptable plan (current/armed, higher priority, or shell/charge mismatch)"`)。
- 得手:`CancelPreparation(victim)`;victim 计划标 Failed,FailureReason =
  `"preempted by urgent task"`;若是 fireWaitOwner 则 `ClearAllFireWait()`;
  `ReleaseGunSlot(victim, notify:false)`;**victim 的任务不算失败**:progress 复位
  Pending、failureReason 清空、pendingHint 清空,重新 `EnqueueTask`(serial 不变;其
  priority < urgent 故不会递归抢占)。detail = `"preempted {victim.Label}"`。
  **注意:抢占路径不清 `_current`/`_next`**(与 §9 的 `DetachForRequeue` 不同,见那里的对比表)。
- 紧急任务与被抢占任务的仰角都会在后续以实际装药重解,无需在此处理。

## 6. R4 晚绑定射击诸元 + 射击原点

### 6.1 射击原点 `MapTable.GetTurretLocalOnMap()`

- **每次调用都做惰性重试**(不是只在绑定时查一次):

  ```csharp
  if (turretMapModel == null && mapSurface != null)
  {
      turretMapModel = mapSurface.Find(PlayerTurretPieceName);
      if (turretMapModel != null) MelonLogger.Msg(/* 附录 C 绑定行 */);
  }
  if (turretMapModel != null) return turretMapModel.localPosition;  // 完整 Vector3,含 z
  ```

  这样棋子晚于场景绑定生成也能被后续调用捡到;**只有 Find 成功那一次打日志**。
- 未命中才走 baseline 的 `turretLocation` 反变换分支;该分支在
  `turretLocation == null || mapSurface == null` 时返回 `Vector3.zero`。
- 返回值是**完整 Vector3(含 z)**,不是水平投影。
- 指挥官对棋子的错误摆放会产生错误诸元——**by design**(棋子即指挥官的置信)。

### 6.2 `GetMarkTarget` 与 `RefreshSolution`

- `GetMarkTarget` 建出的任务顺带固化 `hasAimPoint = true; aimLocal = 标记 localPosition`。
  (`GetMarkTarget` 是 §17 反射契约面,**必须保持单一重载**。)
- `MapTable.RefreshSolution(ArtilleryTask)`(public):
  - **入口守卫**:`if (!task.hasAimPoint || mapSurface == null) return;`——mapSurface 未绑定时
    **静默返回**(与 `AdjustAim` 返回 `map surface unbound` 不同,这里不返回值也不记任何东西),
    `angel`/`distance`/`position` 保持原值不被覆写。缺了这条,未绑定时
    `GetTurretLocalOnMap()` 返回 `Vector3.zero`,会把全部待办任务的诸元覆写成以地图原点为
    炮位的垃圾解。
  - 由 `aimLocal − 原点` 经 baseline `BuildMarkTarget` 重推 `angel`/`distance`/`position`。
  - **三个字段无条件覆写**;阈值只影响是否打日志。
  - 日志条件(角度比较必须**环绕安全**):

    ```csharp
    if (Mathf.Abs(Mathf.DeltaAngle(task.angel, refreshed.angel)) > 0.05f
        || Mathf.Abs(task.distance - refreshed.distance) > 0.02f)
    ```

    **不得**写成 `Mathf.Abs(a - b)`——方位跨 0°/360° 时会得到 ~360 的假差值,每轮规划刷日志。

### 6.3 每个规划轮的诸元刷新链

**每个规划轮开始**(在 `Planner.CaptureSnapshot()` **之前**),dispatcher 对队列中每个
pending 任务执行:

```csharp
if (pending.trackEntityId.Length > 0)
    _fcs.MapTable.UpdateEntityMotion(pending);
_fcs.MapTable.ApplyMotionModel(pending);      // 默认 prep 45s 重载
_fcs.MapTable.RefreshSolution(pending);
```

**只有 `UpdateEntityMotion` 受 `trackEntityId` 非空的条件约束;`ApplyMotionModel` 与
`RefreshSolution` 对队列里每一个任务无条件调用。** 差别是可观测的:外部桥直接置 `hasMotion`
而不跟踪的任务、以及仅需原点重校的静态 `hasAimPoint` 任务,在错误解读下不会被刷新。

## 7. R5 运动目标模型(`FCS/MapTable.cs`)

### 7.1 `public static float MissionNow`

- 世界时钟分支的返回条件是**缓存非空且读数为正**:
  `if (_worldClock != null && _worldClock.CurrentTime > 0f) return _worldClock.CurrentTime;`
  ——读数 ≤ 0(时钟对象存在但未启动)只是**跳过该分支**下落到秒表,**不清缓存、不重新扫描**。
- 扫描触发条件是 `_worldClock == null`(**只扫一次**,满足 §18.5 的缓存要求);多个
  `GenericTimerSceneSync` 取 `CurrentTime` 最大者并缓存。
- **仅当 try 块访问抛异常**时 `_worldClock = null`,触发下次重扫。
- 依次退 `MissionStatsTracker.Instance.timerRunning ? timerValue`,再退 `Time.realtimeSinceStartup`。

### 7.2 `public void UpdateEntityMotion(ArtilleryTask task)`

- **入口守卫**:`if (task.trackEntityId.Length == 0 || fireMissionRoot == null || mapSurface == null) return;`
  ——fireMissionRoot/mapSurface 未绑时直接返回,**连 `trackingLost` 都不更新**。
- 通过守卫后、遍历子物体之前,**赋值**(不是条件更新)一次:

  ```csharp
  task.trackingLost = task.hasMotion && (now - task.motionT0 > TrackingLostAfterSeconds /* 90f */);
  ```

  即 `hasMotion == false` 时把 `trackingLost` 显式清成 false。
- 在 fire-mission root 的子物体里找 `EntityLocation`,其 Entity 的 `ID` 或 `RawID`
  等于 `trackEntityId`。Il2Cpp 属性访问全部 try/catch,**局部变量初值为
  `bool visible = false, alive = true;`**——即**异常等价于「进入迷雾」**,走保留旧模型的路径,
  而不是清空模型或抛出。
- 三条「静默返回、保留旧模型继续外推」的路径(战争迷雾语义),行为一致但成因不同:
  1. 实体不可见(§2 可见性定义);
  2. 实体已死;
  3. **遍历完所有子物体根本没找到该实体**。
- 可见:采样 `local = mapSurface.InverseTransformPoint(child.position)`;与上次采样求 dt。
  样本存放于 `MapTable` 的**实例**字段
  `private readonly Dictionary<string, (Vector3 local, float t)> _entitySamples = new();`
  ——**键是 `task.trackEntityId`,不是 serial**,因此跟踪同一实体的多个任务**共享同一份样本**;
  字典**从不清理**(没有 Reset、换场景不清空,只随 MapTable 实例消亡)。改成按任务存样本会
  改变 0.5–10s 采样窗与 0.5 低通的实际行为。
- **四个 dt 分支(第四个不可省)**:

  | 分支 | vel | 样本字典 |
  |---|---|---|
  | 0.5s ≤ dt ≤ 10s | `vel = (Δlocal)/dt`(z 清零);已有模型时低通 `Lerp(旧vel, 新vel, 0.5)` 抑制地图抖动 | 更新 |
  | dt > 10s(暂停/读档产生的陈旧样本) | **归零重新拟合** | 更新 |
  | **dt < 0.5s(含时钟回拨造成的负 dt)** | **不动** | **不动**(保留旧 (local, t),使下一次调用仍以旧样本计 dt——这正是 0.5s 最小采样窗真正生效的机制) |
  | 无历史样本 | 0 | 存入 |

  若在 dt < 0.5 分支顺手更新样本,采样窗形同虚设,高频调用(规划轮 + 执行期 3s 重调)会把
  速度拟合成噪声。
- 最后**无条件**(四个分支之后统一执行,dt < 0.5 分支也照走):
  `motionOriginLocal = local; motionT0 = now; hasMotion = true; trackingLost = false;`
  ——**样本时间戳与 `motionT0` 是两个独立时间基准,不得合并为一个。**

### 7.3 `ApplyMotionModel`

`public void ApplyMotionModel(ArtilleryTask)` ≡ prep 45s;
`public void ApplyMotionModel(ArtilleryTask, float prepSeconds)`:需 `hasMotion && hasAimPoint`。

**两遍不动点迭代**(提前量改变射程、射程改变飞行时间):

- 局部变量初值:`var aim = task.aimLocal;`、`var distanceKm = task.distance;`
  ——`distanceKm` 初值是**任务上一次解算出的射程**,不是现场从 `aimLocal` 推的。
- 每遍(`MissionNow` **每遍重新读取**):
  - `horizon = MissionNow − task.motionT0 + prepSeconds + FlightSecondsFor(task, distanceKm)`;
  - `lead = vel × horizon`(z 清零),模长封顶 3 km(局部单位 `3f / 3.8164f`);
  - `aim = motionOriginLocal + lead`,x/y 用 §2 的四个局部单位常量夹取,
    `aim.z = task.aimLocal.z`(因为 `task.aimLocal` 直到两遍**全部结束**才写回,遍内取到的
    始终是**原始 z**);
  - 遍末用**水平口径**重算射程供下一遍:
    `distanceKm = new Vector2(toAim.x, toAim.y).magnitude * 3.8164f`,其中
    `toAim = aim − GetTurretLocalOnMap()`。
- 两遍结束后把 `aim` 写回 `task.aimLocal`。
- **`ApplyMotionModel` 本身不更新 `angel`/`distance`/`position`**;调用方必须紧跟
  `RefreshSolution`。

### 7.4 `FlightSecondsFor(task, distanceKm)`(private static)

装药 = `chargeCount ∈ [1,6] ? chargeCount : Clamp(Ceil(distanceKm/5), 1, 6)`(规划前
用最小可行装药估计);查 `TimeToImpactEstimator.TryEstimateSeconds`;查不到退
`distanceKm / 0.4`(d > 0.1 时),再退 30s。**严禁只用平均弹速**——扁平 0.4 km/s 曾把
C1/C2 飞行时间低估近一倍,是"炮弹落在移动目标屁股后面"的系统性根因。

### 7.5 `public Vector3 ShortenedAim(ArtilleryTask task, float rangeKm)`

沿"原点→aimLocal"方向缩短到 rangeKm 的瞄点。**一律用水平口径**:

```csharp
var dir = task.aimLocal - GetTurretLocalOnMap();
dir.z = 0f;
var lenKm = new Vector2(dir.x, dir.y).magnitude * 3.8164f;
if (lenKm < 0.01f) return task.aimLocal;             // 原样返回
var aim = GetTurretLocalOnMap() + dir * (rangeKm / lenKm);   // dir 保持局部单位,比例无量纲
aim.z = task.aimLocal.z;
return aim;                                           // 结果不再夹地图包络
```

**`ShortenedAim` 的结果不夹地图包络**(与 `ApplyMotionModel` 不同)。清膛倾泻弹用(§9)。

### 7.6 prep 视界约定

排队期 45s;执行期 pre-aim 45s、pre-fire 15s、人工等待重调用默认 45s。

## 8. R6 执行期跟踪修正(`Execution/FirePlanExecutor.cs`)

**动机:装填与大幅摇仰角耗时数分钟,漂移主要在这里积累;修正要"晚而小"。**

常量:`TrackRelayIntervalSeconds = 3`、`TrackAzimuthEpsilonDegrees = 0.1`、
`TrackDistanceEpsilonKm = 0.03`、`TrackElevationEpsilonDegrees = 0.05`、
`PreFirePrepSeconds = 15`、`PreFireSignificantErrorKm = 0.05`、`PreAimPrepSeconds = 45`。

### 8.0 存活性谓词(逐阶段不同,是规范的一部分)

| 阶段 | yield 后的存活性判定 |
|---|---|
| 阶段 1 pre-aim(在 `PrepareLocal` 内) | 仅 `if (!IsActive(plan)) yield break;`——**不查 `_current`、不查 `plan.Failed`** |
| 阶段 2 pre-fire | `if (!ReferenceEquals(_current, plan) \|\| !IsActive(plan) \|\| plan.Failed) yield break;` |
| 阶段 3 manual-wait | 同阶段 2 的完整三联,每次 yield 之后都查 |

pre-aim 若加上 `_current == plan`,**每一个与搭档同批的计划都会在装填完成后立刻 `yield break`**
——此时 `_current` 很可能是同批另一门炮的计划。§18.1 对此有对应的例外条款。

### 8.1 解仰角的统一入口

- 内部类 `ElevationSolve { bool Ok; float Elevation; bool Analytic; }`。
- `public static bool TryAnalyticElevation(int charge, float distanceKm, out float elevationDeg)`:
  - 方法入口即 `elevationDeg = float.NaN;`——**所有 `return false` 的路径 out 均为 NaN**
    (这是 public static 方法,外部/未来调用方可见)。
  - `if (charge <= 0 || distanceKm <= 0.01f) return false;`(**`<=`**,0.01 本身也返回 false)。
  - `candidate = distanceKm × 12 / charge`;`if (candidate > 60.01f) return false;`
    (**严格大于**,candidate 恰为 60.01 时仍可解并被 clamp 到 60;超出该装药射程时交给
    物理台和它的错误路径)。
  - 否则 `elevationDeg = Mathf.Min(candidate, 60f); return true;`
  - 这两个边界的**开闭方向不得改动**。
- 协程 `ResolveElevation(plan, result, lockPriority)`:
  - **两条分支的输入必须同源**:一律以 `(plan.Charge, plan.Task.distance)` 为输入
    (刷新后的当前解算距离 + 计划已装/已提交的装药),否则解析解与台解会给出不同答案。
  - 解析解成功即刻返回(不占台、零耗时,`Analytic = true`)。
  - 否则退物理弹道台:`Ballistic.Acquire(lockPriority)` + `WaitUntilFocused` +
    `SetDistance(plan.Task.distance)` / `SetDirection(plan.Task.angel)` / `SetCharge(plan.Charge)` /
    `SetShellType(plan.Task.bulletType)` → `Calculate` → `GetElevation`;
    `Ok = LastCalculationSucceeded && 有限`。

### 8.2 触发条件三联与日志时序

- **触发条件三联**(三个阶段共用):`trackEntityId 非空 || hasMotion || aimAdjusted`。
  跟踪任务在每次刷新前先 `UpdateEntityMotion`。
- **日志与动作的先后**:所有 pre-fire / manual-wait 的方位与仰角修正日志都在发出
  `SetRotation` / `SetElevation` **之前无条件打印**。因此即使随后旋转/摇仰失败或计划中途
  失活,日志也已经出现,且日志里的"新值"是**目标值而非实际达成值**;动作成功与否不影响
  是否打印。

### 8.3 阶段 1 pre-aim(装填完成后、`Progress.Aiming` 大幅摇仰角之前)

`ApplyMotionModel(task, 45)` + `RefreshSolution`;`ResolveElevation`(锁优先级 = 任务
priority);存活检查(§8.0,仅 `!IsActive(plan)`);仰角差 > 0.05° 才采用:更新本次要摇到的
仰角并写回 `task.elevation`,日志
`pre-aim elevation refresh {旧:F2}° -> {新:F2}° (analytic|console)`。
之后 `gun.SetElevation(采用值)`;失败消息用采用值。
(与阶段 2/3 对比:**pre-aim 之后 `gun.LastElevationSucceeded` 为假会 `FailPlan`**。)

### 8.4 阶段 2 pre-fire(粗对齐完成后、进入扳机流程之前)

- **三个 `applied*` 变量的作用域与取值时刻是规范的一部分**:必须在 pre-fire 的三联门
  **之外、且在任何 `ApplyMotionModel`/`RefreshSolution` 之前**无条件就地初始化,
  **无论三联门是否成立**,并且其生命周期**横跨到阶段 3**:

  ```csharp
  var appliedAzimuth  = plan.Azimuth;
  var appliedElevation = plan.Task.elevation > 0f ? plan.Task.elevation : plan.Elevation;  // pre-aim 可能已更新
  var appliedDistance = plan.Task.distance;
  ```

  放进 `if(三联)` 内会让阶段 3(任务在 fire-wait 期间才被 `AdjustTaskAim` 置 `aimAdjusted`
  的场景)拿不到基准值;放在 `RefreshSolution` 之后会让
  `rangeErrorKm = |task.distance − appliedDistance|` 恒为 0(纵向修正永远不触发)。
- 刷新模型(prep 15s)后**换算成预测落点误差,只有显著才碰炮**:显著阈值 =
  `aimAdjusted ? 0.03 km(改瞄是明令,按普通 epsilon 执行) : 0.05 km(约 1/3 HE 杀伤半径)`。
- **横向**:`crossErrorKm = |DeltaAngle(appliedAzimuth, task.angel)| × Deg2Rad × task.distance`
  超阈 → 先打日志,再 `Turret.SetRotation(task.angel, 45f, 取消谓词)`(谓词=计划死/换人/失活),
  仅在 `Turret.LastRotationSucceeded` 为真时才 `appliedAzimuth = task.angel`。
- **纵向**:`|task.distance − appliedDistance|` 超阈 → `ResolveElevation`(任务 priority);
  `Ok` 时先更新 `appliedDistance = task.distance`;新旧仰角差 > 0.05° 才打日志 +
  `SetElevation`(用 `ElevationTimeoutSeconds`),且**仅在 `gun.LastElevationSucceeded` 为真时**
  才更新 `appliedElevation` 并写回 `task.elevation`,失败则两者都保持原值。
- **pre-fire 阶段的仰角/方位修正失败不得调用 `FailPlan`、不得把计划标失败**;修正失败仅放弃
  本次修正,流程继续进入扳机阶段。(与阶段 1 pre-aim 形成对比。)
- 每步 yield 后都做完整三联存活检查,失活直接 `yield break`。

### 8.5 阶段 3 人工击发等待重调(fire-wait 循环内,仅**手动模式**即无 autoFire 截止时间)

- **节拍相位**:`nextRelay` 在**进入 fire-wait 等待循环之前**初始化为
  `FcsRuntimeClock.Now + TrackRelayIntervalSeconds`(即进入等待后 3 秒才第一次重调,
  **不是**进入即先重调一次);且 `nextRelay` 在重调块**开头**就重新置为
  `FcsRuntimeClock.Now + TrackRelayIntervalSeconds`(节拍从块开始计,而不是从重调完成计,
  重调耗时被计入间隔内)。
- 重调块内:跟踪任务先 `UpdateEntityMotion`;刷新模型(默认 prep 45s)+ `RefreshSolution`。
- **方位**:差 > 0.1° → 打日志 + 重转;`if (Turret.LastRotationSucceeded) appliedAzimuth = task.angel;`
  失败则保持旧值(下个 3s 周期会再试)。
- **仰角**:距离差 > 0.03 km → `ResolveElevation`(**锁优先级 10**,让路给新任务规划);
  `Ok` 后**先更新 `appliedDistance = task.distance`**(即使随后因仰角差 ≤ 0.05° 不摇炮也更新);
  再在仰角差 > 0.05° 时打日志 + `SetElevation`,且**仅在 `gun.LastElevationSucceeded` 为真时**
  才更新 `appliedElevation` 与写回 `task.elevation`。
- **任一动作失败均不判定计划失败。** 玩家决定何时击发,炮必须一直跟到扳机落下。

### 8.6 Acquire 换带优先级重载

执行器内 `Requisition.Acquire(task.priority)`、`Trigger.Acquire(task.priority)`
(含 follower 路径 `follower.Task.priority`);FirePlanner 的台解 `Ballistic.Acquire(task.priority)`。

## 9. R7 装药失败恢复(`FirePlanExecutor.FailPlan` 前拦截)

**拦截点位置**:恢复钩子插在 `if (plan.CompletionHandled) return;` **之后**、
`plan.Failed = true` **之前**:

```csharp
if (plan.CompletionHandled)
    return;

if (TryRecoverPowderFailure(plan, reason))
    return;

plan.Failed = true;
…
```

已经完成结算的计划绝不进入恢复路径(否则会把已出膛/已收尾的任务重新入队)。

`loadRetryCount` 是**两条恢复路径共用的同一个计数器**(commit 不符也会 +1,并影响供药机
路径的 `< 2` 判定)。

- **commit 不符**(正则 `powder commit mismatch: expected C(\d+), physical C(\d+)`,
  取 physical ≥ 1):`loadRetryCount ≥ 3` → 放弃(真失败);否则 +1,`DetachForRequeue`:
  - 射程够(`task.distance ≤ physical × 5 + 0.01`):任务直接重入队,下轮以实际装药重解
    开火。警告日志 `committed C{m} still reaches {d:F2}km — requeued to fire on the actual charge`。
  - 射程不够:**清膛倾泻弹**——新任务 `{ bulletType = 原弹种, priority = min(100, 原+5),
    hasAimPoint = true, aimLocal = ShortenedAim(原任务, physical × 5 × 0.9) }`,
    `RefreshSolution` 后入队(拿到 serial),警告日志说明原委(附录 C);原任务随后重入队
    等一次全新装填。**背景:膛内的弹只能打出去,这发就近砸在原方位线上换清膛。**
- **供药机瞬断**(reason 含 `powder dispenser`,`loadRetryCount < 2`):先 `task.loadRetryCount++`,
  `DetachForRequeue`,再打警告
  `transient dispenser failure, retry {task.loadRetryCount}/2 — requeued`
  ——**`{n}` 是自增后的值**,故第一次输出 `retry 1/2`、第二次 `retry 2/2`
  (`loadRetryCount` 达到 2 后不再恢复)。
  (实测:供药机没坏,是补货窗口;有界重试即可,严禁加"跳过装药"的补偿逻辑。)
- `DetachForRequeue(plan, reason)`:plan 标 Failed(带 reason);`CancelPreparation`;
  fireWaitOwner → `ClearAllFireWait`;
  `if (ReferenceEquals(_current, plan)) _current = null; if (ReferenceEquals(_next, plan)) _next = null;`;
  `ReleaseGunSlot(plan, notify:true)`;任务复位 Pending/清 reason/清 hint。

  **与 §5.5 抢占清理的两点差别**:
  1. `notify:true`,立即触发重规划(抢占是 `notify:false`);
  2. **显式清 `_current`/`_next`**——抢占路径不做这一步。抢占虽把 `_current` 排除在候选
     之外,却**没有**排除 `_next`,因此被抢占的计划可能仍留在 `_next` 引用里。

## 10. R8 排队有效期

- `validForSeconds > 0` 的任务,`FcsRuntimeClock.Now − firstEnqueuedAt > validForSeconds`
  且仍在等待队列 → 自动撤销:progress = Failed,出队,`RecordTaskResult`(外部能从
  RecentTasks 读到原因),警告日志。**已上炮的任务永不过期**。
- **文案中的 `{n}` 一律取 `task.validForSeconds`(配置的窗口长度),不是实测经过时间**
  ——过期检查有 1s 节流 + 规划轮触发,实际时长通常比 `validForSeconds` 大 0~1s 以上,
  `:F0` 后可见差异:

  ```csharp
  task.failureReason = $"时效已过: 入队{task.validForSeconds:F0}秒仍未上炮, 时敏任务自动撤销";
  MelonLogger.Warning($"[FCS Dispatch] #{task.serial} expired after {task.validForSeconds:F0}s in queue; auto-cancelled");
  ```

- 检查点两处:
  1. 规划轮扫描每个任务时——`if (TryExpireTask(task)) continue;` 直接 continue:
     **既不调用 `Planner.BuildEligibility`,也不加入 `planningResults`**,因此
     `[FCS Dispatch] planning round deferred {n} pending task(s)` 的 n **只统计真正做过
     资格评估的任务**。
  2. `TaskDispatcher.SweepExpiredTasks()`(public,内部 1s 节流)由 `FSC.Update` 每帧调用,
     保证无规划轮时也会到期。

## 11. R9 炮击顺序规划(`TaskDispatcher.PlanEngagementOrder`)

**动机:两发之间炮塔方位/俯仰并行运动,换位耗时 = max(Δ方位/4, Δ俯仰/2) 秒(Chebyshev
度量)。队列顺序应最小化总换位时间,同时严格尊重优先级。**

- 每个规划轮、队列 ≥ 2 时运行(诸元刷新之后)。
- **换位耗时函数(方位差必须取最短弧)**:

  ```csharp
  TransitionSeconds(fromBrg, fromElev, toBrg, toElev) = Mathf.Max(
      Mathf.Abs(Mathf.DeltaAngle(fromBrg, toBrg)) / FireReadyEstimator.AzimuthSlewDegreesPerSecond,
      Mathf.Abs(fromElev - toElev)                 / FireReadyEstimator.ElevationSlewDegreesPerSecond);
  ```

  方位差取绕行**最短弧**(归一化到 ±180°),俯仰差取普通绝对差、**不环绕**。
  按字面 `|a−b|` 实现会把 350°→10° 记成 340/4=85s(正确是 20/4=5s),带内 DP/贪心的
  最优序列会完全不同。
- **"带"(band)的定义**:`_taskQueue.GroupBy(t => t.priority).OrderByDescending(g => g.Key)`
  ——**每个不同的 priority 数值各成一带**,按 priority 数值降序处理,**不做任何区间归并**
  (不是"紧急带/普通带"、不是按十位分档)。带序是**硬外层顺序**。
- 带内:任务数 ≤ 10 用 Held-Karp 开路 DP 精确求解,更大退化为最近邻贪心(同度量)。
  带内候选先 `OrderBy(serial)` 保证确定性。
- 光标:起始方位 = `−snapshot.CurrentAzimuth`(物理角→方位角取反),起始俯仰 =
  空闲侧炮管实际俯仰(仅一侧空闲用那侧;两侧同态取平均)。逐带滚动:上一带末任务的
  方位/估计俯仰作为下一带起点。
- 排队期仰角未解算,用线性模型估计:装药 = `MaxChargeEnabled ? 6 :
  BallisticCalculator.MinimumCharge(distance)`(≤0 时取 6),`min(d × 12 / 装药, 60)`。
- **"估计总换位秒数"的定义**:每带的 `pathSeconds` = 「起始光标 → 本带第一个任务」这一跳
  **加上**各相邻任务之间的 `TransitionSeconds` 之和(**开路,不回到起点**);
  `totalSeconds` = 所有带 `pathSeconds` 之和。(精确解里 `dp[1 << j, j]` 的初值就是
  `cost[start, j]`;贪心里第一次循环也累加了起始跳。只累加任务之间的跳会系统性偏小。)
- 顺序变化时**重建队列本体**(HUD、外部快照、matcher 平局裁决全部跟随同一顺序),
  日志列出序列与估计总换位秒数(附录 C)。不变时零副作用。
- HUD 队列标题注明这就是计划炮击顺序:`等待队列：{n}（计划炮击顺序）` /
  `Pending: {n} (planned engagement order)`。

## 12. R10 唯一编号与 HUD(`FcsWindow.cs` 等)

- 全部日志、状态文本、HUD 用 `#{serial}` 称呼任务;`T{targetId}` 全面退役(替换规则见 §5.2)。
  `FirePlan.Label = "{Side} #{serial} {Shell.DisplayName()} C{Charge}"`。
- **槽位标签是纯位置常量**:`var slot = side == "Left" ? "T9" : "T10";`,**无条件**插在
  `#{serial}` 之前。它**不依赖**地图上是否真的存在 9/10 号标记 token(§13 的
  `SetGunTargetMarker` 在该地图上可能是空操作),也**不依赖** `task.hasAimPoint`。
  标记内部 id 永不在 HUD 显示。
- **HUD 文本双语必须两侧完全对称**(§18.4);中文串一律调 `MotionSuffix(true)`、英文串一律调
  `MotionSuffix(false)`;两行的前导两个半角空格保留:

  | 行 | 中文 | 英文 |
  |---|---|---|
  | 炮行第 1 行 | `{炮名}：{槽位} #{serial} {弹种} · {进度} · {n:F0}秒`(全角冒号 `：`) | `{gunName}: {slot} #{serial} {Shell} · {Progress} · {n:F0}s` |
  | 炮行第 2 行 | 追加 `MotionSuffix(true)` | 追加 `MotionSuffix(false)` |
  | 队列行 | `  #{serial} P{priority} {弹种} · 打击 {网格} · 距离 {d:F2}km · 方位 {b:F1}°{MotionSuffix(true)}` | `  #{serial} P{priority} {Shell.DisplayName()} · Impact {grid} · Range {d:F2}km · Az {b:F1}°{MotionSuffix(false)}` |

  英文串**不得**保留 baseline 的 `T{targetId}`,`P{priority}` 也**不得**只放在一侧。

## 13. R11 FCS 拥有的炮位瞄点标记(T9/T10)

- **T9 = 左炮当前任务瞄点,T10 = 右炮**;T1–T8 完全归玩家,永不触碰——这条纪律由**调用点**
  保证,不由方法自身保证(见下)。
- `FSC` 绑定成功后启动协程 `GunTargetMarkerLoop`,其形态是**无条件 `while(true)`,等待在前**:

  ```csharp
  while (true)
  {
      yield return FcsRuntimeClock.WaitForSeconds(0.5f);
      MapTable.SetGunTargetMarker(9,  ActiveAim(LeftTask));
      MapTable.SetGunTargetMarker(10, ActiveAim(RightTask));
  }
  ```

  即绑定后第一次写标记发生在 **0.5s 之后而不是立即**;循环体内**不做任何 IsBound/存活性
  检查,也不会自行 `yield break`**,其生命周期完全由 `TrackCoroutine` 在解绑/F9 时统一停止。
  照 §18.1 字面加一个 `if (!IsBound) yield break;` 会使标记循环在任一次瞬时未绑定后**永久
  停止**,T9/T10 再不更新。§18.1 对此有例外条款。
- `ActiveAim`:任务存在、`hasAimPoint`、progress 不是 Finished/Failed → `aimLocal`,否则 null。
- `MapTable.SetGunTargetMarker(int id, Vector3? aimLocal)`:
  - **方法自身不做任何 id 白名单**——传什么 id 就移动 `artilleries[id]`;"只碰 9/10"完全由
    调用方 `GunTargetMarkerLoop` 保证。**不要**在方法内加 `id is 9 or 10` 的拒绝/断言
    (对现有调用无影响,但公开方法语义变了)。
  - 空操作守卫是**两个条件**:`if (!artilleries.TryGetValue(id, out var marker) || marker == null) return;`
    ——标记 id 不存在(该图无 9/10 号 token)**或取到的 marker 为 null**(场景重载后 Il2Cpp
    指针失效)时都静默返回。
  - `aimLocal` 为 null → **不动**(击发后标记留在原计划落点,正是"在途炮弹落在哪"的可视指示,
    永不归位)。
  - 移动只改 x/y,保留标记自身 z;位移平方 > 1e-6 才写。

## 14. R12 改瞄(`AdjustTaskAim`)与 R13 取消

### 14.1 `FSC.AdjustTaskAim(int serial, float localX, float localY) → string`(反射契约)

- 依次查左炮任务、右炮任务(progress 非 Finished/Failed,`onGun:true`)、等待队列
  (`onGun:false`);都没有 → `no adjustable task #{serial} — 不在等待队列也不在炮位上(已出膛/已完成/已清除)`。
- **返回值的机器可读前缀是契约**:成功一律以**小写 `ok`** 开头(§17;外部桥用
  `result.StartsWith("ok")` 判成功);失败以其它前缀(`rejected:` / `no adjustable task` /
  `map surface unbound`);**永不返回 null**。

### 14.2 `MapTable.AdjustAim(task, x, y, bool onGun) → string`

- mapSurface 未绑 → `map surface unbound`。
- 新点 x/y 用 §2 的**局部单位**包络常量夹取,z 沿用 `task.aimLocal.z`。
- `onGun && chargeCount > 0`:装药已固定,**距离口径用 Vector3 完整模长(含 z)**:

  ```csharp
  var newDistance = (aim - GetTurretLocalOnMap()).magnitude * 3.8164f;   // Vector3,含 z
  var maxRange    = task.chargeCount * 5f;
  if (newDistance > maxRange + 0.01f) → 拒绝
  ```

  **本处距离口径刻意与 §7(`ApplyMotionModel` / `ShortenedAim` 的水平口径 `Vector2`)不同**,
  这是忠实旧行为的有意保留:炮塔棋子的 `localPosition.z` 一般与 `task.aimLocal.z` 不等,
  故此处算出的距离系统性略大于水平距离,边界附近会出现"水平口径通过、三维口径拒绝"。
  实现者不得为"一致性"擅自改成水平口径。
  拒绝文案:`rejected: 新距离{d:F2}km超出已装装药C{n}射程{r:F1}km — 该任务装药已固定, 需cancel后重排`。
- 通过:清空运动模型(`trackEntityId` 置空 / `hasMotion` = false / `trackingLost` = false)
  ——**改瞄是显式静态覆盖**;`hasAimPoint = true; aimLocal = 新点; aimAdjusted = true`;
  `RefreshSolution(task)`;**日志在 `RefreshSolution` 之后打印**(`b`/`d` 用刷新后的
  `task.angel`/`task.distance`,`{progress}` 是 `task.progress` 的枚举 `ToString()`),
  格式见附录 C;返回
  `ok: #{serial} 已改瞄 -> 方位{b:F1}°, 距离{d:F2}km (当前阶段{progress})`。
- **非阻塞契约**:执行流程永不等待改瞄;新点由 §8 三阶段在各自下一遍拾取
  (`aimAdjusted` 把三联门对静态任务打开)。auto-fire 下 WaitingForFire 阶段可能已来不及。

### 14.3 `FSC.CancelPendingTask(int serial) → string?` → `TaskDispatcher.CancelPendingBySerial`

- 仅等待队列(执行中交给抢占机制);找到则出队、`progress = Failed`、
  `failureReason = "cancelled by commander"`,返回 `#{serial} {弹种} brg {b:F1} dist {d:F2}km`;
  **没有找到返回 null**(返回类型必须是 `string?`;外部严格区分 null 与非 null,见 §17)。
- **【相对旧实现的有意变更】被取消的任务必须调用 `RecordTaskResult`,从而以
  `progress = Failed`、`failureReason = "cancelled by commander"` 进入 `RecentTasks`。**

  动机:外部桥判定"炮弹是否出膛"的唯一依据就是 `RecentTasks`——一个 serial 从活跃集合
  (`LeftTask`/`RightTask`/`QueueCan`)里消失后,若 `RecentOutcomes` 里查不到 `Failed` 记录,
  桥就断定"弹已出膛",记进在途炮弹并给 agent 发 `shell_fired` 事件,再按队列纪律把该目标
  锁死 150s。旧行为(取消**不**进 RecentTasks)会让指挥官每取消一个任务,agent 就收到一条
  **假的"炮弹出膛…等待弹着"**。因此本规格要求取消也走 `RecordTaskResult`。
- 取消**仍不计入失败统计**(`FailedTaskCount` 不因取消递增);`RecordTaskResult` 只负责把
  记录推进 `RecentTasks` 环形缓冲。

## 15. R14 外部买卡通道(征用台)

### 15.1 请求对象 `Infrastructure.ConsoleCardRequest`

`public sealed class ConsoleCardRequest`,位于 `IronNestFCS.Logic.Infrastructure` 命名空间、
与 internal 的 `SharedConsoleCoordinator` 同文件。五个成员全部是**公开可变字段(不是属性)**,
默认值逐字如下:

```csharp
public string  CardId      = "";      // 空串,非 null
public float?  BearingDeg  = null;
public float?  DistanceKm  = null;
public string? StartGrid   = null;
public int     Priority    = 50;
```

### 15.2 `SharedConsoleCoordinator`

- `EnqueueCardRequest(request)` 的**判定顺序**是:先 `_cardRequests.Add(request);`,
  再 `if (!_draining) _fcs.TrackCoroutine(DrainCardRequests());`——事件驱动,无常驻轮询、
  无入队延迟。
- **`_draining` 必须是 `DrainCardRequests()` 的第一条语句、在任何 `yield` 之前**同步置位
  (MelonCoroutines 启动时同步跑完首段),因此同一帧内连续两次 `EnqueueCardRequest`
  只会踢起一个 drain。若置位放到首个 yield 之后,会并发跑出两条 drain 协程、双重占征用锁。
- **drain 的取件时机**:每轮迭代**开头一次性 Pop**(从列表中移除)当前最高优先级请求
  (`while (PopHighestPriorityRequest() is { } request)`),此后该请求**不可被抢占**——
  在 `WaitUntilFocused` 或等锁期间到达的更高优先级请求**不会**顶替已出队的那一个,
  只会在下一轮迭代抢先(P100 中途插队仍会在下一轮先执行)。Pop 用**严格 `>`** 比较以保证
  同级 FIFO;列表空时返回 null 结束 drain。被 Pop 出来的请求在 F9 停协程时**直接丢失**
  (不回填列表)。
- 每轮:`WaitUntilFocused` → 日志 → `Requisition.Acquire(request.Priority)` try/finally →
  `WaitUntilFocused` → `PurchaseDeck.BuyCardById(...)`,回调里
  `LastCardRequestResult = $"{CardId}: {结果} @{FcsRuntimeClock.Now:F0}"` + 日志。
  finally 复位 drain 标志(F9 停协程时也复位)。
- `public string LastCardRequestResult { get; private set; } = "";`
  ——**外部只读、仅 drain 回调内部赋值**;首次请求完成前外部读到的是**空串而非 null**
  (外部轮询逻辑依赖这一点区分"尚无结果")。串格式本身也是契约,见 §17。
- `Reset()` 追加:清请求列表、清 drain 标志(重绑兜底)。
- `ReplenishPowderLoop` 的征用台占用改 `Requisition.Acquire(20)`(背景补货永远让路)。

### 15.3 `PurchaseDeck` 物理核

- 抽公共物理核:
  - `InsertCard(card)`:聚焦 → 摆卡槽 → `card.GetComponent<DraggableItem>()?.MoveToSlot()`
    (**空条件调用**,缺组件时静默跳过而非抛异常)→ 0.5s → 聚焦。
  - `PressBuy()`:点买 → 2s。
- `BuyShell` 重构复用,**行为不变**(BuyShell 中左右拨盘 0/1)。
- **`BuyPowders` 的行为确实变了**:baseline 是 聚焦→摆卡→MoveToSlot→0.5s→**直接点买**;
  复用 `InsertCard` 后在 0.5s 与点买之间**多了一次 `WaitUntilFocused()`**。这是**有意的
  行为变更**,不要为"保持不变"而给 `BuyPowders` 走特例路径。
- `NormalizeCardId`(§2 规范原文)**唯一一处**——扫描与所有购买路径共用,否则一个名字
  扫得到买不到。

### 15.4 `BuyCardById`

两个 public 实例重载,均返回 `IEnumerator`;旧签名逐字转发:

```csharp
public IEnumerator BuyCardById(string cardId, float? bearingDeg, string? startGrid, Action<string> done)
    => BuyCardById(cardId, bearingDeg, null, startGrid, done);

public IEnumerator BuyCardById(string cardId, float? bearingDeg, float? distanceKm, string? startGrid, Action<string> done)
```

调用方必须已持征用锁。流程:

1. 征用台未绑 → done(`requisition console unbound`)。
2. **扫描**:遍历 `_requisitionConsole.GetComponentsInChildren<PunchcardRuntime>(true)`
   (**含未激活**);`CurrentDefinition?.ID` 读取包 try/catch;`IsNullOrWhiteSpace` 的 id
   **直接跳过、不进 available**;其余 id 以**原始未归一化形式**按遍历顺序追加进 available
   (**不去重**)。匹配用原名或规范化名、`OrdinalIgnoreCase`。
   **命中后不 break**,继续扫完,故同名多卡时**最后一个命中者**成为选中卡;选中的
   Transform 是 `runtime.transform` 本身。
   - 没有命中 → done(`card '{cardId}' not found; available [{string.Join(", ", available)}]`)
     (分隔符是**逗号 + 空格**);
   - 命中但无 `DraggableItem` → done(`card has no DraggableItem`)。
3. `InsertCard`。
4. **有 bearing**:等 `DialOdometerPunchcardBridge` 出现。
   - **查找是场景全局的**:`UnityEngine.Object.FindObjectOfType<DialOdometerPunchcardBridge>()`
     ——**不以征用台为根**(控件是动态生成的,限定到 `_requisitionConsole` 子树会查不到)。
   - **两套时基**:截止时刻 `var waitUntil = Time.unscaledTime + 4f;`,循环条件
     `while (bridge == null && Time.unscaledTime < waitUntil)`(非缩放实时);
     步进等待是 `FcsRuntimeClock.WaitForSeconds(0.25f)`(**任务时钟**,失焦/暂停时不推进)。
   - 循环**先探测再等待**(桥已存在时零等待),超时判定在探测之后。
   - 超时 → done(`card accepted but no bearing controls appeared (not a recon card?)`)。
   - 拨盘写值:`bridge.bearingDial?.SetDialValue(bearingDeg)` 直接写**原始度数**;
     dial 引用为 null 时**跳过物理拨盘、不报错**,直接进入等待 + 读值校验 + 内部设置器补偿。
   - → 0.3s → 读值校验:

     ```csharp
     var applied = float.NaN;
     try { applied = bridge.Bearing; } catch { }
     if (float.IsNaN(applied) || Mathf.Abs(Mathf.DeltaAngle(applied, bearing)) > 1f)
     {
         try { bridge.SetBearingInternal(bearing, true); bridge.ForceRefreshAll(); applied = bridge.Bearing; } catch { }
     }
     MelonLogger.Msg($"[FCS] card bearing requested {bearing:F1} applied {applied:F1}");
     ```

     - **方位校验用 `Mathf.DeltaAngle`**(环绕安全),否则请求 359° 而台面读回 -1° 会被误判;
     - **读失败(NaN)与超差同样触发补偿路径**;
     - 补偿块内在 `SetBearingInternal(v, true)` + `ForceRefreshAll()` 之后**重新读一次**
       `bridge.Bearing` 覆盖 `applied`,整块再包一层 try/catch(抛异常则 applied 保持先前值/NaN);
     - 日志**无条件打印**(不论是否发生补偿),打印的是**补偿后的** `applied`。
   - 再 0.3s。
   - **有 distance:同构**(`bridge.distanceDial?.SetDialValue(distanceKm)` 写**原始 km**、
     读 `bridge.Distance`、补偿 `SetDistanceInternal(distance, true)` + `ForceRefreshAll()` + 重读、
     无条件打日志),但**校验用普通差值** `Mathf.Abs(appliedDistance - distance) > 0.05f`
     (距离不环绕)。
   - 拨盘写值**不做任何 0–1 归一化或量程映射**。
   - 只给 distance 不给 bearing → done(`distanceKm given without bearingDeg — the
     distance dial lives on the bearing console controls (give both)`)。
5. **有 startGrid**(进入条件是 `!string.IsNullOrWhiteSpace(startGrid)`):
   - 正则匹配的是 **`startGrid.Trim()`**:`^([A-Za-z])\s*(\d{1,2})$`(前后带空格的 `" P4 "`
     应当被接受);不匹配 → done(`cannot parse startGrid '{startGrid}' (expected like 'P4')`)
     ——**回显的是原始未 Trim 的字符串**。
   - 字母组在传给 `SetFlapDialSymbol` 前经 `ToUpperInvariant()`,数字组**原样传入**。
   - 找两台 split-flap:`UnityEngine.Object.FindObjectsOfType<DialToSplitFlipDisplayBinder>()`
     ——同样是**场景全局**查找,且**不含未激活对象**(不传 `includeInactive`)。
     对每个 binder 取 `binder.transform.parent`(parent 为 null 时视作空串 `""`),判定为
     **else-if 排他**且**后命中者覆盖先命中者(不 break)**:

     ```csharp
     if (parentName.Contains("Location L")) letterBinder = binder;
     else if (parentName.Contains("Location N")) numberBinder = binder;
     ```

     (父名同时含两者时只归字母盘。)缺任一 →
     done(`start-grid dials not found (card may not support a start position)`)。
   - `SetFlapDialSymbol(binder, symbol)`:
     - 查表是对**整个 symbol 字符串**做 `symbols.IndexOf(symbol, StringComparison.OrdinalIgnoreCase)`
       的**子串搜索**,返回首次出现的**字符下标**。注意数字盘的 symbol 可能是两位数
       (正则 `\d{1,2}`):对 `symbols = "0123456789"`、`symbol = "12"` 会返回 1(字符 `'1'`
       的位置),而**不是**失败、也**不是**逐字符查找。`orderedSymbols` 为 null 时按空串
       `""` 处理。
     - 没有找到 → 报 `symbol '{s}' not in [{表}]`。
     - 线性映射 `value = min + (max−min) × i / (len−1)`(单字符表取 min),用
       `MapDialValueToSymbolIndex` 验证,不符则按方向以 `(max−min)/(len×4)` 步长
       nudge ≤ 5 次;`dial?.SetDialValue(value)`,返回 `ok`。
   - `[FCS] card start grid '{startGrid}': letter={r1}, number={r2}` 日志在**失败判定之前**
     打印(回显**原始未 Trim** 串),因此成功和失败两种情况都会出现该行;随后失败才
     done(`start grid failed: letter={..}, number={..}`) 并 `yield break`。
   - 成功 0.4s。
6. `PressBuy` → done(`ok`)。

### 15.5 `FSC.RequestConsoleCard` 四个重载(**签名精确**,§17)

```csharp
public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing);                              // ≡ P50
public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing, int priority);
public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing, int priority, string? startGrid);
public string RequestConsoleCard(string cardId, float bearingDeg, bool hasBearing, float distanceKm, bool hasDistance, int priority, string? startGrid);
```

- **布尔位到可空字段的映射是行为关键**:

  ```csharp
  BearingDeg = hasBearing  ? bearingDeg : null,
  DistanceKm = hasDistance ? distanceKm : null,
  ```

  `hasBearing = false` 时 `BearingDeg` **必须为 null**;`hasDistance = false` 时 `DistanceKm`
  **必须为 null**;数值参数本身的值在对应布尔为 false 时一律忽略(转发重载传 `0f, false`)。
  若无条件写 `BearingDeg = bearingDeg`,则对任何非侦察卡都会进入 §15.4 步骤 4 的
  "等 `DialOdometerPunchcardBridge` ≤4s"路径并最终 done
  (`card accepted but no bearing controls appeared (not a recon card?)`),**普通买卡全部失败**。
- **`startGrid` 的空白归一化**:两处用同一判据 `string.IsNullOrWhiteSpace(startGrid)`:
  (a) 入列时 `StartGrid = string.IsNullOrWhiteSpace(startGrid) ? null : startGrid;`
  (纯空白视同未给,不会走 §15.4 步骤 5);(b) 返回串的 `, start {grid}` 片段同样按
  `IsNullOrWhiteSpace` 判断是否拼接,**不是**按 `!= null`。只判 null 会让 `""` / `" "`
  进入起始网格分支被正则拒绝、整张卡买不成,同时返回串出现尾随的 `, start `。
- 组请求入列并返回(**成功入列时绝不返回 null**,见 §17):

  ```csharp
  return $"queued to FCS console coordinator (P{priority}"
       + (hasDistance ? $", dist {distanceKm:F1}km" : "")
       + (string.IsNullOrWhiteSpace(startGrid) ? "" : $", start {startGrid}") + ")";
  ```

- `public string ConsoleCardRequestResult => SharedResources.LastCardRequestResult;`

## 16. R15 规划轮内务(baseline 行为微调)

- **规划扫描必须遍历 `_taskQueue.ToArray()` 的副本**——理由不是"扫描无 yield",而是
  **扫描第一步的 `TryExpireTask(task)` 内部会 `RemovePendingTask(task)`,在遍历期间真实
  修改 `_taskQueue`**。副本是唯一防 `InvalidOperationException` 的手段;直接
  `foreach (var task in _taskQueue)` 一旦有任务过期就会在规划轮里抛异常。
  `SweepExpiredTasks()` 同理必须遍历 `_taskQueue.ToArray()`。
  副本遍历取代 baseline 的 FindNextUnattempted/attempted 集合;
  "deferred N pending task(s)" 日志以规划结果数计(过期任务不计入,见 §10)。
- 左右侧重试规则表驱动(`(bit, LeftRight, GunSide)` 静态表),消除左右复制粘贴;
  行为与 baseline 完全一致。
- csproj:追加 `UnityEngine.UIModule` 引用,**完整元素逐字如下**(漏 `<Private>false</Private>`
  会把该 DLL 复制到输出目录,在 MelonLoader mod 打包中是可观测差异):

  ```xml
  <Reference Include="UnityEngine.UIModule">
      <HintPath>$(GameDir)\MelonLoader\Il2CppAssemblies\UnityEngine.UIModule.dll</HintPath>
      <Private>false</Private>
  </Reference>
  ```

## 17. 兼容性契约(§9;外部反射面——名字/签名逐字冻结)

外部 mod(IronNestAgentBridge)通过反射访问以下成员;任何改名/改签名/改成员种类
(字段 ↔ 属性)都是破坏性变更,且**绝大多数是静默的**——反射查不到就返回 default,
没有异常、没有日志。

### 17.1 实例解析链(桥拿到 FSC 的唯一路径)

`FcsGateway.ResolveFsc` 依次走:

1. `MelonMod.RegisteredMelons` 里 `Info.Name == "IronNestFCS Smart"` 的宿主
   ——**MelonInfo 名字串逐字冻结为 `"IronNestFCS Smart"`**;
2. 该宿主(FcsHostMod)的**私有字段 `_reloader`**;
3. reloader(LogicReloader)的**公有属性 `Current`**(必须是**属性**,桥用 `GetProperty`);
4. `Current` 返回对象(FcsModule)的**私有字段 `_fcs`**,其值必须是 `FSC` 实例。

`IronNestFCS.Logic.FcsModule._fcs`(`private FSC? _fcs`)正在 clean-room 重写范围内:
改名(如 `_core`/`_instance`)后桥所有调用静默退化成 "FCS instance unavailable"。桥用
`BindingFlags.NonPublic` 读取,所以**私有可保持私有,但名字是接口**。

### 17.2 类型全名与程序集位置

- `IronNestFCS.Logic.FSC`
- `IronNestFCS.Logic.FCS.ArtilleryTask`
- `IronNestFCS.Logic.FCS.BulletType`

**程序集位置同样是契约**:桥用 `var logicAsm = fsc.GetType().Assembly;` 再
`logicAsm.GetType("IronNestFCS.Logic.FCS.ArtilleryTask")` ——即
**`ArtilleryTask` 与 `BulletType` 必须与 `FSC` 同在 `IronNestFCS.Logic.dll` 内**。
仓库里已存在的 `IronNestFCS.Abstractions` 项目**不得**接收这两个类型:迁过去后
`GetType` 返回 null,桥所有入队路径返回
`FCS internal types not found (incompatible FCS version?)`,而命名空间名字看上去完全没变。

### 17.3 成员种类:属性 vs 字段(精确,不可互换)

| 成员 | 所在类型 | 必须是 | 桥的取值方式 |
|---|---|---|---|
| `LeftTask`、`RightTask` | FSC | **属性** | `GetProperty`,**无字段回退** |
| `QueueCan`、`RecentTasks` | FSC | **属性** | `GetProperty`,无回退 |
| `ConsoleCardRequestResult` | FSC | **属性** | `GetProperty`,无回退 |
| `IsBound`、`PendingCount`、`AutoFireEnabled`、`MaxChargeEnabled` | FSC | **属性** | `GetProperty` |
| `CompletedTaskCount`、`SuccessfulTaskCount`、`FailedTaskCount` | FSC | **`int` 属性** | `GetProperty` |
| `SharedResources` | FSC | 属性**或**字段均可 | `GetRequisitionLock` 是唯一有 property-或-field 双路的 |
| `MapTable` | FSC | **字段** | 桥只用 `GetField` |
| `Requisition` | SharedResources | **属性** | `GetProperty` |
| `ArtilleryTask` 的全部契约成员 | ArtilleryTask | **公开字段** | 桥对 ArtilleryTask 一律 `GetField` |

把 `QueueCan`/`RecentTasks`/`LeftTask`/`RightTask`/`ConsoleCardRequestResult` 之一做成公开
字段,`GetProperty` 返回 null、`Get<T>` 直接 `return default` ——队列空、任务 null、卡片结果
永不上报,**全部静默**。

`CompletedTaskCount` / `SuccessfulTaskCount` / `FailedTaskCount` 进桥的
`LastFcsSummary`(`"FCS: pending=… done=… fail=…"`)和 agent 快照,三者显式入表,
不再由"等"含糊兜住。

### 17.4 单一重载约束

桥对下列方法用的是**不带参数类型数组的 `GetMethod(name)`**,因此这些成员**必须全程保持
单一重载**(含私有重载——桥的 BindingFlags 带 `NonPublic`):

- `FSC.EnqueueTask`
- `FSC.AdjustTaskAim`
- `FSC.CancelPendingTask`
- `ArtilleryTask.MotionSuffix`
- `MapTable.GetMarkTarget`

`Type.GetMethod(string, …)` 在匹配到一个以上同名方法时抛 `AmbiguousMatchException`。
`EnqueueByBearing`/`EnqueueAimPoint` 里这次调用**不在 try/catch 内**,异常会冒到 fire 工具;
其余在 catch 里则变成静默降级。随手加一个便利重载(如 `EnqueueTask(task, int priority)`
或 `GetMarkTarget(int, bool)`)就会当场炸掉桥。

### 17.5 `CoroutineLock` 的 Acquire 反射歧义(**契约注记**)

桥的 `RequisitionOperator.PurchaseRoutine` 在 `SharedResources.Requisition` 返回的
`CoroutineLock` 上反射调用 `GetMethod("Acquire")` / `GetMethod("Release")`(默认
BindingFlags = `Public|Instance|Static`,**无参数类型数组**),把 `Acquire` 的返回值
`as IEnumerator` 后 `yield return`。

**事实**:`CoroutineLock` 在 baseline 已有两个 `Acquire` 重载,§4 又加两个,共四个。
因此 `GetMethod("Acquire")` **必然抛 `AmbiguousMatchException`**,被桥的
`catch { consoleLock = null; }` 吞掉——**旧桥这条路径从未真正拿到过征用台锁**,与 FCS 自己
的操作台动作可能对撞。

**裁决**:FCS 侧**保留全部四个 `Acquire` 重载**(忠实旧实现),**不为此新增无歧义别名方法**
(如 `AcquireDefault()`)。契约要求**外部改用 `GetMethod("Acquire", Type.EmptyTypes)`**
精确取无参重载。`Release` 必须保持 public 实例、无参、返回 void、单一重载。

### 17.6 `EnqueueTask` 的时序契约

`EnqueueTask(ArtilleryTask)` 是**同步方法**(不是协程、不返回 `IEnumerator`,桥丢弃返回值),
且 **`serial` 必须在 `EnqueueTask` 返回之前同步赋好**:桥的 `EnqueueAimPoint` 在
`Invoke` 之后立刻回读 `task` 的 `serial` 字段并作为句柄返回给 agent
(`_deployedTasks` 的键、`adjust_fire`/`cancel_pending_task` 的唯一寻址方式、出膛判定的比对键)。
若把入队做成"先塞待处理表、下一个规划轮/Update 再编号",桥拿到 -1,整套 serial 簿记
(改瞄、取消、在途炮弹跟踪)全废,而返回值仍是 `"ok"` ——完全静默。

### 17.7 `ArtilleryTask`:公开字段名 + 类型冻结 + 构造

**公开字段名**(反射 `GetField` 直取——**必须是字段不是属性**):
`serial`、`targetId`、`angel`、`distance`、`position`、`bulletType`、`chargeCount`、
`progress`、`failureReason`、`priority`、`hasAimPoint`、`aimLocal`、`trackEntityId`、
`hasMotion`、`motionOriginLocal`、`motionVelLocalPerSec`、`motionT0`、`validForSeconds`;
方法 `MotionSuffix(bool)`。

> `chargeCount` 曾被 §17 旧版漏列:桥的 `DescribeTask` 反射读它拼进任务显示串
> (`"chg {chargeCount}"`),该串进 HUD 摘要、`/state` 快照和 LLM 快照。改成属性同样致命。

**字段类型同样冻结**(反射 `SetValue` 对类型不匹配抛 `ArgumentException`,读取侧则是静默
失败,风险不对称):

| 字段 | 必须的类型 | 依据 |
|---|---|---|
| `serial`、`targetId` | **恰好 `int`** | 桥用 `GetValue(task) as int?`;改成 uint/long/short 会得到 null,`SerialToMarker` 与 `RecentOutcomes` 整个静默失效(出膛判定、改瞄、取消全跟着废) |
| `priority` | `int` 且 **public** | `TrySetPriority` 用默认 BindingFlags,只看 public |
| `chargeCount` | `int` | `DescribeTask` |
| `angel`、`distance`、`motionT0`、`validForSeconds` | `float` | |
| `position`、`aimLocal`、`motionOriginLocal`、`motionVelLocalPerSec` | `UnityEngine.Vector3` | |
| `hasAimPoint`、`hasMotion` | `bool` | |
| `trackEntityId`、`failureReason` | `string`(非 null) | |
| `bulletType` | `BulletType` 枚举 | 见 §17.8 |
| `progress` | 枚举,成员名冻结 | 见 §17.9 |

**构造**:必须保留**公开无参构造函数**——桥用 `Activator.CreateInstance(taskType)` 凭空造任务
(`EnqueueByBearing` 与 `EnqueueAimPoint` 两条主路径)。改成"只能经工厂/带参构造"会让
`Activator` 抛 `MissingMethodException`,fire 工具整条挂掉。新建实例后未显式设置的字段必须是
安全默认值,尤其 `failureReason` 必须是 `""` 而不是 null(`DescribeTask` 用 `Equals(reason, "")`
判有无失败原因)。

### 17.8 `BulletType` 的语义契约

- 必须是**枚举**,且**成员名逐字等于游戏弹种 ID**。
- 桥两头都靠它:入队时 `Enum.Parse(bulletType, shell, ignoreCase: true)` 把 LLM 给的弹种串
  转过去(非枚举类型 `Enum.Parse` 直接抛,被 catch 成 `"unknown shell type"`);读回时
  `bulletType.ToString()` 当弹种名喂给 SurveyBlast 的弹种规格查表和 LLM。
- 桥侧**没有任何归一化**,因此 §2 记的那些怪癖拼写(游戏 `SMOKE` → 枚举 `SMK`;
  游戏 `PCLM` → 枚举 `PLCM`)也一并被冻结。
- 契约:**大小写不敏感可解析(`Enum.Parse(..., ignoreCase: true)`)、`ToString()` 必须与
  成员名往返一致**。

### 17.9 `Progress` 枚举的值契约

- `progress` 必须是**枚举**;桥取 `progress.ToString()` 与字面量 **`"Failed"`** 逐字比较来
  区分"任务失败"与"炮弹出膛"(`dto.RecentOutcomes[serial] = progress == "Failed" ? $"Failed: {reason}" : progress;`)。
- 因此成员名 **`Failed`** 恒为不变式英文:**不能本地化、不能改成 `Aborted`/`Cancelled`、
  不能换成 int 状态码或字符串常量**。比错了不会报错,只会把每一次任务失败都当成一发已出膛
  的炮弹报给 agent。
- 其余成员名(**`Finished`** 等)会原样出现在快照/HUD 文本里,同样视为对外文本并冻结。
- `failureReason` 必须是 `string` 且**非 null**(桥 `as string ?? ""` 兜底,但 `DescribeTask`
  里 `Equals(reason, "")` 的判空只认空串)。

### 17.10 `QueueCan` / `RecentTasks`:快照副本 + 保有量

- 两者**必须返回可安全遍历的快照副本**(`new(_taskQueue)` / `new(_recentTasks)`)。桥在
  `ReadStatus` 与 `TryGetTaskInfo` 里直接 `foreach` 这两个集合,而 §11/§16 的规划轮会在
  同一帧内**重建队列本体**。为省分配把 `QueueCan` 改成返回活队列本身(或 `IEnumerable`
  惰性视图),遍历中途会抛 `InvalidOperationException` ——桥的 `foreach` 全在 try/catch 里,
  后果是 `pendingTasks` **静默变空**,agent 判定"队列里没有这个目标"从而重复排队。
- **`RecentTaskLimit = 20`** 也是契约:桥 2 秒轮询一次,保有量太小会让失败记录在被读到之前
  被挤掉,退化成 §14.3 描述的假"出膛"事件。

### 17.11 返回值语义契约

| 方法 | 契约 |
|---|---|
| `AdjustTaskAim(int, float, float)` | 返回 `string`,**永不 null**;成功以**小写 `"ok"`** 开头(桥用 `result.StartsWith("ok")` 判成功,只有成功才把 `_deployedTasks` 里该 serial 的弹着匹配点更新成新瞄点)。改成 `"OK"`/`"已改瞄"`/`"adjusted"` 会让改瞄照常生效但桥的弹着匹配点仍停在旧坐标,3km 匹配窗口会把这发炮弹错配或超时销账——纯静默偏差。 |
| `CancelPendingTask(int)` | 返回类型必须是 **`string?`**。桥严格区分:**null → `"no pending task with #{serial}"`;非 null → `"cancelled: {返回串}"`**。改成"找不到时返回一句说明串"会让 agent 收到 `"cancelled: no such task"` 这种自相矛盾的回执,并据此认为任务已清掉。 |
| `RequestConsoleCard`(四个重载) | **成功入列后绝不能返回 null**。桥的 `RequestCardPurchase` 在四个重载全找不到时返回 null,`AgentBridgeMod` 用 `viaFcs != null` 判定 FCS 是否接管了买卡;为 null 就回落到桥自己的物理买卡协程 `RequisitionOperator.StartPurchase` ——同一张卡买两次、征用点双扣,且两条路径同时操作征用台。 |
| `ConsoleCardRequestResult` | 串格式本身是契约,见 §17.12。 |

### 17.12 `RequestConsoleCard` 四条参数类型序列(逐字)

桥用 `GetMethod(name, flags, Type[])` **精确匹配**,按**固定顺序**探测,**全部必须是实例方法**
(桥的 BindingFlags 只含 `Instance`,静态版找不到):

1. `(string, float, bool, float, bool, int, string)`
2. `(string, float, bool, int, string)`
3. `(string, float, bool, int)`
4. `(string, float, bool)`

`startGrid` 必须声明为 `string`(可空注解 `string?` 不影响匹配,但不能改成别的类型);
`distanceKm`/`hasDistance` 必须是 `(float, bool)` 而**不是** `float?`。

### 17.13 `ConsoleCardRequestResult` 的串格式契约

- 桥每 2 秒轮询一次,靠 **`cardResult != _lastCardResult` 这一个字符串不等式**判断"有新结果",
  才发 requisition 事件给 agent。
- 因此 §15.2 的 `"{CardId}: {结果} @{FcsRuntimeClock.Now:F0}"` 里那个 **`@时间戳` 是承重的**:
  去掉它以后,连续两次买同一张卡得到同样结果(常见,如同一张 STAR 连买两次都 `ok`)
  第二次就**永远不会上报**,agent 会一直等一个不来的回执。
  **契约:该串每次请求完成都必须与上一次不同**(时间戳/序号)。
- **首个结果产生前必须是空串(或 null)**——桥用 `IsNullOrEmpty` 当"尚无结果";
  §15.2 已规定初值为 `""`。

### 17.14 取消任务的可观测性(**相对旧实现的有意变更**)

桥判定"炮弹是否出膛"的唯一依据是 `RecentTasks`:一个 serial 从活跃集合
(`SerialToMarker` 覆盖的 `LeftTask`/`RightTask`/`QueueCan`)里消失后,若 `RecentOutcomes`
里查不到以 `"Failed"` 开头的记录,桥就断定"弹已出膛",记进在途炮弹并给 agent 发
`shell_fired` 事件;而桥的 `CancelPendingFcsTask` **并不**从 `_deployedTasks` 里删条目。
旧行为(`CancelPendingBySerial` 不调 `RecordTaskResult`)因此让指挥官/LLM 每取消一个任务,
agent 就收到一条**假的"炮弹出膛…等待弹着"**,并按队列纪律把该目标锁死 150s。

**裁决:FCS 侧修正。** `CancelPendingBySerial` **必须调用 `RecordTaskResult`**,使取消的
任务以 `progress = Failed`、`failureReason = "cancelled by commander"` 进入 `RecentTasks`
(见 §14.3)。这是本规格相对旧实现的第 2 项有意变更;取消仍不计入 `FailedTaskCount`。

### 17.15 baseline 既有公开面

`IsBound`、`PendingCount`、`FirePriorityStatusText`、`AutoFireEnabled`、`MaxChargeEnabled`、
`Dispatcher.QueueSnapshot`、`RecordTaskResult` 等保持不变(种类要求见 §17.3)。

## 18. 全局不变量

1. 每个 `yield return` 后重查存活性;取消/换人一律 `yield break`,不得误报失败。
   **两条例外(不是疏漏,是规范)**:
   - **准备阶段例外**:尚未取得共享方位所有权的准备阶段(装填、§8.3 pre-aim)**不得**把
     `_current == plan` 纳入存活性——否则同批搭档的计划会在装填后立刻 `yield break`。
     逐阶段谓词见 §8.0。
   - **`GunTargetMarkerLoop` 例外**(§13):该循环是无条件 `while(true)`,循环体内不做
     存活性/绑定检查、不会自行 `yield break`,生命周期完全交给 `TrackCoroutine`。
2. 所有锁 try/finally;可取消 Acquire 在占锁前最后一刻仍要查取消。
3. 反射契约面(§17)与日志格式(附录 C)是接口,不是实现细节。
4. HUD/玩家可见文本走 `FcsLocalization.T(中, 英)` 双语,**两侧必须完全对称**(§12);
   MelonLog 日志单语(英文为主,既有中文措辞按附录 C 与 §5.2)。
5. 禁止 Update/协程里每帧 FindObjectsOfType;昂贵查找必须缓存(如世界时钟、炮塔棋子)。
   例外:§15.4 的 `DialOdometerPunchcardBridge` / `DialToSplitFlipDisplayBinder` 是买卡流程
   内的一次性查找,允许全局 Find。
6. 空引用防御:Il2Cpp 对象属性访问包 try/catch;场景重载后指针可能失效。异常时的兜底取值
   本身是规范(如 §7.2 的 `visible = false, alive = true`)。
7. **源文件编码**:含非 ASCII 字面量的 `.cs` 一律 UTF-8 with BOM(§0)。

## 附录 A 数值真值表

| 常量 | 值 |
|---|---|
| 仰角公式 | `d(km) × 12 / charge`,cap 60°;`d ≤ 0.01` 或 `charge ≤ 0` 或 `candidate > 60.01` → 无解析解(out = `float.NaN`) |
| 最大射程 | `charge × 5 km` |
| 坐标换算 | `km = local × 3.8164 + offset`;`local = (km − offset) / 3.8164`(先缩放后平移) |
| km 原点偏移 | (10.016, 5.235) |
| 地图包络(km) | x ∈ [-1, 27], y ∈ [-1, 16] |
| 地图包络(**局部单位**,夹取实际用的常量) | `MapLocalMinX = (-1f - 10.016f)/3.8164f`、`MapLocalMaxX = (27f - 10.016f)/3.8164f`、`MapLocalMinY = (-1f - 5.235f)/3.8164f`、`MapLocalMaxY = (16f - 5.235f)/3.8164f` |
| 方位转速 / 俯仰转速 | 4°/s / 2°/s(取自 FireReadyEstimator 常量) |
| TTI(s/km,实测) | C1 4.758869 · C2 3.830061 · C3 2.613011 · C4 1.894451 · C5 1.540442 · C6 1.427168(经 TimeToImpactEstimator 使用) |
| 兜底弹速 | 0.4 km/s(仅当 TTI 表不可用);再退 30s |
| 卡槽位置 | (6.4814, -2.4675, -22.0968) |
| 紧急阈值 | priority ≥ 90(`UrgentPriorityThreshold`) |
| 跟踪常量 | 重调间隔 3s;ε:方位 0.1°、距离 0.03km、仰角 0.05°;pre-fire 显著误差 0.05km(`aimAdjusted` 时 0.03km);prep:排队/pre-aim 45s、pre-fire 15s;提前量上限 3km(局部 `3f/3.8164f`);失联 90s;采样窗 0.5–10s(**dt < 0.5 时 vel 与样本均不动**);速度低通 0.5 |
| 距离口径 | §7 `ApplyMotionModel`/`ShortenedAim` = **水平**(`Vector2`);§14 `AdjustAim` = **三维完整模长**(`Vector3`,含 z)——刻意不同 |
| 恢复上限 | commit 不符 ≤3 次;供药机 ≤2 次(两者**共用** `loadRetryCount`);倾泻弹射程系数 0.9;倾泻弹优先级 +5(≤100) |
| 序列规划 | 精确 DP 上限 10 任务/带;带 = priority 值完全相同的任务集合;标记循环 0.5s(等待在前);过期扫描 1s |
| 买卡时基 | 等桥截止 `Time.unscaledTime + 4f`;步进 `FcsRuntimeClock.WaitForSeconds(0.25f)`;拨盘后 0.3s;起始网格后 0.4s;`InsertCard` 0.5s;`PressBuy` 2s |
| 买卡容差 | 方位 `Mathf.DeltaAngle` > 1°;距离 `Mathf.Abs` > 0.05km;网格 nudge 步长 `(max−min)/(len×4)`,≤5 次 |
| 外部契约量 | `RecentTaskLimit = 20`;MelonInfo 名 `"IronNestFCS Smart"` |
| 源文件编码 | UTF-8 with BOM(含非 ASCII 字面量者);度数符号 U+00B0 `°` |

## 附录 B 锁优先级约定

| 场景 | 优先级 |
|---|---|
| 规划期台解 / 执行期征用·扳机 | 任务 priority(默认 50,紧急 ≥90) |
| 外部买卡请求 | 请求 Priority(紧急转移 100) |
| 火药自动补货 | 20 |
| 人工击发等待期实时重调 | 10 |
| 无参 Acquire() | 50 |

## 附录 C 日志格式表(关键行,`[]` 内为字面)

> 编码纪律:表中所有 `°` 均为 **U+00B0**;旧实现输出的 `掳` 是丢 BOM 后按 GBK 重解码的
> 事故产物,**不要复现**(§0)。部分行按旧实现用 ASCII 字面 `deg` 而非 `°`,已逐行照录。

- `[FCS] firing origin bound to 'Player Turret Piece' local=({x:F3},{y:F3})`
  (对象名由 `MapTable.PlayerTurretPieceName` 插值;仅 Find 成功那一次打印)
- `[FCS] #{serial} solution refreshed: {b0:F1}°/{d0:F2}km -> {b1:F1}°/{d1:F2}km`
- `[FCS Track] {Label}: pre-aim elevation refresh {旧:F2}° -> {新:F2}° (analytic|console)`
- `[FCS Track] {Label}: pre-fire azimuth correction {旧:F2}° -> {新:F2}° (cross error {m:F0}m)`
- `[FCS Track] {Label}: pre-fire elevation correction {旧:F2}° -> {新:F2}° (range error {m:F0}m)`
- `[FCS Track] {Label}: manual-wait azimuth re-lay {旧:F2}° -> {新:F2}°`
- `[FCS Track] {Label}: manual-wait elevation re-lay {旧:F2}° -> {新:F2}°`
  (以上四条修正日志**在 SetRotation/SetElevation 之前无条件打印**,记录目标值——§8.2)
- `[FCS Order] priority override: {Label} (P{a}) fires before committed {Label} (P{b})`
  (`P` 前**有空格**;仅 override 分支打印——§5.3)
- `[FCS Order] batch {executionBatchId} paired once: {first.Label} first, {second.Label} second; {reason}`
  (reason 为 `priority P{高} over P{低}` 时**不过本地化**——§5.2)
- `[FCS Order] engagement sequence (est lay {totalSeconds:F0}s): ` +
  `string.Join(" -> ", ordered.Select(t => $"#{t.serial}(P{t.priority} {t.angel:F0}deg)"))`
  ——**`#` 后是任务 serial,不是序位**;角度是任务 `angel`(方位角),F0,后缀字面 `deg`;
  分隔符字面 `" -> "`。示例:`[FCS Order] engagement sequence (est lay 38s): #12(P90 123deg) -> #9(P50 271deg)`
- `[FCS Plan] {victim.Label} preempted by urgent #{serial} P{p} (load {shell} C{c} transfers; min required C{m})`
- `[FCS Dispatch] queued #{serial} P{priority}; pending={n}`
- `[FCS Dispatch] urgent #{serial}: {preemptDetail}`(**仅抢占成功时打印**——§5.5)
- `[FCS Dispatch] #{serial} expired after {validForSeconds:F0}s in queue; auto-cancelled`
  (`{n}` 取 **validForSeconds**,不是实测经过时间——§10)
- `[FCS Dispatch] pending #{serial} cancelled by commander; pending={n}`
- `[FCS Plan] {Label}: committed C{m} still reaches {d:F2}km — requeued to fire on the actual charge`
- `[FCS Plan] {Label}: chamber committed C{m}, target {d:F2}km out of its reach — queued chamber-clearing shot #{dump} at {r:F1}km same bearing; original requeued for fresh load`
- `[FCS Plan] {Label}: transient dispenser failure, retry {loadRetryCount}/2 — requeued`
  (`{n}` 是**自增后**的值:第一次 `retry 1/2`——§9)
- `[FCS] console card request: {CardId} P{n}[ bearing {b:F1}deg][ dist {d:F1}km]`
- `[FCS] console card request {CardId} -> {result}`
- `[FCS] card bearing requested {bearing:F1} applied {applied:F1}`(**无条件打印**,值为补偿后)
- `[FCS] card distance requested {distance:F1} applied {appliedDistance:F1}`(同上,F1 精度)
- `[FCS] card start grid '{startGrid}': letter={r1}, number={r2}`
  (回显**原始未 Trim** 串;在失败判定**之前**打印,成功/失败都会出现)
- `[FCS] #{serial} (marker #{serial}) aim adjusted by agent -> brg {b:F1}deg, {d:F2}km [{progress}]`
  ——**两个占位符填的都是 `task.serial`**,输出恒为 `#7 (marker #7)`;该行在
  `RefreshSolution(task)` **之后**打印,`b`/`d` 用刷新后的 `task.angel`/`task.distance`,
  `{progress}` 是 `task.progress` 的枚举 `ToString()`。
