# API 参考

Base URL：`http://localhost:5170`（开发）。全部端点挂在 `/api` 组下；OpenAPI 文档（Development 环境）：`/openapi/v1.json`，前端类型由 `pnpm gen:api` 生成。

## 统一响应包络

```json
{ "code": 0, "message": "ok", "data": { }, "traceId": "…", "timestamp": "…" }
```

- `code = 0` 成功；非 0 见下方错误码表
- 业务错误由 `ArkException(code, message, httpStatus)` 抛出，统一过滤器转包络；未知异常 → 1000 + HTTP 500（不泄漏堆栈）
- 请求体 JSON 反序列化失败由全局中间件兜底转包络（1400/400）
- 例外：`[SkipEnvelope]` 端点返回原始响应（SSE 流、文件下载）

## 错误码表

| 分段 | 码 | 含义 |
|---|---|---|
| 成功 | 0 | 成功 |
| 1xxx 通用 | 1000 | 未知/内部错误（500） |
| | 1400 | 参数校验失败（400） |
| | 1404 | 资源不存在（404） |
| 2xxx 连接 | 2003 | 连接配置不完整（如 SQLite 缺文件路径） |
| | 2004 | 只读模式拒绝写操作（403） |
| 3xxx 元数据/DDL | 3002 | 表不存在（404） |
| | 3003 | DDL 执行失败 |
| | 3004 | 标识符不合法 |
| 4xxx 数据 | 4002 | 行变更失败（如无主键表更新/删除） |
| 5xxx 同步 | 5002 | 同步执行失败 |
| 6xxx AI | 6001 | AI 未配置（400） |
| | 6002 | AI 接口调用失败（502） |

> 2001/2002/3001/4001/4003/5001/5003 为预留分段，当前未使用；「连接/任务不存在」实际复用 1404。

## 连接管理

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/connections` | 连接列表（`hasPassword` 布尔，永不回显密码） |
| GET | `/api/connections/{id}` | 连接详情 |
| POST | `/api/connections` | 新建。Body：`{name, dialect, host?, port?, username?, password?, database?, filePath?, readOnly, options}`；dialect：`PostgreSQL/MySQL/SQLite` |
| PUT | `/api/connections/{id}` | 更新；`password: null` 表示保留旧密码 |
| DELETE | `/api/connections/{id}` | 删除连接配置（不影响数据库） |
| POST | `/api/connections/{id}/test` | 测试已保存连接 → `{success, serverVersion, elapsedMs, error}` |
| POST | `/api/connections/test` | 测试未保存表单（Body 同新建） |

## 元数据浏览

前缀 `/api/connections/{id}`：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/databases` | 库列表 |
| GET | `/databases/{db}/schemas` | schema 列表（MySQL 返回库名自身，SQLite 固定 `main`） |
| GET | `/databases/{db}/schemas/{schema}/tables` | 表/视图：`{name, kind, estimatedRows}` |
| GET | `/databases/{db}/schemas/{schema}/tables/{table}` | 表详情：Canonical 模型 + 建表语句原文 |
| GET | `/databases/{db}/schemas/{schema}/completion` | SQL 补全元数据（表→列→类型） |

## 表设计器

前缀 `/api/connections/{id}/databases/{db}/schemas/{schema}/tables`：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/preview` | 建表预览。Body：`{table: CanonicalTable, execute}` → `{statements[], warnings[]}` |
| POST | `/` | 建表执行 → `{statements, executed, errors, warnings}` |
| PUT | `/{table}/preview` | 改表 Diff → DDL 预览 |
| PUT | `/{table}` | 改表执行 |
| DELETE | `/{table}` | 删除表 |

## 数据网格

前缀 `…/tables/{table}/rows`：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/query` | 分页查询。Body：`{limit≤10000, offset, filters[{column, op, value}], orderBy}`；op 支持 `= != > < >= <= like not_like is_null is_not_null in not_in` → `{columns, rows, total, primaryKeyColumns}` |
| POST | `/changes` | 变更集提交（单事务）。Body：`{inserts[], updates[], deletes[]}` → `{inserted, updated, deleted}`；无主键表拒绝 update/delete |

## SQL 执行

前缀 `/api/connections/{id}`：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/query` | 多结果集执行。Body：`{database, schema, sql, maxRows≤100000 默认1000, timeoutSeconds≤600 默认30}` → `{resultSets[{columns, rows, affectedRows}], messages, elapsedMs}` |
| POST | `/explain` | 执行计划 → `{planText}`（PG JSON / MySQL TREE / SQLite QUERY PLAN） |

## 同步

前缀 `/api/sync`：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/plan` | 生成计划快照 → `ExecutionPlan`（含每表 `TableExecution`、`DdlAction{sql, destructive, id}`、转换问题、数据计划） |
| POST | `/tasks` | 提交执行。Body：`{plan, skipActionIds[], confirmDestructive}` → 任务 DTO |
| GET | `/tasks?kind&status&profileId&limit≤200&offset` | 任务列表 |
| GET | `/tasks/{id}` | 任务详情（状态/进度/日志/报告/对比结果） |
| POST | `/tasks/{id}/cancel` | 请求取消（块/批边界生效） |
| POST | `/tasks/{id}/resume` | 断点续传 |
| POST | `/tasks/{id}/retry` | 从头重试 |
| GET | `/tasks/{id}/events` | **SSE** 进度流（`text/event-stream`，事件名 `state`） |
| GET | `/tasks/{id}/export-script` | 下载实际执行的 SQL 脚本（`application/sql` 附件） |

前缀 `/api/sync/profiles`：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET/POST | `/` | Profile 列表（含 nextRun）/ 新建（cron 即时校验） |
| PUT/DELETE | `/{profileId}` | 更新 / 删除 |
| PATCH | `/{profileId}/schedule` | 仅改调度 `{cron, scheduleEnabled}` |
| POST | `/{profileId}/run` | 立即运行（重建计划快照并提交；有活动任务时拒绝） |

## 对比（先比后改）

前缀 `/api/compare`：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/runs` | 提交异步对比任务。Body：`{source/targetConnectionId, source/targetDatabase, tables[], chunkRows?, targetSchema?, maxDiffKeys?}` |
| GET | `/runs/{taskId}` | 对比状态（每表 `+插入 ~更新 -删除` 汇总） |
| GET | `/runs/{taskId}/tables/{table}/diffs?changeType=inserted\|updated\|deleted&cursor&limit≤200` | 差异键分页 + 逐键回查源/目标行值 |
| POST | `/runs/{taskId}/to-plan` | 由对比结果生成执行计划（默认 RowDiff + 结构变更，不删多余行） |

## 导入导出

前缀 `…/tables/{table}`：

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/export` | 文件流下载（跳过包络）。Body：`{format: csv\|json\|sql, limit?}` |
| POST | `/import` | multipart 导入。字段：`file`、`format`（缺省取扩展名）、`batchSize`（默认 500）→ `{table, inserted, tableCreated, warnings[]}` |

## AI 与设置

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/settings/ai` | `{enabled, baseUrl, model, hasApiKey}`（Key 不回显） |
| PUT | `/api/settings/ai` | 保存；apiKey：null 保持 / 空串清除 / 其他重新加密 |
| POST | `/api/ai/test` | 连通性测试 |
| POST | `/api/ai/sql` | 自然语言→SQL（自动附带库结构上下文，剥 ```sql 围栏） |
| POST | `/api/ai/explain` | 解释 SQL |
| POST | `/api/ai/fix` | 纠错。Body：`{sql, error}` |

## curl 快速上手

```bash
# 新建 SQLite 连接
curl -X POST http://localhost:5170/api/connections -H 'Content-Type: application/json' -d '{
  "name": "Demo 商城", "dialect": "SQLite",
  "filePath": "/绝对路径/data/demo/shop.db", "readOnly": false
}'

# 查表列表
curl http://localhost:5170/api/connections/{id}/databases/main/schemas/main/tables

# 执行 SQL
curl -X POST http://localhost:5170/api/connections/{id}/query -H 'Content-Type: application/json' -d '{
  "database": "main", "schema": "main", "sql": "SELECT city, COUNT(*) FROM customers GROUP BY city"
}'
```
