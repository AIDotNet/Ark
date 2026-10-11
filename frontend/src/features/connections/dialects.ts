// 方言注册表：所有按方言分支的 UI 逻辑（连接表单、默认 schema、格式化语言）
// 一律查这张表，禁止再写 per-dialect 条件分支。
// 与后端 Ark.Core/Metadata/DialectDescriptor.cs 的语义保持一致。
import type { SqlLanguage } from "sql-formatter"
import type { ConnectionDto, Dialect } from "@/types/api"

export interface DialectInfo {
  /** 展示名 */
  label: string
  /** 文件型连接（SQLite/DuckDB）：表单显示文件路径，无主机/端口 */
  fileBased: boolean
  /** 建连默认端口（文件型省略） */
  defaultPort?: number
  /**
   * schema 解析模式：
   * - none：无 schema 层，固定 "main"（嵌入式单库）
   * - database：库名即 schema（MySQL 系 / ClickHouse）
   * - named：独立 schema 层（PG 系 / SQL Server / DM8）
   */
  schemaMode: "none" | "database" | "named"
  /** named 模式下的默认 schema */
  defaultSchema: string
  /** 默认库输入框占位符 */
  databasePlaceholder: string
  /** sql-formatter 格式化语言 */
  formatterLanguage: SqlLanguage
}

const DIALECT_LIST_INFO: readonly (DialectInfo & { id: Dialect })[] = [
  {
    id: "PostgreSQL",
    label: "PostgreSQL",
    fileBased: false,
    defaultPort: 5432,
    schemaMode: "named",
    defaultSchema: "public",
    databasePlaceholder: "postgres",
    formatterLanguage: "postgresql",
  },
  {
    id: "MySQL",
    label: "MySQL",
    fileBased: false,
    defaultPort: 3306,
    schemaMode: "database",
    defaultSchema: "",
    databasePlaceholder: "可选（连接后再选库）",
    formatterLanguage: "mysql",
  },
  {
    id: "SQLite",
    label: "SQLite",
    fileBased: true,
    schemaMode: "none",
    defaultSchema: "main",
    databasePlaceholder: "",
    formatterLanguage: "sqlite",
  },
  {
    id: "MariaDB",
    label: "MariaDB",
    fileBased: false,
    defaultPort: 3306,
    schemaMode: "database",
    defaultSchema: "",
    databasePlaceholder: "可选（连接后再选库）",
    formatterLanguage: "mariadb",
  },
  {
    id: "SqlServer",
    label: "SQL Server",
    fileBased: false,
    defaultPort: 1433,
    schemaMode: "named",
    defaultSchema: "dbo",
    databasePlaceholder: "master",
    formatterLanguage: "transactsql",
  },
  {
    id: "ClickHouse",
    label: "ClickHouse",
    fileBased: false,
    defaultPort: 8123,
    schemaMode: "database",
    defaultSchema: "",
    databasePlaceholder: "default",
    formatterLanguage: "sql",
  },
  {
    id: "DuckDB",
    label: "DuckDB",
    fileBased: true,
    schemaMode: "none",
    defaultSchema: "main",
    databasePlaceholder: "",
    formatterLanguage: "postgresql",
  },
  {
    id: "DM8",
    label: "达梦 DM8",
    fileBased: false,
    defaultPort: 5236,
    schemaMode: "named",
    defaultSchema: "",
    databasePlaceholder: "默认（单库多模式）",
    formatterLanguage: "plsql",
  },
  {
    id: "KingbaseES",
    label: "人大金仓",
    fileBased: false,
    defaultPort: 54321,
    schemaMode: "named",
    defaultSchema: "public",
    databasePlaceholder: "test（PG 兼容模式）",
    formatterLanguage: "postgresql",
  },
  {
    id: "OpenGauss",
    label: "openGauss",
    fileBased: false,
    defaultPort: 5432,
    schemaMode: "named",
    defaultSchema: "public",
    databasePlaceholder: "postgres",
    formatterLanguage: "postgresql",
  },
]

export const DIALECT_LIST = DIALECT_LIST_INFO.map((d) => d.id)

const REGISTRY: Record<Dialect, DialectInfo> = Object.fromEntries(
  DIALECT_LIST_INFO.map(({ id, ...info }) => [id, info]),
) as Record<Dialect, DialectInfo>

export function dialectInfo(dialect: Dialect): DialectInfo {
  return REGISTRY[dialect] ?? REGISTRY.PostgreSQL
}

/** 树/网格用的默认 schema：PG 系 → public；MySQL 系/ClickHouse → 库名；嵌入式 → main */
export function defaultSchema(dialect: Dialect, database: string): string {
  const info = dialectInfo(dialect)
  if (info.schemaMode === "named") return info.defaultSchema
  if (info.schemaMode === "database") return database
  return info.defaultSchema
}

/** 连接的第一个"库名"：文件型用 main，其余用连接配置的默认库 */
export function firstDatabaseOf(conn: ConnectionDto): string {
  return dialectInfo(conn.dialect).fileBased ? "main" : (conn.database ?? "")
}

/** 是否存在独立 schema 层（决定树中是否展示 schema 节点） */
export function hasSchemas(dialect: Dialect): boolean {
  return dialectInfo(dialect).schemaMode === "named"
}
