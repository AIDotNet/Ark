# Ark 新增 7 个数据库支持 — 实施计划

## 一、目标与范围

在现有 PostgreSQL / MySQL / SQLite 基础上,新增 **7 个数据库**的全功能支持:

| # | 数据库 | 驱动(已核实) | 风格 |
|---|---|---|---|
| 1 | MariaDB | MySqlConnector(复用现有依赖) | MySQL 同源 |
| 2 | SQL Server | Microsoft.Data.SqlClient(MIT) | 独立 |
| 3 | ClickHouse | ClickHouse.Client 7.14.0(MIT,net8,活跃) | 分析型 |
| 4 | DuckDB | DuckDB.NET.Data.Full 1.5.6(MIT,net8/net10,活跃) | 嵌入式分析型 |
| 5 | 达梦 DM8 | ⚠ DmProvider(见风险 R1) | Oracle 风格 |
| 6 | 人大金仓 KingbaseES | Kdbndp_V9 10.0.1(中电科金仓,net8,2026-10 仍在更新) | PG 同源 |
| 7 | openGauss | 官方 Npgsql 分支(openGauss-connector-dotnet);首选直接复用 Npgsql(见 5.7) | PG 同源 |

**每个库全功能对齐**:连接管理、对象浏览器、数据网格(行内编辑/变更集)、表结构设计器(DDL 预览)、SQL 控制台(补全/EXPLAIN)、导入导出(CSV/JSON/SQL)、结构+数据同步。
**同步立即全组合**:任一新库完成即可与现有 10 方言互相同步。
**实施方式**:并行开发(Phase 0 基建串行 → 三个并行组 → Phase 2 矩阵验证串行)。

---

## 二、现状分析(已验证的代码事实)

### 2.1 现有扩展点(架构友好,加库 = 实现接口 + 注册)

| 触点 | 位置 |
|---|---|
| 方言枚举 | `backend/src/Ark.Core/Metadata/Canonical.cs:7` `ArkDialect` |
| Provider 接口 | `backend/src/Ark.Providers.Abstractions/IDbProvider.cs`(约 20 方法)+ `ProviderBase.cs`(485 行共享:连接缓存/行查询/变更集/SQL/DDL 执行) |
| DDL 生成 | `IDdlGenerator.cs`(建表/改表 Diff/索引/外键) |
| 能力矩阵 | `ConnectionSpec.cs:18` `DbCapabilities`(驱动前端禁用与同步降级) |
| 类型映射 | `backend/src/Ark.Core/Typing/TypeMapper.cs`(canonical 中枢,每库一份双向映射,**非 N×N**) |
| 函数翻译 | `backend/src/Ark.Core/Typing/FunctionTranslator.cs`(Recognize/Emit,每库一份) |
| 工厂注册 | `backend/src/Ark.Api/Infrastructure/ProviderRegistry.cs:47` |
| 连接校验 | `backend/src/Ark.Core/Dtos/Dtos.cs:38` `ValidateForDialect` |
| 参数转换 | `backend/src/Ark.Providers.Abstractions/CellValue.cs:74` `ToParam` |

### 2.2 隐藏触点(方言 switch 散落在 Ark.Sync / Ark.Api / 前端,新库每处都要补)

- `backend/src/Ark.Sync/RowOps.cs:98`(参数上限)、`:137`(ON CONFLICT/IGNORE)、`:248`(PG 参数 cast)
- `backend/src/Ark.Sync/DataTransferEngine.cs:255`(ResetPragmas)
- `backend/src/Ark.Sync/SyncTaskManager.cs:448`(FK 开关语句)、`:220/:322/:642`(PG 特判)
- `backend/src/Ark.Sync/SyncPlanner.cs:223/:337/:371`(标识符引用、schema 限定)、`:579`(同构判定)
- `backend/src/Ark.Sync/TableConverter.cs:55/:88`(PG/SQLite 特判)
- `backend/src/Ark.Sync/Exporter.cs:165`(SQL 字面量转义)、`Importer.cs:127`(PG cast)
- 前端 6 处:`frontend/src/types/api.ts:3`(Dialect 联合类型)、`types/openapi.d.ts`(生成物,`pnpm gen:api` 再生)、`ConnectionDialog.tsx:36`(zod + 默认端口 :224)、`ConnectionTree.tsx:48/:321`(默认 schema/database)、`QueryConsole.tsx:186`(CodeMirror 语言)、`CommandPalette.tsx:27`

### 2.3 关键约束

- **同步"全组合"的实现成本是 O(N) 不是 O(N²)**:类型映射与函数翻译都以 canonical 为中枢,每个新库只写自己的一份;N×N 只体现在集成测试组合数(45 对 × 2 方向 = 90 组合)。
- `ark.db` 里 `connections.dialect` 以 long 存储(`ArkRepository.cs:269/:285`):**新枚举值只能追加在末尾**,旧值不变,零迁移。
- `IDbProvider` 是关系型模型(库→Schema→表/行/列),本次 7 库全部适用,无需改模型。

---

## 三、总体分三个阶段

```
Phase 0  基建(串行,先做,三库行为不变回归)
   ↓
Phase 1  七库并行开发(三组并行,每库走同一 checklist)
   ↓
Phase 2  全组合同步矩阵验证 + 文档(串行收尾)
```

---

## 四、Phase 0 — 基建:把"加库"变成填表(一次性)

**目的**:把 §2.2 的 10+ 处散落方言 switch 收敛到一处方言特征表,否则 7 个库 × 10 处 = 70 个散点修改无法并行。

1. **后端 `DialectDescriptor`**(新文件,建议 `Ark.Providers.Abstractions/DialectDescriptor.cs`,或并入 `DbCapabilities` 旁):
   - `QuoteIdentifier(name)`(PG/金仓/openGauss=`"x"`,MySQL/MariaDB=`` `x ``,SQL Server/DM=`[x]`/"x",ClickHouse=`"x"`…)
   - `MaxBatchParams`(MySQL 65535、SQLite 999(现 RowOps 取 900)、SQL Server 2100、ClickHouse/PostgreSQL 不限→大值)
   - `ConflictClause(ConflictMode)`(PG/SQLite/DuckDB=`ON CONFLICT DO NOTHING/UPDATE`,MySQL/MariaDB=`INSERT IGNORE`/`ON DUPLICATE KEY UPDATE`,SQL Server=`MERGE`/`IGNORE_DUP_KEY` 提示,DM=`MERGE`,ClickHouse=去重由引擎决定→仅支持 SkipExisting 语义说明)
   - `FkDisableStatements/FkEnableStatements`(取代 SyncTaskManager.cs:448 硬编码)
   - `ResetPragmas`(取代 DataTransferEngine.cs:255)
   - `SqlLiteral(object)`(取代 Exporter.cs:165)
   - `RequiresFilePath` / `RequiresHost` / `RequiresDatabase` / `DefaultPort`(连接校验与前端表单共用)
   - `SupportsTransactions` / `SupportsConsistentSnapshot`
   - 提供者方式:静态字典按 `ArkDialect` 索引(避免为取 trait 实例化 provider);Ark.Sync 全部改读 descriptor。
2. **前端方言注册表** `frontend/src/features/connections/dialects.ts`:
   `label / icon / defaultPort / needsFilePath / needsDatabase / defaultSchemaFn / codeMirrorDialect / formOrder`,ConnectionDialog、ConnectionTree、QueryConsole、CommandPalette 全部改为查表。
3. **回归**:现有三库 `dotnet test` 全绿 + 前端 `pnpm build` + 手工冒烟(连接/网格/同步向导)。
4. (可选,不阻塞)`ProviderRegistry.Create` 从 switch 改为注册表。

**验收**:三库全部功能无回归;`Ark.Sync`/前端不再有 `ArkDialect.X` 硬编码特判(允许保留的仅:SyncPlanner 同构判定列表、TableConverter 语义特判这类真方言逻辑)。

---

## 五、Phase 1 — 七库并行开发

### 5.0 每个 Provider 的标准 Checklist(三组按此执行,可互不阻塞)

1. `ArkDialect` 枚举**末尾**追加 + `backend/Ark.slnx` 加项目 + `ProviderRegistry` 注册
2. 新建 `backend/src/Ark.Providers.X`(csproj 引驱动包):`XProvider : ProviderBase` + `XDdlGenerator : IDdlGenerator`
3. 实现清单(对应 `IDbProvider`):元数据查询(`ListDatabases/Schemas/Tables/GetTable/GetCreateTableSql/GetViewDefinition`)、`TestAsync`(返回 server version)、`BeginSnapshotAsync`(一致快照会话)、`OpenBulkWriterAsync`(批量装载,降级为多行 INSERT)、`CheckFunctionsAsync`(内置函数依赖)、`ExplainAsync`、`GetCompletionSchemaAsync`、`EstimateRowsAsync`
4. `TypeMapper` 加 `XToCanonical` + `FromCanonical` 的 X 分支(带降级+警告,不静默)
5. `FunctionTranslator` 加 `RecognizeX` + `Emit` 的 X 分支
6. `DialectDescriptor` 填该库条目;`DbCapabilities` 填能力(驱动前端按钮禁用与同步计划降级)
7. `CellValue.ToParam` / `ValidateForDialect` 加该库分支
8. 前端:`api.ts` Dialect 联合类型 → `pnpm gen:api` 再生 openapi.d.ts → `dialects.ts` 注册表条目 → ConnectionDialog 表单校验规则
9. 单测:`TypeMapperTests`/`FunctionTranslatorTests` 补该库用例
10. 集成测试:`Ark.Integration.Tests` 加该库 Testcontainers 用例(或本地文件链路测试);`docker-compose.dev.yml` 加该库服务
11. README 功能表/文档站更新

### 5.1 A 组 — MariaDB + SQL Server

**MariaDB**(工作量最小,建议全组最先完成,用它校验 Phase 0 抽象是否成立):
- 驱动直接复用 `MySqlConnector`;`MySqlProvider` 可提取共享基类或组合复用(元数据 SQL、LOAD DATA 批量写、information_schema 查询基本相同)
- 差异点:`uuid` 原生类型(10.7+)、JSON 实为 LONGTEXT 别名、`SHOW CREATE VIEW` 输出差异、系统库过滤(`mysql`/`information_schema`/`performance_schema`/`sys`)
- 测试:`Testcontainers.MariaDb`(镜像 `mariadb:11`);CodeMirror 用 `mariadb` 方言

**SQL Server**:
- 驱动 `Microsoft.Data.SqlClient`(MIT);默认 Schema `dbo`,`SupportsSchemas=true`
- 类型映射:`bit`→Boolean、`uniqueidentifier`→Guid、`datetime2/datetimeoffset/datetime/smalldatetime`、`nvarchar/nchar`(长度按字符)、`varbinary(max)`→Binary、`money/smallmoney`→Decimal(19,4)、`sql_variant`/`hierarchyid`/`geography`→Unknown+警告
- 函数翻译:`GETDATE()/SYSDATETIME()`→CurrentTimestamp、`NEWID()`→UuidV4、`ISNULL(a,b)`→Coalesce、`+` 拼接→Concat
- 批量写:`SqlBulkCopy` 实现 `IBulkWriter`;快照:`READ COMMITTED SNAPSHOT` 或 `TABLOCKX` 说明(取可用者)
- EXPLAIN:`SET SHOWPLAN_ALL ON` 输出文本化(不执行语句)
- 标识符:`[x]` 转义;分页 `OFFSET ... FETCH`
- 测试:`Testcontainers.MsSql`(镜像 `mcr.microsoft.com/mssql/server:2022`,注意镜像 EULA 与内存需求 ≥2GB)

### 5.2 B 组 — ClickHouse + DuckDB

**ClickHouse**(类型系统与事务语义是本计划最大改动点):
- 类型映射(全部显式降级+警告):`UInt8..UInt64`→就近加宽 Int16/32/64 + 警告;`Array(T)/Map(K,V)/Tuple/Nested`→Json(建议)或 Unknown,数据网格只读该列;`LowCardinality(T)`→按内层 T;`Enum8/16`→VarChar;`FixedString(N)`→Char(N);`IPv4/IPv6`→VarChar;`UUID`→Guid;`Decimal(P,S)`;`Date/Date32/DateTime/DateTime64`
- `Nullable(T)` 与 canonical Nullable 组合:元数据读取时展开,DDL 生成时按目标方言决定是否包 `Nullable(...)`
- 表模型:`MergeTree` 系引擎,`ORDER BY` 元组≈主键(决定网格可编辑性:无 ORDER BY 表只读,规则与无主键表一致);`SupportsTruncate=false`(TRUNCATE 有但慎用)→ 能力表如实填写
- **无 ACID 事务**:`SupportsConsistentSnapshot=false`(快照会话降级为普通连接+警告)、变更集提交无单事务回滚 → `ApplyChangesResponse` 警告字段注明"目标无事务,失败不回滚";同步引擎按批次失败即停+报告(现有报告机制已支持)
- 批量写:ClickHouse.Client 自带 bulk insert(RowBinary 流)
- EXPLAIN:`EXPLAIN AST/PLAN/PIPELINE` 文本化
- 测试:`Testcontainers.ClickHouse`(镜像 `clickhouse/clickhouse-server`)

**DuckDB**(与 SQLite 同为嵌入式,大量模式可参照):
- 连接:FilePath(前端表单复用 SQLite 的文件路径模式);驱动 net8/net10 与后端 net10 匹配
- 类型映射:`INTEGER/BIGINT/DOUBLE/DECIMAL(P,S)/VARCHAR/BLOB/DATE/TIMESTAMP/TIMESTAMP WITH TIME ZONE/UUID/JSON`;`LIST/STRUCT/MAP/UNION/ENUM`→Json 或 Unknown+警告(网格该列只读)
- ALTER:支持 ADD/DROP COLUMN、ALTER TYPE、RENAME → `DbCapabilities` 如实填,设计器可直接用(v1.0+ 能力)
- 快照:支持事务(`BEGIN TRANSACTION`),`SupportsConsistentSnapshot=true`(单连接文件内一致)
- 批量写:DuckDB.NET Appender API
- 测试:无需容器,本地临时文件链路测试(参照现有 SQLite 端到端测试);`docker-compose.dev.yml` 不加服务

### 5.3 C 组 — 达梦 DM8 + 人大金仓 + openGauss

**达梦 DM8**:
- ⚠ 驱动见 R1:从 DM8 安装包/eco.dameng.com 取 netstandard2.1/net6 版 `DmProvider.dll`,vendor 进 `backend/lib/dm/`,`csproj` 用 `Reference Include` 引用;README 写明来源与许可
- 元数据:Oracle 兼容视图 `ALL_TABLES/ALL_TAB_COLUMNS/ALL_INDEXES/ALL_CONSTRAINTS`(DM8 支持),无 information_schema;`SupportsMultipleDatabases=false`(单库多模式,`SupportsSchemas=true`,Schema≈用户)
- 类型:`NUMBER(P,S)`→Decimal、`VARCHAR2/NVARCHAR2`→VarChar、`CLOB/NCLOB`→Text、`BLOB`→Binary、`DATE`(含时间)→DateTime、`TIMESTAMP/TIMESTAMP WITH TIME ZONE`、`BIT`→Boolean、`RAW`→Binary、`INTERVAL`→Unknown+警告
- 函数:`SYSDATE`→CurrentTimestamp、`SYSGUID()`→Uuid(映射 UuidV4 语义+警告)、`NVL`→Coalesce、`||`→Concat
- 分页 `ROWNUM`/`OFFSET...FETCH`;标识符默认大写(读元数据时统一大小写处理,对齐现有 canonical 命名策略)
- 批量写:DmProvider 批量绑定;快照:DM 事务隔离(READ COMMITTED + 只读事务)
- EXPLAIN:`EXPLAIN <sql>` 文本
- 测试:GenericContainer + 达梦官方 dm8 镜像(eco.dameng.com 提供 tar 或 `dm8_single` 镜像;无 Testcontainers 专用模块)

**人大金仓 KingbaseES**:
- 驱动 `Kdbndp_V9`(NuGet,net8)⚠ 仅支持 **PG 兼容模式**(见 R2),连接测试时检测 `show database_mode` 并在不符时给出明确错误
- 复用策略:新建 `KdbndpProvider`,可考虑与 PostgreSQL Provider 抽共享基类(PG 系共享元数据 SQL;pg_catalog 在 KES 保持兼容),差异点单独覆写(系统 schema 名 `sys_catalog`、`sys_class` 等 V9 前缀差异需 spike 确认)
- 类型映射:调用现有 `PgToCanonical` + 金仓差异覆盖;函数翻译同(Emit 目标方言=KingbaseES 时输出 PG 语法)
- 测试:GenericContainer + 金仓官方镜像(kingbase_v009r001c*,需从金仓官网/生态站获取;无 Testcontainers 模块)

**openGauss**:
- 首选方案(先 spike 验证):**直接用现有 Npgsql 连 openGauss**——openGauss 基于 PG 9.2+/内核,pg_catalog 兼容度高;风险点是 sha256 认证(openGauss 5+ 支持 md5 兼容模式可配)与少量系统视图差异(`gs_` 前缀)
- 若 Npgsql 不可行:vendor 官方 Npgsql 分支(openGauss-connector-dotnet,GPL 需评估许可传染性)再引用
- Provider 结构:与金仓同为"PG 派生"策略,差异点覆写(如 `pg_catalog` 查询中 openGauss 特有列、`myschema` 行为)
- 测试:GenericContainer + `opengauss/gaussdb` 或 `opengauss/opengauss` 官方镜像

### 5.4 并行组划分与顺序

| 组 | 库 | 预估(每库) | 建议顺序 |
|---|---|---|---|
| A | MariaDB → SQL Server | 0.5~1 周 / 1~1.5 周 | MariaDB 先(校验 Phase 0) |
| B | DuckDB → ClickHouse | 1 周 / 1.5~2 周 | DuckDB 先(嵌入式,模式熟) |
| C | 金仓 → openGauss → DM8 | 1 周 / 1 周 / 1.5~2 周 | PG 系两个先做,DM8 最后(驱动风险) |

> 三组各自第一个库完成后做一次 15 分钟交叉评审(对照 checklist),确认 descriptor 抽象没有漏项,再放量做后面的库。

---

## 六、Phase 2 — 全组合同步矩阵验证(串行)

1. **自动化矩阵**:`Ark.Integration.Tests` 增加"任意源→任意目标"参数化同步测试(结构同步 + 全量数据 + 行级 Diff 各一),源/目标从已启用的容器方言取。全组合 = 10 方言 45 对 × 2 方向 × 3 类同步。
   - CI 只跑冒烟子集(每新库 ↔ 三库);全矩阵提供 `ARK_MATRIX=1` 开关夜间/手动跑(容器成本控制)
2. **警告矩阵人工抽查**:每个新库输出一份"类型映射降级表 + 函数翻译表"对照 README 现有三库表格的格式,进文档站
3. **回归三库**:全部既有测试保持绿
4. **文档**:README 功能表、文档站(API/教程/FAQ)、`docker-compose.dev.yml` 注释、连接字段表

---

## 七、验证方式(每阶段)

- 后端:`dotnet test backend/tests/Ark.Core.Tests && dotnet test backend/tests/Ark.Sync.Tests && dotnet test backend/tests/Ark.Integration.Tests`(集成需 Docker,无 Docker 自动跳过——保留此约定)
- 前端:`cd frontend && pnpm build`(tsc 类型检查含新生成的 openapi.d.ts)
- 手工冒烟(每库 checklist 第 11 步):新建连接 → 测试 → 浏览表 → 网格编辑提交 → 设计器改列 + DDL 预览执行 → SQL 控制台补全 + EXPLAIN → 与 PostgreSQL 做一次结构+数据同步(计划预览含警告)→ CSV 导入导出

## 八、数据兼容与迁移

- `ark.db` 的 `connections.dialect` 为 long 枚举:新值**只追加在枚举末尾**,零迁移、旧连接不受影响
- 密码保护、只读模式、同步报告存储均与方言无关,无需改动
- 前端 openapi.d.ts 由 `pnpm gen:api` 再生,不手改

## 九、风险与决策记录

| # | 风险/决策 | 对策 |
|---|---|---|
| R1 | **DM8 驱动**:NuGet `DmProvider` 2.0.0 为 2017 年 .NET Framework 版,不可用 | Phase 1 开始前先做驱动 spike:取 DM8 安装包内 netstandard DLL vendor 进仓库;若 netstandard 版不存在,DM8 降级为"仅浏览+查询"(告知用户后另行决策) |
| R2 | 金仓多兼容模式(PG/Oracle/MySQL/SQLServer) | v1 仅支持 PG 兼容模式,连接测试时检测并明确报错提示;Oracle 模式列入"不做"边界 |
| R3 | openGauss 用 Npgsql 的可行性(认证/系统视图差异) | Phase 1 首个 spike;备选官方 Npgsql 分支(注意 GPL 许可评估) |
| R4 | ClickHouse 无事务:同步引擎"每批事务+失败回滚"语义不成立 | Capabilities 声明,引擎按批次降级:失败即停+报告已写入批次数,计划预览显式警告 |
| R5 | 达梦/金仓/openGauss 无 Testcontainers 官方模块 | `GenericContainer` + 官方镜像封装辅助类;镜像不可得时该库集成测试降级为手动步骤文档 |
| R6 | 全组合矩阵容器成本(45 对) | CI 冒烟子集 + `ARK_MATRIX=1` 夜间全量 |
| R7 | 前端 10 方言表单分支膨胀 | Phase 0 注册表驱动,禁止再写 per-dialect 条件分支 |
| R8 | SQL Server 容器镜像体积/EULA(mcr.microsoft.com/mssql) | 仅测试依赖,不进生产镜像;CI 缓存镜像 |

## 十、明确不做(本期边界)

- 金仓 Oracle/MySQL/SQLServer 兼容模式;DM8 的 DBLink/实例管理等 Oracle 特有能力
- ClickHouse 分布式表/集群/物化视图管理;各库存储过程、函数、触发器的可视化管理(延续 v1 边界)
- MongoDB/Redis 等非关系型(模型不适配,需要另行立项)
- 增量同步/断点续传(v1.5 候选,维持不变)
