# Ark 查询控制台（Query Console）样式与功能优化计划

> 范围：`frontend/src/features/query/QueryConsole.tsx` 及相关 store / 后端 Query、Metadata 端点。
> 决策已确认：允许改前后端；支持同一连接多查询标签；纳入 AI 能力；按 P0/P1/P2 分期。

---

## 一、现状问题（基于代码核实）

### 样式 / 交互
| # | 问题 | 位置 |
|---|---|---|
| S1 | 编辑器默认 doc 是残缺占位符 `SELECT * FROM  LIMIT 100`，首屏即显示半截高亮 SQL，观感像 bug | `QueryConsole.tsx:185` |
| S2 | CodeMirror 用裸 `basicSetup`，无深浅色主题适配，暗色模式下样式突兀（截图中首行浅色背景块） | `QueryConsole.tsx:165-172` |
| S3 | 工具栏为一排 `Label + Select` 平铺，无分组无分隔，拥挤且主操作（执行）不突出 | `QueryConsole.tsx:205-277` |
| S4 | 快捷键标签恒为 `Ctrl+Enter`，macOS 实际是 `⌘Enter`（`Mod-Enter` 已支持，仅显示错误） | `QueryConsole.tsx:232-235` |
| S5 | 结果表用 shadcn `Table` 一次性渲染前 500 行，无虚拟滚动、无行号、无列宽调整、无排序、无单元格复制 | `QueryConsole.tsx:47-92` |
| S6 | 结果 Tabs 仅"结果 1/2/…"，行数、耗时、截断信息埋在工具栏右侧小字 | `QueryConsole.tsx:296-313` |
| S7 | 执行错误只弹 toast，长错误信息不可读、无法复查、不能定位语句 | `QueryConsole.tsx:142` |
| S8 | 历史是 96 宽 DropdownMenu，只显示 SQL 首行，无搜索、无收藏、无耗时/行数 | `QueryConsole.tsx:246-270` |

### 功能
| # | 缺失 | 对照竞品 |
|---|---|---|
| F1 | 每个连接只能开 1 个查询控制台（tab key 锁死 `query:{connectionId}`），切换连接/库会共用同一编辑器 | Navicat/DataGrip/DBeaver 均支持多控制台 |
| F2 | 只能整篇执行；无"执行选中语句""执行光标处语句" | 全部竞品标配 |
| F3 | 无列级自动补全（仅表名）、无别名/JOIN 补全 | DataGrip/DBeaver |
| F4 | 无 SQL 格式化 | 全部竞品标配 |
| F5 | 无 EXPLAIN / 执行计划 | DBeaver/DataGrip/Navicat |
| F6 | 无法取消运行中的查询；无已耗时显示 | 全部竞品标配 |
| F7 | 无保存的查询（收藏/Snippets），历史不可搜索 | TablePlus/Beekeeper |
| F8 | 结果无法复制单元格/行、无"复制为 INSERT"、无结果内过滤 | 全部竞品标配 |
| F9 | SQL 内容不持久化，页面刷新即丢失（标签隐藏保活只救了 tab 切换） | DataGrip console 文件化 |
| F10 | 无 AI 能力（NL2SQL / 错误解释） | Chat2DB/DBeaver AI |
| F11 | schema 选择固定为打开时的 `initialSchema`，工具栏无 schema 切换 | pg 多 schema 场景受限 |
| F12 | 超时固定 30s，行上限固定 5 档 | 可配置化 |

### 竞品功能参考（DBeaver 官方文档已核对 + DataGrip/TablePlus/Navicat/Chat2DB 产品知识）
- **DBeaver**：schema 感知补全、SQL 模板、格式化、执行计划可视化、Query Manager（历史+统计）、书签、Outline、错误悬浮指示、活动库/schema 切换（保留 SQL）、多结果单 Tab、AI 错误解释。
- **DataGrip**：光标处语句执行、语句边界高亮、表别名感知补全（自动建议 JOIN）、console 持久化、本地历史、参数化查询、结果网格编辑。
- **TablePlus/Beekeeper**：收藏查询（Favorites）、历史面板（搜索+重跑）、快捷键优先、结果导出 CSV/JSON。
- **Chat2DB**：NL2SQL、SQL 解释/优化建议、错误修复。

---

## 二、目标设计

### 工具栏（重排后）
```
[▶ 运行 ⌘↩] [■ 取消*] │ [✨ 格式化] [⻗ 计划] [🤖 AI ▾] │ 库[▾] schema[▾] 行上限[▾] │ [⭐ 保存] [🕘 历史] │···右侧状态：✓ 12.3 ms · 3 个结果集
```
`*` 仅运行中显示；库/schema/行上限收进带图标的紧凑 Select；运行状态从"埋字"升级为带图标的状态片段。

### 结果区（重排后）
```
Tabs: [结果 1 · 1,000 行] [消息 2] [计划]        [🔍 过滤] [导出 ▾]
网格: 行号列 | 可拖拽列宽 | 点击表头排序 | 单元格选中复制 | NULL 斜体 | 长文本/JSON 点击弹出查看
状态条: 共 1,000 行（已达上限截断） · 12.3 ms
```

---

## 三、分期实施

## P0 — 核心体验（样式 + 执行 + 结果网格）

### 1. 多查询标签页（F1）
- `frontend/src/stores/workspace.ts`：`tabKey` 对 `kind === "query"` 由 `query:{connectionId}` 改为 `query:{connectionId}:{seq}`（`seq` 为 store 内自增计数，持久化到 localStorage 防重启重复）；`WorkspaceTab` 增加可选 `queryNo?: number` 用于标题"查询-3"。
- `AppShell.tsx`「新建查询」、`ConnectionTree.tsx` 连接/库右键菜单加「新建查询」：每次都开新标签。
- 新 store `frontend/src/stores/query-session.ts`（zustand + persist）：`Record<tabKey, { sql: string; database: string; schema: string }>`，编辑器内容/库选择变更时写入；标签关闭时清除；页面刷新后按 key 恢复。**不持久化结果集**（数据可能过期）。
- 标签关闭无需确认（内容已自动保存过，可从历史找回）。

### 2. 编辑器体验（S1/S2/S4 + F2）
- 修复默认 doc：改为空字符串，配合现有 Empty 状态；新建标签给注释模板 `-- Ctrl+Enter 执行 · 表名/列名自动补全`。
- 新增 `frontend/src/features/query/editor-theme.ts`：CodeMirror `EditorView.theme` + `HighlightStyle` 两套，按 `next-themes` 的 resolvedTheme 切换（Compartment reconfigure），配色对齐 Tailwind 语义变量（背景透明融入面板）。
- 新增 `frontend/src/lib/sql-statements.ts`：语句边界扫描器（处理 `'...'`、`"..."`、反引号、`--`/`/* */` 注释、PG `$$...$$`），返回 `{from, to}[]`；供"执行选中/光标语句"与错误定位使用。
- 执行逻辑：有选区→执行选区；无选区→执行光标所在语句；空/全注释→整篇。当前语句加背景高亮（CM6 decoration）。
- 快捷键标签按平台显示：`navigator.platform` 检测，macOS 显示 `⌘ ↩`。
- 补全增强（P0 先做表名+视图+列名平铺，JOIN 感知留 P1）：`lang-sql({ schema })` 的 schema 值由 `CompletionSchema`（见 P1 后端端点，可先降级为现有 `listTables`）生成 `{ table: [col1, col2...] }`。

### 3. 结果网格（S5/S6 + F8）
- 新组件 `frontend/src/features/query/ResultsGrid.tsx`，参照 `DataGrid.tsx` 的 TanStack Virtual 方案（`ROW_HEIGHT` 常量、`useVirtualizer`、绝对定位行）：
  - 行号列（sticky）；列宽拖拽（`onMouseDown` 手柄，min 60px）；列类型小字标注保留。
  - 点击表头排序（客户端 `Array.sort`，数值/字符串感知）；再点取消。
  - 单元格单击选中（蓝色 ring），`Ctrl/Cmd+C` 复制；右键 ContextMenu：复制单元格 / 复制行 / 复制为 INSERT（基于列名+行值生成）、整列复制。
  - 长文本（>80 字符）与 JSON（`{`/`[` 开头）单元格显示省略，点击弹 Popover/Dialog 格式化查看（JSON pretty print + 等宽字体 + 复制按钮）。
  - 过滤输入框：客户端所有列 `includes` 匹配（大小写不敏感），显示"命中 N/M 行"。
  - 移除 500 行硬截断（虚拟滚动可扛 maxRows 上限 10000）。
- 结果区 Tabs：标题显示行数（`结果 1 · 1,000`）；新增「消息」Tab 渲染 `result.messages` 逐行列表；受影响行数在无结果集时显示为消息 Tab + 状态条。

### 4. 错误展示与运行状态（S7 + F6）
- `run()` catch：错误写入 `result` 同级的 `error` 状态（而非仅 toast）；结果区显示 Alert（destructive）：错误消息 + `ArkApiError.code`；toast 保留简短提示。
- 取消：`api.executeSql` 增加 `AbortSignal` 参数（fetch signal）；工具栏运行中显示「取消」按钮 → abort；后端 Minimal API 的 `CancellationToken ct` 已绑定 RequestAborted，`ProviderBase.ExecuteQueryAsync` 逐行 `ReadAsync(ct)` 会中断（SQLite/PG/MySQL ADO 驱动均支持命令级取消）。
- 状态条升级：运行中显示已耗时计时器（`setInterval` 100ms）；完成后显示 `✓ N ms · M 行`；出错显示 `✗ 失败`。

### 5. 样式统一（S3）
- 工具栏按上述目标设计重排：运行按钮 primary 加图标；分组间加 `Separator` orientation="vertical"；Label 文字收进 Select 内部前缀。
- 结果状态条（border-t, text-xs）统一展示行数/截断/耗时，替代工具栏右侧小字。

**P0 验收**：同连接开 3 个查询标签互不干扰；刷新后 SQL 恢复；选中一段 SELECT 只执行选中部分；执行 `SELECT 1; SELECT 2;` 出两个结果 Tab；错误 SQL 显示错误面板；取消生效；10000 行结果滚动流畅；单元格可复制/查看 JSON。

## P1 — 进阶功能（需后端配合）

### 6. 列级补全元数据端点
- 后端 `MetadataEndpoints.cs` 新增：`GET /api/connections/{id}/databases/{db}/schemas/{schema}/completion` → `CompletionSchema { tables: [{ name, kind, columns: [{ name, dataType }] }] }`（含视图）。
  - PG：`information_schema.columns` 单查询；MySQL：`information_schema.columns WHERE table_schema = DATABASE()`；SQLite：每表 `PRAGMA table_xinfo`（表数量小）。
  - 各 Provider 接口加 `Task<CompletionSchema> GetCompletionSchemaAsync(string database, string schema, CancellationToken ct)`，默认实现放 `ProviderBase`。
- 前端 QueryConsole 改用该端点喂给 `sql()` schema，切换库/schema 时 reconfigure。

### 7. SQL 格式化
- 新依赖 `sql-formatter`（支持 postgresql/mysql/sqlite 方言参数）。
- 工具栏「格式化」按钮 + `Shift+Alt+F`：格式化全文或选中段，`view.dispatch` 替换。

### 8. EXPLAIN 执行计划
- 后端 `QueryEndpoints.cs` 新增 `POST /api/connections/{id}/explain`，body `{ database, schema?, sql }` → 统一返回 `{ planText: string, planJson?: unknown }`：
  - PG：`EXPLAIN (VERBOSE, FORMAT JSON)`（剥离外层 wrapper）；MySQL 8：`EXPLAIN FORMAT=TREE`，探测版本 <8 回退传统 `EXPLAIN` 表格转文本；SQLite：`EXPLAIN QUERY PLAN`。
  - 只读连接允许（SELECT 类 explain，`ReadOnlyGuard` 不拦截）。
- 前端：选中/光标语句可「解释」；结果区新增「计划」Tab，等宽字体树形缩进展示 + 复制按钮。

### 9. 历史与保存的查询（S8 + F7）
- `query-history.ts`：entry 增加 `durationMs`、`rowCount`、`favorite` 字段（persist version 迁移 v1→v2，`migrate` 补默认值），上限提到 500。
- 「历史」按钮改 Popover 面板（w-[480px]）：顶部搜索框（SQL 内容模糊匹配）；按日期分组（今天/昨天/更早）；每条显示 SQL 首行 + 库 + 时间 + 耗时 + 行数 + ⭐ 收藏切换；单击回填、双击执行；底部清空。
- 新增「保存的查询」：store `saved-queries.ts`（persist，全局不分连接，记录创建时的连接/库作默认值）；工具栏 ⭐ 保存当前 SQL；「历史」面板内 Tab 切换 历史/已保存；保存支持命名。

### 10. AI 助手（F10）
- 后端：
  - `ArkRepository` 增加 `settings` 表（`key TEXT PRIMARY KEY, value TEXT`，启动 `CREATE TABLE IF NOT EXISTS`）；API `GET/PUT /api/settings/ai`（DTO：`{ enabled, baseUrl, model, apiKeyProtected }`，apiKey 经 `PasswordProtector` 加密存储，GET 不回传明文只回 `hasApiKey`）。
  - 新增 `Ark.Api/Services/AiAssistantService.cs`：HttpClient 调 OpenAI 兼容 `/chat/completions`（非流式）。
  - 端点 `POST /api/ai/sql`（自然语言 + CompletionSchema 摘要截断至 ~8k 字符 → 生成 SQL）；`POST /api/ai/explain`（SQL → 解释/优化建议）；`POST /api/ai/fix`（SQL + 错误信息 → 修正后 SQL + 原因）。统一 system prompt 要求仅返回 SQL/Markdown。
- 前端：
  - 设置入口：顶栏齿轮 → Dialog（新 `SettingsDialog.tsx`）：AI 开关、Base URL、API Key（password input）、模型名、测试连接按钮。
  - 工具栏「AI」DropdownMenu：**生成 SQL**（Popover 输入自然语言 → 结果 diff 预览 → 插入编辑器/替换选区）、**解释此查询**（Dialog 展示 Markdown）、错误面板内「AI 修复」按钮（仅 `enabled` 时显示）。
  - 未配置时菜单项置灰 + 引导文案。

### 11. schema 切换 + 参数开放（F11/F12）
- 工具栏 schema Select（`api.listSchemas` 已有）；切换后重建补全 schema、更新 `query-session`。
- 超时改为「行上限」旁的小 Select（10/30/60/300s）；行上限档位不变。

**P1 验收**：输入表名补全出列名；`Shift+Alt+F` 格式化复杂 JOIN；PG 库对 `SELECT * FROM big_table` 出计划树；历史可搜索可收藏；配置 LLM 后 NL2SQL 插入生成的 SQL；错误可一键 AI 修复；pg 库切换 schema 后补全跟随。

## P2 — 锦上添花（可独立裁剪）

### 12. 编辑器增强
- JOIN/别名感知补全：解析 FROM/JOIN 后表别名，`ON` 处建议关联列（基于 CompletionSchema 的主外键）。
- 当前文件 Outline（右侧栏：语句列表 + 跳转）；语句书签（DBeaver 式，localStorage）。
- 编辑器底部状态栏：Ln/Col、选中字符数、语句计数。

### 13. 结果增强
- 「复制为 Markdown / JSON」；列右键隐藏列 / 列宽重置。
- 结果列统计悬浮（数值列 min/max/avg、离散度 top5）。
- AI「解释结果」（采样前 N 行发 LLM 生成摘要）。

### 14. 其他
- AI 流式输出（SSE）。
- 查询标签页标题随 SQL 首行自动摘要（DataGrip 风格）。
- 命令面板（⌘K）接入：新建查询/格式化/解释/保存查询等命令注册。

---

## 四、文件改动清单

**前端（新增）**
- `src/features/query/ResultsGrid.tsx` — 虚拟化结果网格
- `src/features/query/editor-theme.ts` — CM6 深/浅主题
- `src/features/query/QueryHistoryPanel.tsx` — 历史+已保存 Popover
- `src/features/query/AiMenu.tsx` — AI 下拉与 Dialogs
- `src/lib/sql-statements.ts` — 语句边界扫描
- `src/stores/query-session.ts`、`src/stores/saved-queries.ts`
- `src/components/layout/SettingsDialog.tsx`

**前端（修改）**
- `src/features/query/QueryConsole.tsx` — 拆分重构（编辑器区/工具栏/结果区引用新组件）、执行选中/光标、错误面板、取消、计时器
- `src/stores/workspace.ts` — query key 多实例 + seq 持久化
- `src/stores/query-history.ts` — 字段扩展 + version 迁移
- `src/lib/api.ts` — executeSql 加 signal；新增 explain/ai/settings/completion 方法
- `src/types/api.ts` — CompletionSchema、ExplainResponse、Ai DTO、SettingsAi
- `src/components/layout/AppShell.tsx`、`ConnectionTree.tsx` — 新建查询入口、设置入口
- `frontend/package.json` — 新增 `sql-formatter`

**后端（新增）**
- `Ark.Api/Services/AiAssistantService.cs`

**后端（修改）**
- `Ark.Api/Endpoints/QueryEndpoints.cs` — `/explain`
- `Ark.Api/Endpoints/MetadataEndpoints.cs` — `/completion`
- `Ark.Api/Endpoints/` 新 `SettingsEndpoints.cs`（settings + ai 三个代理端点）
- `Ark.Core/Dtos` — 上述 DTO
- `Ark.Providers.Abstractions`（IDbProvider/ProviderBase）— `GetCompletionSchemaAsync`、`ExplainAsync` 抽象与默认实现；三个 Provider 各自覆写
- `Ark.Api/Infrastructure/ArkRepository.cs` — settings 表

---

## 五、边界与风险

| 风险 | 处理 |
|---|---|
| 取消查询在 SQLite（本地文件）上可能延迟到下个读取点才中断 | UI 侧取消立即返回"已取消"；后端 ct 终究会终结命令，可接受 |
| MySQL 5.x 无 `FORMAT=TREE` | explain 端点内 `SELECT VERSION()` 探测，<8 回退传统表格输出 |
| CompletionSchema 大库（几百表×列）传输/补全开销 | 端点带 schema 过滤；前端仅在切库时拉取并缓存（TanStack Query staleTime 5min）|
| AI API Key 安全 | PasswordProtector 加密存 ark.db；GET 永不回明文；前端 password 输入框 |
| LLM 生成 SQL 的破坏性 | 生成结果只插入编辑器**不自动执行**，仍由用户按执行 + 只读连接保护 |
| 语句扫描器边界（`$$`、嵌套注释 MySQL 不支持嵌套）| 单测覆盖 8+ 用例（含 PG dollar-quote、`--` 内分号、字符串内分号）|
| query tab key 加 seq 后旧 localStorage 残留 `query:{id}` 旧标签 | persist version 迁移时丢弃旧 query 项（无内容可迁，代价可接受）|
| 只读连接 + AI/EXPLAIN | EXPLAIN 走只读通道不受限；AI 只产文本不触库 |

## 六、验证

1. 单测：`sql-statements.ts`（vitest 未配置，则用 `tsc` + 手工清单替代；后端 Explain 分支用 xUnit + SQLite 真实链路测试）。
2. `cd frontend && pnpm build`（tsc + vite）；`dotnet test backend/tests/...`。
3. 手工验收清单（对照 P0/P1 验收标准，在 My主库(MySQL)/Pg主库(PG)/本地SQLite 三连接逐项过）。
4. OpenAPI 再生成：`pnpm gen:api` 更新 `src/types/openapi.d.ts`。
