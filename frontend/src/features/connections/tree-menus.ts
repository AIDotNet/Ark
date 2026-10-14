// 资源树右键菜单的统一构建：五级节点（连接/数据库/Schema/表/视图）的菜单项都在这里生成，
// 方言差异一律查 dialects 注册表，此处与 ConnectionTree 均不得再写 per-dialect 分支。
// 菜单渲染与动作分发在 ConnectionTree。
import type { ConnectionDto } from "@/types/api"
import { dialectInfo } from "./dialects"

export type MenuAction =
  | { type: "editConnection" }
  | { type: "deleteConnection" }
  | { type: "newTable" }
  | { type: "newQuery" }
  | { type: "refresh"; scope: "connection" | "database" | "schema" }
  | { type: "copy"; text: string; label: string }
  | { type: "openTable" }
  | { type: "designTable" }
  | { type: "showDdl"; kind: "table" | "view" }
  | { type: "quickSelect" }
  | { type: "io" }
  | { type: "truncate" }
  | { type: "drop"; isView: boolean }
  | { type: "exportScope"; scope: "database" | "schema" }

export interface MenuItemSpec {
  label: string
  action: MenuAction
  danger?: boolean
  separatorBefore?: boolean
}

/** 动作分发的目标上下文：缺省 database/schema 时回落到连接的默认库/Schema */
export interface MenuTarget {
  conn: ConnectionDto
  database?: string
  schema?: string
  table?: string
  kind?: "table" | "view"
}

export function connectionMenu(conn: ConnectionDto): MenuItemSpec[] {
  return [
    { label: "编辑连接", action: { type: "editConnection" } },
    { label: "新建表", action: { type: "newTable" } },
    { label: "新建查询", action: { type: "newQuery" } },
    { label: "复制连接名", action: { type: "copy", text: conn.name, label: "连接名" }, separatorBefore: true },
    { label: "刷新", action: { type: "refresh", scope: "connection" } },
    { label: "删除连接", action: { type: "deleteConnection" }, danger: true, separatorBefore: true },
  ]
}

export function databaseMenu(conn: ConnectionDto, database: string): MenuItemSpec[] {
  void conn
  return [
    { label: "新建表", action: { type: "newTable" } },
    { label: "新建查询", action: { type: "newQuery" } },
    { label: "导出数据库…", action: { type: "exportScope", scope: "database" } },
    { label: "刷新", action: { type: "refresh", scope: "database" } },
    { label: "复制数据库名", action: { type: "copy", text: database, label: "数据库名" }, separatorBefore: true },
  ]
}

export function schemaMenu(conn: ConnectionDto, database: string, schema: string): MenuItemSpec[] {
  void conn
  void database
  return [
    { label: "新建表", action: { type: "newTable" } },
    { label: "新建查询", action: { type: "newQuery" } },
    { label: "导出 Schema…", action: { type: "exportScope", scope: "schema" } },
    { label: "刷新", action: { type: "refresh", scope: "schema" } },
    { label: "复制 Schema 名", action: { type: "copy", text: schema, label: "Schema 名" }, separatorBefore: true },
  ]
}

export function relationMenu(
  conn: ConnectionDto,
  _database: string,
  _schema: string,
  table: string,
  kind: "table" | "view",
): MenuItemSpec[] {
  const isView = kind === "view"
  const items: MenuItemSpec[] = [
    { label: isView ? "打开视图" : "打开数据", action: { type: "openTable" } },
  ]
  if (!isView) items.push({ label: "设计表", action: { type: "designTable" } })
  items.push({ label: "查询前 100 行", action: { type: "quickSelect" } })
  items.push({ label: isView ? "查看定义" : "查看建表语句", action: { type: "showDdl", kind } })
  items.push({ label: "导入 / 导出", action: { type: "io" }, separatorBefore: true })
  items.push({ label: isView ? "复制视图名" : "复制表名", action: { type: "copy", text: table, label: "名称" } })
  if (!conn.readOnly) {
    if (!isView) {
      items.push({
        // SQLite 无 TRUNCATE，后端生成器自动降级 DELETE FROM，菜单文案如实告知
        label: dialectInfo(conn.dialect).truncateKind === "delete" ? "清空表（DELETE）" : "清空表",
        action: { type: "truncate" },
        danger: true,
        separatorBefore: true,
      })
    }
    items.push({ label: isView ? "删除视图" : "删除表", action: { type: "drop", isView }, danger: true })
  }
  return items
}

/** 表/视图在 SQL 中的限定名：named schema 方言带 schema 前缀（树内标识符均为合法安全名） */
export function qualifiedName(conn: ConnectionDto, schema: string, table: string): string {
  return dialectInfo(conn.dialect).schemaMode === "named" ? `${schema}.${table}` : table
}

/** 前 N 行查询：方言差异（SQL Server 的 TOP）由注册表决定 */
export function topNSelect(conn: ConnectionDto, schema: string, table: string, limit = 100): string {
  const ref = qualifiedName(conn, schema, table)
  return dialectInfo(conn.dialect).topSyntax === "top"
    ? `SELECT TOP ${limit} * FROM ${ref};`
    : `SELECT * FROM ${ref} LIMIT ${limit};`
}
