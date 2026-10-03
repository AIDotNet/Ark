# Ark — 多数据库管理与同步工具 实施计划

> 单机单用户的 Web 版数据库管理工具（布局参考 Navicat），支持 PostgreSQL / MySQL / SQLite 的连接管理、可视化操作、以及**同构 + 异构**的库级/表级同步（仅结构 或 结构+数据），含插件/扩展函数的翻译与依赖检查。

---

## 1. 已确认的关键决策

| 决策点 | 结论 |
|---|---|
| 部署形态 | 纯 Web 应用（浏览器访问，前后端独立部署） |
| 用户体系 | 单机单用户，无登录；连接配置存服务端 |
| v1 模块 | 连接管理 + 对象浏览器、表结构设计器、数据网格、SQL 查询控制台、结构同步 + 数据同步、导入导出（全部必含） |
| 同步组合 | 同构 + 异构互转（MySQL ↔ PgSQL ↔ SQLite 两两互转） |
| 插件函数 | 自动翻译（函数映射表）+ 目标库扩展依赖检查与提示 |
| 数据同步策略 | 全量分批复制 + 行级 Diff（PK 对比：新增/变更/删除） |
| 迁移含义 | 同步即迁移（库到库传输），不做版本化迁移脚本管理 |
| 数据网格 | shadcn/ui Data Table 模式（TanStack Table 内核 + shadcn 样式） |

## 2. 技术栈

**后端**（`backend/`）
- .NET 10（`net10.0`）Minimal APIs，无 Controller
- NuGet：`Npgsql`、`MySqlConnector`（官方推荐，替代 MySql.Data）、`Microsoft.Data.Sqlite`
- 统一响应：`IEndpointFilter`（`MapGroup().AddEndpointFilter<T>()` 挂根组）
- 校验：.NET 10 Minimal API 内置 Validation（`AddValidation()`，基于 DataAnnotations）；若 GA 版不可用则回退到过滤器内手工校验
- API 文档：`Microsoft.AspNetCore.OpenApi`（`AddOpenApi()`），导出 `swagger.json` 供前端生成类型
- 测试：xUnit + `Testcontainers`（Pg/MySQL 集成测试）；SQLite 用临时文件

**前端**（`frontend/`，包管理器 pnpm）
- Vite + React 19 + TypeScript（strict）
- Tailwind CSS v4（`@tailwindcss/vite` 插件，`src/index.css` 仅 `@import "tailwindcss"`）
- shadcn/ui（官方 Vite 流程：`tsconfig` 加 `@/*` 别名 → `vite.config.ts` 加 alias → `pnpm dlx shadcn@latest init` → `pnpm dlx shadcn@latest add <component>`）。**规则：UI 一律使用 shadcn 组件或其基础组件的组合，不引入其他组件库**
- 数据层：TanStack Query v5（服务端状态）+ Zustand v5（UI 状态：标签页、树展开、活动连接）
- 表格：TanStack Table v8 + `@tanstack/react-virtual`（虚拟滚动），套 shadcn Data Table 样式
- SQL 编辑器：CodeMirror 6（`@codemirror/lang-sql`）——shadcn 无编辑器组件，属"基础组件组合"的例外，外壳（工具栏/按钮/面板）仍全部 shadcn
- 表单：react-hook-form + zod（shadcn Form 组件）
- 类型生成：`openapi-typescript` 读取后端 swagger.json 生成 API 类型

**开发环境**：`docker-compose.dev.yml` 起 PostgreSQL 16 + MySQL 8 供开发与集成测试；SQLite 用本地文件，无需容器。

## 3. 仓库结构

```
Ark/
├── backend/
│   ├── Ark.sln
│   ├── src/
│   │   ├── Ark.Api/                     # Minimal API 端点、过滤器、Program.cs
│   │   ├── Ark.Core/                    # DTO、统一响应体、错误码、Canonical 元数据模型
│   │   ├── Ark.Providers.Abstractions/  # IDbProvider、IDdlGenerator、ITypeMapper、IFunctionTranslator
│   │   ├── Ark.Providers.PostgreSQL/
│   │   ├── Ark.Providers.MySQL/
│   │   ├── Ark.Providers.SQLite/
│   │   └── Ark.Sync/                    # 结构 Diff、数据全量/Diff 同步引擎、任务调度
│   └── tests/
│       ├── Ark.Core.Tests/              # 类型映射、函数翻译、Canonical 模型
│       ├── Ark.Sync.Tests/              # 结构 Diff、数据 Diff（内存 SQLite 即可跑）
│       └── Ark.Integration.Tests/       # Testcontainers：三库互转 round-trip
├── frontend/
│   ├── src/
│   │   ├── components/ui/               # shadcn 生成组件（不手改核心逻辑）
│   │   ├── components/                  # 布局壳、连接树、数据网格、DDL 预览等业务组件
│   │   ├── features/
│   │   │   ├── connections/             # 连接管理对话框、测试连接
│   │   │   ├── explorer/                # 对象树
│   │   │   ├── designer/                # 表结构设计器
│   │   │   ├── grid/                    # 数据网格
│   │   │   ├── query/                   # SQL 控制台
│   │   │   ├── sync/                    # 同步向导 + 任务报告
│   │   │   └── io/                      # 导入导出
│   │   ├── lib/                         # api client（统一解包响应体）、cn、utils
│   │   ├── stores/                      # zustand：workspace（标签页/树状态）
│   │   └── types/                       # openapi-typescript 生成
│   └── package.json
├── docker-compose.dev.yml               # pg16 + mysql8
└── README.md
```

## 4. 后端设计

### 4.1 统一响应体与过滤器

响应包络（`Ark.Core/Responses/ApiEnvelope.cs`）：

```csharp
public record ApiEnvelope<T>(
    int Code,          // 0 = 成功；非 0 = 业务错误码（分段见下）
    string Message,
    T? Data,
    string TraceId,
    DateTimeOffset Timestamp);
```

- `UnifiedResponseFilter : IEndpointFilter`：挂在 `app.MapGroup("/api")` 根组。端点只返回业务 DTO / `TypedResults`，过滤器在 `await next(ctx)` 之后把 `Ok<T>`/JSON 结果统一包成 `ApiEnvelope<T>`；错误 `Results.Problem`/异常转 `ApiEnvelope`（Code=错误码，HTTP 状态仍保留）。
- 跳过包装：文件流（导入导出下载）等原始响应，用标记接口 `IRawResult` 或端点元数据扩展 `.SkipEnvelope()` 识别后原样放行。
- 全局异常：`IExceptionHandler` 把未知异常转 `ApiEnvelope(Code=1000, Message=...)`；业务异常 `ArkException(code, message)` 由过滤器/异常处理器映射。
- 错误码分段：`1xxx` 通用、`2xxx` 连接、`3xxx` 元数据/DDL、`4xxx` 数据、`5xxx` 同步。
- 请求校验：DTO 用 DataAnnotations，注册 .NET 10 `AddValidation()`，校验失败返回 `Code=1400`。

### 4.2 端点组织（模块化 Minimal API）

每个模块一个静态扩展方法，如 `Ark.Api/Endpoints/ConnectionEndpoints.cs`：

```csharp
public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder app)
{
    var g = app.MapGroup("/connections").WithTags("Connections");
    g.MapGet("/", Handler).AddEndpointFilter<...>();
    ...
}
```

模块：`Connections`、`Metadata`（对象树）、`Tables`（设计器/DDL）、`Rows`（数据网格）、`Query`（SQL）、`Sync`、`Export`/`Import`。

### 4.3 数据库抽象层（核心）

**统一层级模型**（`Ark.Core/Metadata/Canonical`）：

```
Connection → Database → Schema → Table/View → Column / Index / ForeignKey / Sequence
```

- PgSQL：Server → Database → Schema（真实 schema）→ Table
- MySQL：Server → Database（schema == database，一一对应）→ Table
- SQLite：文件 → Database（固定 `main`）→ Table；无序列/存储过程/函数对象

`CanonicalTable` 为中间表示：列（名称、规范类型、长度/精度、可空、默认值【表达式标记】、自增、注释、生成列）、索引、外键、引擎/字符集（MySQL 专属，放 ProviderOptions 字典）。

**`IDbProvider`**（每个 Provider 实现一份）：

```csharp
public interface IDbProvider
{
    DbCapabilities Capabilities { get; }              // 支持 schema? 序列? ALTER 能力矩阵...
    Task OpenAsync(ConnectionString cs, CancellationToken ct);
    Task<IReadOnlyList<DbNode>> ListDatabasesAsync(...);          // pg: pg_database / my: SHOW DATABASES / sq: main
    Task<IReadOnlyList<TableRef>> ListTablesAsync(db, schema);    // 过滤系统表
    Task<CanonicalTable> GetTableAsync(db, schema, table);        // 列/索引/外键/DDL 佐证
    Task<string> GetCreateTableSqlAsync(db, schema, table);       // SHOW CREATE TABLE / pg_dump 风格重建 / sqlite_master.sql
    Task ExecuteDdlAsync(IReadOnlyList<string> statements);       // 逐条执行，返回每条结果
    IAsyncEnumerable<DataBatch> ReadRowsAsync(TableRef, order, offset/limit);
    Task WriteRowsAsync(TableRef, RowChangeSet changes);          // 参数化批量 INSERT/UPDATE/DELETE
}
```

元数据来源：PgSQL 查 `pg_catalog`/`information_schema`；MySQL 查 `information_schema`（表/列/索引/外键/引擎/字符集）；SQLite 用 `sqlite_master` + `PRAGMA table_info/index_list/index_info/foreign_key_list`。

**类型映射**（`Ark.Core/Typing/TypeMapper`）：以"规范类型"为中枢（整数、长整数、小数、字符串定长/变长、文本、布尔、日期、时间、时间戳、二进制、UUID、JSON、数组…），三库各有一张 `→ 规范` 与 `规范 →` 映射表。无法精确映射时按"降级 + 警告"处理（如 PgSQL `jsonb` → MySQL `JSON` 可、`text[]` → 无对应 → 降级 `JSON`/`text` 并警告）。精度收窄（如 `numeric(20)` → MySQL `decimal(20)` 超限）在同步计划中列为**截断警告**。

**函数翻译**（`Ark.Core/Typing/FunctionTranslator`）：仅作用于**默认值表达式与生成列**（数据本体不翻译）。内置双向映射表（语义键 → 各方言写法）：

| 语义 | MySQL | PostgreSQL | SQLite |
|---|---|---|---|
| 当前时间戳 | `CURRENT_TIMESTAMP` / `NOW()` | `now()` / `CURRENT_TIMESTAMP` | `CURRENT_TIMESTAMP` / `datetime('now')` |
| 当前日期 | `CURRENT_DATE` / `CURDATE()` | `CURRENT_DATE` | `date('now')` |
| UUID | `UUID()` | `gen_random_uuid()` | ✗（无内置，报错提示） |
| 空值合并 | `IFNULL(a,b)` | `COALESCE(a,b)` | `IFNULL(a,b)` |
| 字符串拼接 | `CONCAT(a,b)` | `a \|\| b` | `a \|\| b` |

不可翻译的默认值：同步计划中该列标记 `error` 并明示"默认值无法自动翻译"，用户可跳过该列/人工改写后继续。

**扩展依赖检查**（`Ark.Providers.*.ExtensionChecker`）：生成同步计划时收集目标端所需能力（UUID 函数等）——PgSQL 查 `pg_extension`/`pg_available_extensions`，缺失时给出 `CREATE EXTENSION IF NOT EXISTS ...` 建议（可选勾选"自动执行"）；MySQL 检查目标函数是否可用；SQLite 无扩展体系，直接在计划中标注不支持项。

### 4.4 同步引擎（`Ark.Sync/`）

**结构同步**（`StructureDiffer`）：
1. 读源端选中表的 `CanonicalTable` → 类型映射 + 函数翻译 → 目标端规范模型；
2. 读目标端现状 → 逐表 Diff：新建表 / 删除表 / 列增删改 / 索引 / 外键变化 → `DdlAction { Kind, Sql, Warnings[] }` 列表；
3. 破坏性操作（DROP TABLE/COLUMN、类型收窄、主键变更）一律打 `Warnings` 并默认不勾选，需用户显式确认；
4. 目标端 DDL 生成器输出语句（MySQL/PgSQL 支持完整 ALTER；SQLite 仅支持其 ALTER 子集——加列/改名，其余标记"需重建表，v1 提示不支持"）；
5. 计划预览 → 用户确认 → 逐语句执行，逐条反馈成功/失败。

**数据同步**（`DataTransferEngine`）：
- **全量模式**：目标表先（可选）TRUNCATE → 源端 `SELECT * ORDER BY pk` 分批（默认 1000 行/批，可配）→ 批量参数化 INSERT（PgSQL 可走 `COPY binary`，MySQL 走多值 INSERT，SQLite 走事务内 prepared batch）→ 每批一个事务，失败停在当前表可整表重试。
- **行级 Diff 模式**：按 PK 顺序分批拉取两侧数据，在**应用层**统一计算行哈希（列名排序拼接 + SHA-256，避免三库 hash 函数差异）→ 源有目标无→INSERT；都有但哈希不同→UPDATE；目标有源无→DELETE（可选勾选）→ 生成变更批次执行。无主键表：Diff 模式禁用并警告，仅可全量。
- **自增/序列对齐**：同步完成后 PgSQL `setval(max)`、MySQL `ALTER TABLE ... AUTO_INCREMENT = max`，SQLite 免处理（rowid）。
- 值转换走统一 `ValueConverter`（布尔↔tinyint、日期时区规范 UTC、二进制 bytea/blob/blob、UUID↔char(36) 等）。

**任务模型**：`POST /api/sync/tasks` 返回 `taskId`，后台 `Channel` 队列执行，内存态进度；`GET /api/sync/tasks/{id}` 轮询：状态/百分比/当前表/错误列表；结束后生成报告（每表：DDL 变更数、插入/更新/删除行数、耗时、警告），报告落盘最近 N 次到 Ark 自身库。

### 4.5 导入导出

- 导出（流式，`Content-Disposition` 下载，跳过包络包装）：CSV、JSON（数组流式写出）、SQL dump（建表语句 + 分批 INSERT）。
- 导入：上传 CSV/JSON → 目标表存在则列映射，不存在则按采样推断类型建表（走设计器 DDL 通道）→ 分批插入 → 返回行数/错误报告。SQL dump 导入不在 v1。

### 4.6 Ark 自身存储与安全

- 自身数据用 SQLite 单文件 `data/ark.db`（`Microsoft.Data.Sqlite` + 裸 ADO，不引 EF）：`connections`、`sync_reports` 两张表，启动时自动建表迁移（`CREATE TABLE IF NOT EXISTS`）。
- 连接密码用 ASP.NET Core Data Protection 加密落盘（purpose 字符串隔离）。
- 所有用户数据操作强制参数化；SQL 控制台属工具性质不拦截多语句，但每连接提供"只读模式"开关（拒绝非 SELECT 的 DML/DDL）与执行超时（默认 30s）。

## 5. 前端设计

### 5.1 布局（参考 Navicat）

```
┌────────────────────────────────────────────────────────────────┐
│ 顶栏: [连接▾] [新建连接] [同步] [导入/导出]   …当前标签上下文工具栏 │
├──────────────┬─────────────────────────────────────────────────┤
│  左侧对象树   │  标签页: [表: users] [表: orders] [查询-1] [同步] │
│  (Sidebar +  │ ┌─────────────────────────────────────────────┐ │
│  可搜索/右键) │ │  子标签: 对象 | 列 | 索引 | 外键 | DDL        │ │
│              │ │  主内容区（网格 / 设计器 / 编辑器 / 向导）      │ │
│  连接A        │ │                                             │ │
│   └ db       │ │                                             │ │
│     └ 表/视图 │ │                                             │ │
├──────────────┴─────────────────────────────────────────────┤
│ 消息/日志面板（可折叠: SQL 日志、同步报告摘要、错误）               │
├─────────────────────────────────────────────────────────────┤
│ 状态栏: 连接名 · 库 · 执行耗时 · 行数 · 只读模式                  │
└─────────────────────────────────────────────────────────────┘
```

- 组件对应：`Sidebar`、`Resizable`（左树/主区/底栏拖拽）、`Tabs`（顶部标签 + 对象子标签）、`ScrollArea`、`Separator`、`ContextMenu`（树右键）、`Command`（Ctrl+K 面板，可选）。
- 对象树用 shadcn `Collapsible` + `ContextMenu` 组合实现（shadcn 无原生 Tree）。

### 5.2 功能模块 → shadcn 组件映射（约束：必须用组件库）

| 功能 | shadcn 组件 |
|---|---|
| 连接管理 | `Dialog` + `Form/Input/Select/Switch/Alert/Button`，测试连接结果用 `Alert` |
| 对象树 | `Sidebar` + `Collapsible` + `ContextMenu` + `Input`(过滤) + `Badge` |
| 表对象列表 | `Table` + `DropdownMenu`（右键动作）+ `Empty` |
| 表设计器 | 子标签 `Tabs` + 行编辑网格（TanStack）+ `Select`(类型) + `Switch`(可空) + `Tooltip` + `Dialog`(DDL 预览，CodeMirror 只读高亮) |
| 数据网格 | shadcn Data Table 模式：`Table` + `Checkbox`(行选) + `Pagination` + `Popover`(列筛选) + `DropdownMenu`(排序) + 虚拟滚动 + 单元格编辑（`Input/Select/Checkbox/DatePicker` 按列类型）+ 未保存变更 `Badge` |
| SQL 控制台 | CodeMirror 6（编辑器本体）+ shadcn `Button/Splitter-Resizable/Spinner` + 结果集复用数据网格 + 多结果集 `Tabs` + `Alert`(报错) |
| 同步向导 | 分步卡片（`Card` + `Button` 上一步/下一步）+ 树形勾选（`Checkbox` 列表）+ 选项 `Select/Switch` + 计划预览 `Table` + 破坏项红字 `Badge variant="destructive"` + 执行进度 `Progress` + 报告页 `Table/Alert` |
| 导入导出 | `Dialog` + `Select`(格式) + 进度 `Progress` + `Toast`（sonner） |
| 全局 | `Toast`（sonner）、`Skeleton`、`Spinner`、`Tooltip`、`Sheet`（小屏抽屉）、暗色模式（`next-themes` 等价实现） |

### 5.3 前端数据层约定

- `lib/api.ts`：fetch 封装，统一解包 `ApiEnvelope`——`code !== 0` 抛 `ArkApiError`，TanStack Query `onError` 统一 Toast。
- 所有列表接口带分页（`limit/offset`）；数据网格查询为 POST 请求体（筛选/排序结构化传参）。
- Zustand store：`openTabs[]`、`activeTabId`、树展开状态、各连接最近使用库/表。

## 6. API 契约（`/api` 前缀，全部包络包装）

| 端点 | 方法 | 说明 |
|---|---|---|
| `/connections` | GET/POST | 列表 / 新建（密码只写不回显） |
| `/connections/{id}` | PUT/DELETE | 修改 / 删除 |
| `/connections/{id}/test` | POST | 测试连接（含服务器版本） |
| `/connections/{id}/databases` | GET | 库列表 |
| `/connections/{id}/databases/{db}/schemas/{schema}/tables` | GET | 表/视图列表（含行数估算） |
| `/connections/{id}/.../tables/{table}` | GET | 列/索引/外键/原始 DDL |
| `/connections/{id}/.../tables` | POST | 建表（Canonical 模型 → DDL 预览 + 执行） |
| `/connections/{id}/.../tables/{table}` | PUT | 改表（现模型 vs 新模型 Diff → DDL 预览 + 执行） |
| `/connections/{id}/.../rows/query` | POST | 网格查询：筛选/排序/分页 |
| `/connections/{id}/.../rows/changes` | POST | 提交变更集（INSERT/UPDATE/DELETE，单事务） |
| `/connections/{id}/query` | POST | SQL 执行 → `results[]`（多结果集 + 受影响行数 + 耗时） |
| `/sync/plan` | POST | 生成同步计划（源/目标/范围/模式 → DDL 动作 + 警告 + 扩展依赖） |
| `/sync/tasks` | POST / GET | 提交执行 / 任务列表 |
| `/sync/tasks/{id}` | GET | 进度与报告 |
| `/connections/{id}/.../export` | POST | 导出 CSV/JSON/SQL（原始流） |
| `/connections/{id}/.../import` | POST | 导入 CSV/JSON（multipart） |

## 7. 里程碑（每档可独立验收）

- **M0 骨架**：仓库结构、`docker-compose.dev.yml`、后端 sln + Program + 统一响应过滤器（含异常处理与错误码段）+ OpenAPI、前端 Vite+shadcn 初始化（Button/Card 验证）、api 类型生成脚本。
- **M1 连接与浏览**：连接 CRUD + 测试 + 密码加密；三库元数据读取；对象树 UI + 表对象列表。
- **M2 数据网格**：分页/筛选/排序/虚拟滚动；行内编辑 + 变更集提交（PK 定位、类型化编辑器）。
- **M3 表设计器**：Canonical 模型编辑 UI；建表/改表 Diff → DDL 预览 → 执行；SQLite ALTER 能力边界提示。
- **M4 SQL 控制台 + 导入导出**：CodeMirror 编辑器、多结果集、只读模式开关；CSV/JSON/SQL 导出、CSV/JSON 导入。
- **M5 结构同步**：向导（源→目标→选表→模式[仅结构/结构+数据]）；类型映射 + 函数翻译 + 扩展检查；DDL 动作预览（破坏项确认）→ 执行报告。
- **M6 数据同步**：全量分批 + 行级 Diff（应用层哈希）；自增/序列对齐；后台任务 + 进度轮询 + 报告落盘。
- **M7 打磨**：暗色模式、快捷键、大表（100w 行）虚拟滚动与分批同步压测、错误文案。

## 8. 边界与风险

| 风险 | 处理 |
|---|---|
| SQLite ALTER 能力弱 | 只支持子集，其余在计划中标记"需重建表（v1 不支持）"，不静默失败 |
| 异构类型精度/语义丢失 | 映射表逐条标注降级策略；截断风险在同步计划以警告显式列出 |
| 函数无法翻译 | 该列标 `error`，允许跳过列或人工改写后继续 |
| 无主键表 | Diff 不可用仅全量；网格编辑不可用（只读），UI 明示 |
| 大表内存 | 网格强制分页；同步按 PK 游标分批流式读写，哈希在应用层逐批计算 |
| MySQL utf8mb4/引擎差异 | 连接默认 `utf8mb4`；引擎/字符集放入 ProviderOptions，同步计划中提示 |
| PgSQL schema vs MySQL database | 统一四级模型，MySQL schema==database、SQLite 固定 `main`，UI 对无 schema 的库隐藏该层 |
| 长事务/批失败 | 每批独立事务；报告精确到批；表级整表重试（v1 不做断点续传） |

## 9. 验证方案

- **单元测试**：类型映射全矩阵（三库 × 规范类型 × 降级）、函数翻译表、结构 Diff（含破坏项标记）、行级 Diff 三类变更判定。
- **集成测试**（Testcontainers：pg16 + mysql8，SQLite 临时文件）：三库两两 round-trip（建样例库 → 结构+数据同步 → 目标端校验行数/类型/默认值）；扩展检查（PgSQL 缺 uuid 扩展场景）。
- **API 测试**：包络包装（成功/业务错/异常/原始流跳过）、校验失败码。
- **手动验收清单**：按 M1–M7 各写 3–5 条 Navicat 对标操作（连接→浏览→改表→编辑数据→跑 SQL→同步向导全流程→导出导入）。

## 10. 明确不在 v1 范围

- 登录/多用户/团队共享、版本化迁移脚本（Flyway 式）、视图/存储过程/触发器设计器、断点续传式增量同步（时间戳 watermark）、SQL dump 导入、SSH 隧道连接（列为 v1.5 候选）。
