# Ark — 多数据库管理与同步工具

单机单用户的 Web 版数据库管理工具（布局参考 Navicat），支持 **PostgreSQL / MySQL / SQLite** 的连接管理、可视化操作，以及**同构 + 异构**的库级/表级同步（仅结构 或 结构+数据），含插件/扩展函数的自动翻译与目标库扩展依赖检查。

## 功能

| 模块 | 说明 |
|---|---|
| 连接管理 | 新建/编辑/测试/删除，密码加密存储（ASP.NET Data Protection），支持只读模式 |
| 对象浏览器 | 连接 → 数据库 → 表/视图 树形浏览（左侧导航，右键菜单） |
| 数据网格 | 分页 / 结构化筛选 / 排序 / 行内编辑（变更集一次性提交）/ 新增行 / 删除行；无主键表自动只读 |
| 表结构设计器 | 列可视化编辑 → **DDL 预览** → 执行（建表/改表 Diff）；索引/外键/原始 DDL 查看 |
| SQL 控制台 | CodeMirror 6 编辑器，Ctrl+Enter 执行，多语句多结果集，行数上限 |
| 结构同步 | 类型映射矩阵 + **插件函数翻译**（now()/uuid()/ifnull()/拼接…）+ 扩展依赖检查（如 pgcrypto/uuid-ossp）；破坏性动作（DROP 等）显式确认 |
| 数据同步 | **全量分批复制**（每批事务 + 自增/序列对齐）与**行级 Diff**（应用层行哈希，跨方言一致；新增/变更/删除） |
| 同步向导 | 5 步向导：源 → 目标 → 范围与选项 → 计划预览（转换警告/扩展依赖/动作勾选）→ 后台执行（进度轮询 + 报告） |
| 导入导出 | CSV / JSON / SQL dump 流式导出；CSV / JSON 导入（目标表不存在时按采样推断类型自动建表） |

## 技术栈

- **后端**：.NET 10 Minimal APIs（`IEndpointFilter` 统一响应包络 `{code, message, data, traceId, timestamp}` + 统一错误码分段）、Npgsql / MySqlConnector / Microsoft.Data.Sqlite（`IDbProvider` 抽象，四层模型 Connection → Database → Schema → Table）、OpenAPI
- **前端**：Vite + React 19 + TypeScript + Tailwind CSS v4 + **shadcn/ui**（全部 UI 组件来自组件库；数据网格 = TanStack Table + shadcn Data Table 模式 + 虚拟滚动）、TanStack Query、Zustand、CodeMirror 6
- **测试**：xUnit 单元测试（类型映射/函数翻译/结构 Diff/行哈希/CSV）+ 真实链路测试（SQLite Provider 端到端同步）+ Testcontainers 跨库集成测试（无 Docker/无网络自动跳过）

## 目录

```
backend/
  src/Ark.Core                      # DTO、包络/错误码、Canonical 元数据模型、类型映射、函数翻译
  src/Ark.Providers.Abstractions    # IDbProvider / IDdlGenerator / 共享 ProviderBase
  src/Ark.Providers.PostgreSQL|MySQL|SQLite
  src/Ark.Sync                      # 结构 Diff、同步计划器、全量/行级Diff 引擎、任务调度、导入导出
  src/Ark.Api                       # Minimal API 端点、统一响应过滤器、ark.db 存储
  tests/                            # 单元 + 真实链路 + Testcontainers
frontend/                           # Vite + React + shadcn/ui
docs/                               # 文档站（Fumadocs，源文件 docs/content/docs）
docker-compose.dev.yml              # 开发用 PostgreSQL 16 + MySQL 8.4
docker-compose.yml                  # 生产部署
```

## 文档

完整文档站基于 [Fumadocs](https://fumadocs.dev) 构建，位于 `docs/`：

```bash
cd docs && npm install && npm run dev   # http://localhost:3000
```

包含：8 篇图文教程（28 张真实界面截图）、系统架构、同步引擎深入、API 参考、生产部署、开发指南与 FAQ。源文件为 `docs/content/docs/*.mdx`，支持全文搜索与 LLM 友好输出（`/llms.txt`）。

## 启动

```bash
# 0) 可选：启动开发数据库
docker compose -f docker-compose.dev.yml up -d

# 1) 后端（http://localhost:5170，OpenAPI: /openapi/v1.json）
cd backend/src/Ark.Api && dotnet run

# 2) 前端（http://localhost:5173，已配置 /api 代理到 5170）
cd frontend
pnpm install
pnpm dev
```

Ark 自身数据（连接配置、同步报告）存放在 `data/ark.db`（SQLite，首次启动自动建表）。

## 测试

```bash
dotnet test backend/tests/Ark.Core.Tests
dotnet test backend/tests/Ark.Sync.Tests
dotnet test backend/tests/Ark.Integration.Tests   # 需要 Docker；网络不可达时自动跳过
cd frontend && pnpm build                          # tsc + vite build
```

## API 摘要

全部 `/api` 端点返回统一包络 `{"code":0,"message":"ok","data":…}`；错误码分段：1xxx 通用 / 2xxx 连接 / 3xxx 元数据与 DDL / 4xxx 数据 / 5xxx 同步。

- `GET|POST|PUT|DELETE /api/connections`、`POST /api/connections/{id}/test`、`POST /api/connections/test`
- `GET /api/connections/{id}/databases`、`.../schemas`、`.../tables`、`.../tables/{table}`
- `POST .../tables`、`PUT .../tables/{table}`、`POST|PUT .../preview`（DDL 预览）、`DELETE .../tables/{table}`
- `POST .../tables/{table}/rows/query`、`.../rows/changes`
- `POST /api/connections/{id}/query`（SQL 控制台）
- `POST /api/sync/plan`、`POST /api/sync/tasks`、`GET /api/sync/tasks/{id}`、`GET /api/sync/reports`
- `POST .../tables/{table}/export`（csv/json/sql 文件流，跳过包络）、`POST .../tables/{table}/import`

### 插件函数翻译

同步计划阶段将源端默认值/生成列表达式识别为语义（当前时间戳/UUID/COALESCE/拼接/字面量），按目标方言输出，例如：

| 语义 | MySQL | PostgreSQL | SQLite |
|---|---|---|---|
| 当前时间戳 | `CURRENT_TIMESTAMP` | `now()` | `CURRENT_TIMESTAMP` |
| UUID | `UUID()` | `gen_random_uuid()` | ✗ 报错提示（无内置函数） |
| 空值合并 | `IFNULL(a,b)` | `COALESCE(a,b)` | `IFNULL(a,b)` |
| 拼接 | `CONCAT(a,b)` | `a \|\| b` | `a \|\| b` |

无法翻译的表达式在同步计划中以 warning/error 显式列出（error 默认忽略该默认值，warning 原样复制请人工确认）；目标端缺失的扩展给出 `CREATE EXTENSION IF NOT EXISTS …` 建议。

### 类型映射

以规范类型为中枢双向映射（`Ark.Core/Typing/TypeMapper`）：无法精确映射时**降级 + 警告**（如 `tinyint(1)`↔BOOLEAN、`jsonb`→`JSON`、SQLite 无强制精度），精度截断在同步计划中显式警告，绝不静默失败。

## v1 边界

- SQLite 仅支持其 ALTER 子集（加列/删列/改名），其余变更提示“需重建表”
- 视图不同步数据；无主键表 Diff 模式自动回退全量、数据网格只读
- 不含：登录/多用户、版本化迁移脚本、断点续传增量同步、SSH 隧道（v1.5 候选）
