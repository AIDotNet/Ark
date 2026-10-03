# Ark 同步功能改进计划（P0–P2 全量 · 同步引擎重构 + 同步中心 UI）

> 依据前期分析（现状代码盘点 + pt-table-checksum / pgsync / DBeaver / Navicat 竞品调研）制定。
> 已确认的决策：**P0–P2 全量**、**纳入"先对比后同步"模式（差异查看器）**、**UI 重构为同步中心**、**API 允许破坏性变更**（单机单用户，前后端同仓）。

---

## 0. 现状问题（本计划要解决的事实清单）

| # | 问题 | 位置 |
|---|---|---|
| A1 | 执行时重新规划 → SkipActionIds 与预览失配（TOCTOU），破坏性动作可能被意外执行 | `SyncTaskManager.cs:62` |
| A2 | RowDiff 无源端快照一致性，边同步边写入会误判 | `DataTransferEngine.cs:125` |
| A3 | 索引/FK 仅按名字 Diff，同名不同定义静默通过 | `StructureDiffer.cs:26-34` |
| A4 | 列改名 = 删列+加列，数据丢失无提示 | `SyncPlanner.cs:216` |
| A5 | FullCopy 无冲突语义（不勾 TRUNCATE 硬插，无 PK 表堆重复行） | `DataTransferEngine.cs:35` |
| B1 | RowDiff 两侧全量 PK 驻内存 + LIMIT/OFFSET 翻页 O(n²) | `DataTransferEngine.cs:129` |
| B2 | 无原生批量通道（PG COPY / MySQL bulk loader），PG 每批仅 2000 参数位 | `DataTransferEngine.cs:27` |
| B3 | 表间串行、无批量装载会话调优 | `SyncTaskManager.cs:69` |
| C1 | 任务不可取消/重试/续传，重启即丢；全局单并发；轮询 1.5s | `SyncTaskManager.cs:16`、`SyncWizard.tsx:473` |
| C2 | 无同步后校验、无差异行预览、无 SQL 脚本导出 | — |
| D1 | 无 Profile 存档、无定时调度 | — |
| D2 | 无 WHERE 过滤 / 列映射 / 脱敏 / 视图 DDL 同步 | — |

---

## 1. 总体架构变更

```
现状:  SyncWizard(5步) ──► /sync/plan ──► /sync/tasks(重规划) ──► SyncTaskManager(内存,单并发) ──► DataTransferEngine(全量PK驻内存/多值INSERT)

目标:  SyncCenter(Profile列表│快速同步│先对比)
         │                          │
         ▼                          ▼
   sync_profiles(ark.db)     /compare/runs(异步) ──► ChunkedDiffEngine(分块校验和, keyset, O(块数)内存)
         │                          │                        │
         ▼                          ▼                        ▼
   SyncScheduler(cron,BackgroundService) ──► SyncTaskManager v2(持久化/取消/续传/并行/SSE)
                                                    │
                                                    ▼
                              ExecutionPlan(SQL快照,执行不再规划) ──► DataTransferEngine v2(BulkWriter+流水线+并行+校验)
```

新增/重命名模块（`backend/src/Ark.Sync/`）：

```
Ark.Sync/
  SyncPlanner.cs            # 改造：定义指纹Diff、改名启发式、视图DDL、输出 ExecutionPlan 快照
  ExecutionPlan.cs          # 新增：可持久化的动作快照模型（替代"用请求重规划"）
  ChunkedDiffEngine.cs      # 新增：分块校验和对比（核心新算法）
  DataTransferEngine.cs     # 改造：BulkWriter 通道、流水线、冲突语义、keyset 游标
  Checksum.cs               # 新增：块哈希（行哈希按序合并，跨方言一致）
  SyncTaskManager.cs        # 改造：状态机/持久化/取消/续传/并发度
  SyncScheduler.cs          # 新增：BackgroundService + 轻量 cron
  SyncValidator.cs          # 新增：同步后校验（行数+抽样块哈希）
  Masking.cs                # 新增：脱敏规则应用器
  CronExpression.cs         # 新增：最小 cron 解析（分 时 日 月 周 + */n）
```

---

## 2. 后端详细设计

### 2.1 模型扩展（`Ark.Core/Sync/SyncModels.cs`）

```csharp
// 表级选择扩展
public sealed record TableSelection {
    // 现有字段…
    string? Where;                                  // 行过滤（源端 SQL WHERE 片段，参数化校验后拼接）
    IReadOnlyDictionary<string,string>? ColumnMap;  // 源列→目标列映射（缺省同名）
    DataSyncMethod? MethodOverride;                 // 表级覆盖全局 DataMethod
    IReadOnlyList<string>? ExcludeColumns;          // 列子集
    IReadOnlyList<MaskRule>? MaskRules;             // 写入前脱敏
}
public sealed record MaskRule(string ColumnPattern, MaskRuleKind Kind, string? Value);

// 冲突语义（替代 bool TruncateBeforeCopy）
public enum ConflictMode { Error, Truncate, SkipExisting }   // SkipExisting → ON CONFLICT DO NOTHING / INSERT IGNORE

// 执行计划快照（A1 修复的核心）
public sealed record ExecutionPlan {
    Guid Id; DateTimeOffset CreatedAt;
    SyncPlanRequest Options;                        // BatchSize/ConflictMode/并行度等
    IReadOnlyList<TableExecution> Tables;           // 动作 SQL 已全部生成
}
public sealed record TableExecution {
    TableSelection Source; TableSelection Target;
    IReadOnlyList<DdlAction> StructureActions;      // 含 SQL 全文
    IReadOnlyList<DdlAction> PostCopyActions;       // "索引后建"用的第二段（CreateIndex/FK）
    DataExecutionPlan? Data;
}
public enum TaskKind { Sync, Compare }              // 对比也走任务管理器

// 任务状态机扩展
public enum SyncTaskStatus { Queued, Running, CancelRequested, Cancelled, Completed,
                             Failed, PartiallyFailed, Interrupted }   // Interrupted=服务重启遗留,可 resume

// 对比结果
public sealed record ChunkDiff(string Table, int ChunkIndex, string? LowerKey, string UpperKey,
    long SourceRows, long TargetRows, ChunkStatus Status);   // Matched/Different/MissingInTarget/MissingInSource
public sealed record DiffSampleRow(string KeyPk, string ChangeType, string[] SourceRow, string[] TargetRow);
public sealed record CompareRunResult { Chunks; Samples(前N行); Verified; }
```

`DdlAction` 增加 `Kind` 值：`CreateView` / `DropView` / `RenameColumn`（用户确认后生成的映射动作）。
`ConversionIssue.Severity` 增加 `"hint"`（改名候选等确认型提示）。

### 2.2 SyncPlanner 改造

1. **索引/FK 定义指纹（A3）**：`StructureDiffer` 中索引按 `Fingerprint = hash(sort(列名+排序方向), IsUnique, 谓词, 类型)` 对齐（先名后指纹：同名同指纹→跳过；同名异指纹→Drop+Create；异名同指纹→warning"疑似仅命名差异"）；FK 指纹 = `sort(列) + 引用表 + sort(引用列) + OnDelete + OnUpdate`。`CanonicalIndex/CanonicalForeignKey` 需确认已含谓词/onDelete 字段，缺失则在各 Provider 元数据读取时补齐。
2. **列改名启发式（A4）**：删列+加列配对条件 = 位置相邻 + CanonicalTypeId 相同 + 全表仅此一对候选 → 输出 `RenameCandidate { Drop, Add, Confidence }`，前端让用户选择"视为改名（映射列同步，数据保留）/ 确实删加（标破坏性）"。确认改名后 `ColumnMap` 自动带上该映射。
3. **视图 DDL**：`IDbProvider.GetViewDefinitionAsync(database, schema, view) → string?`（PG `pg_get_viewdef`、MySQL `SHOW CREATE VIEW`、SQLite `sqlite_master.sql`）。计划中对勾选的视图输出 `CreateView` 动作（原文 + warning"未做方言翻译"）；MySQL→PG 等跨方言视图默认 warning 不阻塞。
4. **输出快照**：`BuildPlanAsync` 返回值改为 `SyncPlanResponse { Plan: ExecutionPlan, ... }`；`POST /sync/tasks` 直接收快照（见 2.7），执行阶段零规划。
5. **WHERE 校验**：`Where` 片段经"SELECT 1 FROM t WHERE {片段} LIMIT 1"试编译，失败即计划错误；禁止分号/注释/`--`。

### 2.3 IDbProvider / ProviderBase 扩展

```csharp
// IDbProvider 新增
Task<string?> GetViewDefinitionAsync(string database, string? schema, string view, CancellationToken ct);
Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct);   // A2
Task<IBulkWriter> OpenBulkWriterAsync(TableRef t, IReadOnlyList<string> cols, CancellationToken ct); // B2
IReadOnlyList<string> BulkLoadPragmas { get; }        // 会话调优语句（空数组=不支持）
Task<List<object?[]>> ReadKeysetPageAsync(TableRef t, IReadOnlyList<string> cols,
    IReadOnlyList<string> pkCols, object?[]? afterKey, int limit, string? where, CancellationToken ct); // B1
```

- **ISnapshotSession**：持有专用连接 + 打开事务。PG=`REPEATABLE READ`；MySQL=`START TRANSACTION WITH CONSISTENT SNAPSHOT`（计划期检测 InnoDB，MyISAM 警告"无法保证一致快照"）；SQLite=`BEGIN`（deferred 即快照）。引擎全部源端读走该会话；`DisposeAsync` 提交/回滚。
- **IBulkWriter**（按 `Capabilities.SupportsBulkCopy` 降级）：
  - PG：Npgsql `BeginBinaryImport`（`COPY t (cols) FROM STDIN BINARY`），无参数上限；
  - MySQL：`MySqlBulkLoader`（内存 CSV 流 + `LOCAL INFILE`；启动时探测权限，不可用→降级多值 INSERT 并在任务日志提示）；
  - SQLite：单事务 + 复用 prepared command 批量绑定（batchSize 提升到 5000）。
  - 多值 INSERT 路径保留为降级实现；PG `RowsPerBatch` 修复为 `65535/cols`（仅降级路径生效）。
- **BulkLoadPragmas**：PG `SET synchronous_commit=off`；MySQL `SET FOREIGN_KEY_CHECKS=0, unique_checks=0`（会话级，写连接专用）；SQLite `PRAGMA synchronous=OFF; PRAGMA foreign_keys=OFF`。仅在"写目标"连接上执行，结束恢复默认。

### 2.4 ChunkedDiffEngine（B1 核心，替代现 RowDiffAsync）

```
输入: 源/目标表(均要求PK)、列映射、chunk 目标时长(默认0.5s, 仿 pt-table-checksum --chunk-time) 或固定行数
流程:
 1. 双侧各持一个 keyset 游标（ORDER BY PK, WHERE pk > :afterKey LIMIT n），各自流式推进
 2. 对齐: 以源块 upperKey 为界 —— 目标侧读到 ≥ upperKey 为止，超出部分留给下一块（拆块）
    → 两侧块序列按键范围严格对齐；无 PK 表不支持（计划期报错，维持现状回退全量）
 3. 每块计算: 行数 + ChunkHash = 顺序合并 RowHash.ComputeRow(...) 后再 SHA256（应用层，跨方言一致）
 4. ChunkHash 相同 → 块跳过；不同 → 该 [lowerKey, upperKey] 范围内逐行拉取比对（复用现有
    FetchTargetRows/ExecuteUpdate/InsertRowsAsync 逻辑，改为 keyset 边界）
 5. 每块完成即写 checkpoint（表 + 块序号 + 上界键）→ 支持续传/取消
输出: TableCompareResult { chunks, diffChunks, samples(前N差异行: 新增/变更/删除 各至多20行) }
执行模式: 逐块即时 apply（插入/更新/删除，块内一事务）或 仅报告（compare 任务模式）
```

内存 O(chunks)；LIMIT/OFFSET 全部替换为 keyset。旧 `RowDiffAsync` 删除，测试改写为等价性验证。

### 2.5 DataTransferEngine v2

- **FullCopy**：`ConflictMode` 三选一（A5）——
  `Truncate`：先 TRUNCATE/DELETE；`SkipExisting`：PG/SQLite `ON CONFLICT DO NOTHING`、MySQL `INSERT IGNORE`（无 PK 表→计划期错误）；`Error`：默认硬插（现状）。
- **流水线**：源读第 N+1 批与目标写第 N 批并发（双 `Channel<object?[]>`，深度 2）。
- **索引后建**（可选 `DeferIndexes`，默认关）：建表动作剥离二级索引/外键 → `PostCopyActions`，导入完成+校验通过后执行；大表全量导入显著提速。
- **表级并行（B3）**：`ExecuteAsync(plan, maxDop)` —— 拓扑分层内 `Parallel.ForEachAsync(maxDop)`；每表独立快照会话/写连接；`maxDop` 全局默认 2。
- **校验（C2）**：表完成后 `SyncValidator` 执行——行数 `COUNT(*)` 两侧对比（必做）；`VerifyMode.Sample`（默认）随机抽 1 块重算哈希、`VerifyMode.Full`（compare 复用）/`Off`。结果入 `TableSyncReport.Verification { SourceRows, TargetRows, Match }`。

### 2.6 SyncTaskManager v2 + 持久化（C1）

- **ark.db 新表**：
  ```sql
  sync_tasks(id, kind, profile_id NULL, status, options_json, plan_json,   -- ExecutionPlan 快照
             progress_json, checkpoint_json,  -- {table, chunkIndex, upperKey / insertedRows}
             error, started_at, finished_at)
  sync_profiles(id, name, config_json, cron TEXT NULL, enabled, created_at, updated_at, last_run_at)
  ```
  `sync_reports` 保留，增加 `profile_id` 列。服务启动：`Running→Interrupted`，Queued→重新排队。
- **执行**：全局并发度 `MaxConcurrentTasks`（settings，默认 2）替代 `SemaphoreSlim(1,1)`；`CancellationTokenSource` 每任务一个，仅在块/批边界响应取消。
- **续传**：`POST /api/sync/tasks/{id}/resume` 从 `checkpoint_json` 恢复（同 plan 快照 + 游标跳过已完成块；DDL 按 `CompletedActionIds` 跳过）；`retry` 从头。
- **SSE**：`GET /api/sync/tasks/{id}/events`（`text/event-stream`，推送 state 增量）；`GET /api/sync/tasks` 轮询端点保留但前端改 SSE。

### 2.7 API 变更（破坏性，已获批）

```
POST /api/sync/plan                → SyncPlanResponse{ plan: ExecutionPlan, ... }   (保留, 预览/快照生成)
POST /api/sync/tasks               body: { plan: ExecutionPlan, skipActionIds, confirmDestructive }   ← 破坏性变更
POST       /api/sync/tasks/{id}/cancel | /retry | /resume
GET        /api/sync/tasks?kind=&status=&profileId=&limit=&offset=                  (分页/筛选)
GET        /api/sync/tasks/{id}/events                                              (SSE)

POST /api/compare/runs             body: { source, target, tables, dataMethod } → 异步 compare 任务
GET  /api/compare/runs/{id}        → 结构差异 + 表级 chunk 统计 + 校验
GET  /api/compare/runs/{id}/tables/{table}/diffs?changeType=&cursor=&limit=       (差异键分页, 行值按需拉取)
POST /api/compare/runs/{id}/to-plan → 由对比结果生成 ExecutionPlan(默认勾选全部差异块)

CRUD /api/sync/profiles            + POST /api/sync/profiles/{id}/run  + PATCH .../{id}/schedule
GET  /api/sync/reports?profileId=&limit=&offset=
POST /api/sync/tasks/{id}/export-script            → SQL 脚本文件流(实际执行的动作)
```

错误码沿用 5xxx 同步段扩展（52xx compare / 53xx profile / 54xx schedule）。

### 2.8 Profile + 调度（D1）

- `SyncScheduler : BackgroundService`：每 30s 扫描 `enabled` 且 `cron` 命中的 profile → 创建 `profileId` 关联任务入队；服务重启错过的触发不补跑（日志记录"missed schedule"）。
- `CronExpression`：5 字段最小实现（`*` `,` `-` `*/n`），NextOccurrence(now) 单元测试覆盖。
- Profile 运行即提交 `ExecutionPlan`（运行时重新 `BuildPlanAsync` 生成快照——Profile 保存的是配置而非冻结的快照）。

### 2.9 过滤 / 列映射 / 脱敏（D2）

- `Where`：拼接进源端读 SQL（keyset 分页外层包裹 `SELECT ... FROM (…) WHERE` 或直接追加 AND）；试编译校验（2.2-5）。
- `ColumnMap`：读源用源列名，写目标/哈希比对用映射后目标列名；未映射的源列若目标缺失 → 计划期 warning"列 X 将跳过"。
- `MaskRules`：仅作用于**写路径**（BulkWriter 写入前 / INSERT 参数前）；diff 哈希用源值计算保证对比正确；`MaskRuleKind = Null | RandomInt(min,max) | RandomLetter(n) | RandomDate | Fixed | SqlExpr(目标方言表达式,如 (RANDOM()*10)::int)`。唯一键列上的随机规则 → 计划期警告冲突风险。

---

## 3. 前端设计：同步中心

### 3.1 文件结构

```
frontend/src/features/sync/
  SyncCenter.tsx            # 主容器（Tab: 配置 | 任务历史 | 报告）—— 替代 SyncWizard 挂载点
  ProfileList.tsx           # Profile 卡片列表（名称/源→目标徽章/模式/调度状态/上次运行; 操作: 运行|编辑|复制|调度开关|删除）
  NewSyncDialog.tsx         # 新建/编辑向导容器（原 5 步向导改造为 4 步）
  steps/ConnectStep.tsx     # 源/目标（原 Step0/1 合并, 同屏双卡）
  steps/ScopeStep.tsx       # 表选择(搜索框过滤) + 全局选项 + 每表展开行(方法覆盖/WHERE编辑/列映射/脱敏入口)
  steps/ReviewStep.tsx      # 计划审阅: 按表分组的动作树(Collapsible), 动作行展开只读 CodeMirror 显示 SQL,
                            #   差异块统计, 顶部工具条(全选/按类型筛选/导出SQL), "保存为 Profile"开关
  CompareResultView.tsx     # 对比结果: 表卡片(结构差异数/新增/变更/删除统计 StatPill) → 点击进 DiffViewer
  DiffViewer.tsx            # 差异行表格: 三 Tab(插入/变更/删除), 变更行旧值删除线+新值高亮, 虚拟滚动
                            #   (复用 DataGrid 的 TanStack Table 模式), 分页拉取 diffs API
  TaskRunView.tsx           # 运行页: SSE 驱动 —— 总进度/当前表/块级行数进度, 取消按钮(CancelRequested→Cancelled),
                            #   Interrupted 任务显示"续传/重新开始", 日志流(自动滚底)
  TaskHistory.tsx           # 历史列表: 状态/profile/时间筛选, 行操作(查看/续传/重跑/导出脚本)
  ReportView.tsx            # 报告: 表级 DDL/数据统计 + 校验列(行数对比 ✓/✗) + 导出 CSV
  cron/CronField.tsx        # 调度编辑: 预置模板 Select(每小时/每天HH:mm/每周X HH:mm/自定义5段) + 下次运行预览
  stores/sync-center.ts     # zustand: 向导草稿(跨步骤)、当前 compareRun、profile 编辑态
```

挂载：`TabContent.tsx` 的 `kind==="sync"` 分支由 `<SyncWizard/>` 换为 `<SyncCenter/>`；`workspace.ts` 的 `tabKey("sync")` 不变。`useActiveTask` 扩展为 `taskIds: string[]`（状态栏多任务进度条，SSE 驱动）。

### 3.2 交互流

- **快速同步（向导 4 步）**：`连接 → 范围与选项 → 审阅(动作树+SQL+存Profile) → 执行`。向导完成时若勾选"保存为 Profile"，POST profiles 后跳 Profile 列表。
- **先对比**：向导第 2 步底部模式切换「直接同步 / 先对比」；先对比 → 审阅页只显示对比统计 → 「查看差异」进 CompareResultView → 可「生成同步计划」回到审阅。
- **从 Profile 运行**：卡片「运行」→ 确认弹窗（展示将重新生成的计划要点）→ TaskRunView。
- **调度**：CronField 保存后卡片显示「下次运行：…」+ 绿色调度徽章。

### 3.3 UI 组件

- 复用 shadcn/ui 既有组件（Card/Checkbox/Switch/Progress/Table/Alert/Badge/Select/Tabs/Collapsible/ScrollArea/Dialog/Tooltip）；SQL 展开用只读 CodeMirror 6 实例（复用 `QueryConsole` 的配置）。
- 新增轻组件：`StatPill`（差异统计胶囊：`+12 ~3 -5`）、`DiffCell`（旧值 line-through 红 / 新值绿）、`CronField`（不引入第三方 cron UI 库）。
- 空态沿用 `Empty`；加载沿用 `Skeleton`；删除/破坏性确认沿用 `AlertDialog`。

---

## 4. 里程碑与验证

| 里程碑 | 内容 | 验证 |
|---|---|---|
| M1 P0 正确性 | ExecutionPlan 快照执行(A1)、快照会话(A2)、索引/FK 指纹(A3)、ConflictMode(A5)、改名启发式(A4) | 单测:指纹/启发式/冲突SQL；集成:执行与预览动作一致、快照下写线程注入不误判 |
| M2 分块 Diff + 对比流程 | ChunkedDiffEngine、keyset、compare API | 单测:块切分对齐边界(空表/单块/超大块/复合PK)；Sync.Tests:ChunkedDiff 与全量 RowDiff 结果等价（随机数据+注入差异）；集成:千万行模拟大块数内存稳定 |
| M3 传输提速 | BulkWriter(COPY/LOCAL INFILE/SQLite prepared)、流水线、表级并行、BulkLoadPragmas、索引后建 | 集成:三库两两 round-trip（数据一致）；基准:PG 100w 行导入耗时对比记录到测试输出 |
| M4 任务运维 | sync_tasks 持久化、cancel/retry/resume、Interrupted 恢复、SSE、并发度 | 集成:执行中 kill 进程→重启→resume 完成；cancel 在块边界生效 |
| M5 同步中心 UI | SyncCenter 框架、ProfileList、向导 4 步改造、ReviewStep、TaskRunView(SSE)、TaskHistory、ReportView | `pnpm build`（tsc）；手动:向导全流程 |
| M6 对比 UI | CompareResultView、DiffViewer、to-plan 链路 | 手动:先比后改全流程；虚拟滚动 1w 差异行 |
| M7 Profile + 调度 + 校验 | sync_profiles、SyncScheduler、CronExpression、SyncValidator | 单测:cron NextOccurrence 矩阵；集成:定时触发入队、报告含校验结果 |
| M8 生态能力 | WHERE/ColumnMap/MaskRules、视图 DDL、SQL 脚本/报告导出 | 单测:脱敏规则、WHERE 校验、列映射读写；集成:脱敏写入后目标值已替换且 diff 正确 |

测试命令不变：`dotnet test backend/tests/Ark.Sync.Tests`、`dotnet test backend/tests/Ark.Integration.Tests`（Testcontainers，无 Docker 自动跳过）、`cd frontend && pnpm build`。

## 5. 风险与对策

| 风险 | 对策 |
|---|---|
| MySQL MyISAM 无一致快照 | 计划期检测表引擎 → warning 降级为普通读 |
| MySqlBulkLoader 需 LOCAL INFILE 权限 | 运行时探测失败自动降级多值 INSERT + 日志提示 |
| 无主键表不支持 ChunkedDiff | 维持现状：计划期报错/回退全量复制 |
| 脱敏唯一键冲突 | 计划期警告；SqlExpr 规则由用户自担 |
| 块对齐在复合 PK/乱序写入下边界复杂 | 块上界用完整 PK 元组比较；等价性测试兜底 |
| SSE 连接占用（单用户可接受） | 每任务仅 1 条 SSE；轮询端点保留兜底 |
| cron 仅支持服务器本地时区 | 文档明示，UI 预览下次运行时间 |
