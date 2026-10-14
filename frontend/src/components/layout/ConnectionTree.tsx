import { Fragment, useState } from "react"
import type { LucideIcon } from "lucide-react"
import { useIsFetching, useQuery, useQueryClient } from "@tanstack/react-query"
import {
  ChevronDown,
  Copy,
  Database,
  Download,
  Eraser,
  FileText,
  FolderOpen,
  Layers,
  Pencil,
  Play,
  PlugZap,
  Plus,
  RefreshCw,
  Table2,
  TerminalSquare,
  Trash2,
} from "lucide-react"
import { toast } from "sonner"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@/components/ui/context-menu"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Skeleton } from "@/components/ui/skeleton"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { Collapse, Stagger, StaggerItem } from "@/components/ui/motion"
import { api } from "@/lib/api"
import { useWorkspace } from "@/stores/workspace"
import { useQuerySessions } from "@/stores/query-session"
import type { ConnectionDto } from "@/types/api"
import { ConnectionDialog } from "@/features/connections/ConnectionDialog"
import { ImportExportDialog } from "@/features/io/ImportExportDialog"
import { ExportScopeDialog } from "@/features/io/ExportScopeDialog"
import { defaultSchema, firstDatabaseOf, hasSchemas } from "@/features/connections/dialects"
import type { MenuAction, MenuItemSpec, MenuTarget } from "@/features/connections/tree-menus"
import { connectionMenu, databaseMenu, relationMenu, schemaMenu, topNSelect } from "@/features/connections/tree-menus"

type PendingConfirm =
  | { type: "connection"; conn: ConnectionDto }
  | { type: "table"; conn: ConnectionDto; database: string; schema: string; table: string; isView: boolean }
  | { type: "truncate"; conn: ConnectionDto; database: string; schema: string; table: string }

export function ConnectionTree() {
  const queryClient = useQueryClient()
  const { openTab } = useWorkspace()
  const [openConns, setOpenConns] = useState<Set<string>>(new Set())
  const [openDbs, setOpenDbs] = useState<Set<string>>(new Set())
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<ConnectionDto | null>(null)
  const [ioTable, setIoTable] = useState<{ conn: string; db: string; schema: string; table: string } | null>(null)
  const [exportScope, setExportScope] = useState<{
    conn: ConnectionDto
    database: string
    schema: string | null
    scope: "database" | "schema"
  } | null>(null)
  const [pendingConfirm, setPendingConfirm] = useState<PendingConfirm | null>(null)

  const connections = useQuery({ queryKey: ["connections"], queryFn: api.listConnections })
  const connectionsFetching = useIsFetching({ queryKey: ["connections"] }) > 0

  const toggleConn = (id: string) =>
    setOpenConns((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const toggleDb = (key: string) =>
    setOpenDbs((s) => {
      const next = new Set(s)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })

  const openTable = (conn: ConnectionDto, db: string, schema: string, table: string) =>
    openTab({
      kind: "grid",
      title: table,
      connectionId: conn.id,
      connectionName: conn.name,
      database: db,
      schema,
      table,
    })

  const openDesigner = (conn: ConnectionDto, db: string, schema: string, table?: string) =>
    openTab({
      kind: "designer",
      title: table ? `设计 ${table}` : "新建表",
      connectionId: conn.id,
      connectionName: conn.name,
      database: db,
      schema,
      table,
    })

  const openQuery = (conn: ConnectionDto, db: string, schema: string) =>
    openTab({
      kind: "query",
      connectionId: conn.id,
      connectionName: conn.name,
      database: db,
      schema,
    })

  /** 删除连接：弹 AlertDialog 确认 */
  const requestDeleteConnection = (conn: ConnectionDto) =>
    setPendingConfirm({ type: "connection", conn })

  /** 删除表/视图：弹 AlertDialog 确认 */
  const requestDropTable = (conn: ConnectionDto, db: string, schema: string, table: string, isView = false) =>
    setPendingConfirm({ type: "table", conn, database: db, schema, table, isView })

  /** 清空表：弹 AlertDialog 确认 */
  const requestTruncate = (conn: ConnectionDto, db: string, schema: string, table: string) =>
    setPendingConfirm({ type: "truncate", conn, database: db, schema, table })

  /** 打开预填 SQL 的新查询标签：先写入会话（QueryConsole 挂载时以 session.sql 初始化编辑器）再开标签 */
  const openQueryWithSql = (conn: ConnectionDto, db: string, schema: string, sql: string, title: string) => {
    const nums = useWorkspace
      .getState()
      .tabs.filter((t) => t.kind === "query" && t.connectionId === conn.id)
      .map((t) => t.queryNo ?? 0)
    const queryNo = Math.max(0, ...nums) + 1
    const key = `query:${conn.id}:${queryNo}`
    useQuerySessions.getState().save(key, { sql, database: db, schema })
    openTab({
      kind: "query",
      key,
      queryNo,
      title,
      connectionId: conn.id,
      connectionName: conn.name,
      database: db,
      schema,
    })
  }

  /** 查看表/视图定义：视图走视图定义原文，表走元数据的建表语句 */
  async function showRelationDdl(conn: ConnectionDto, db: string, schema: string, table: string, kind: "table" | "view") {
    try {
      let sql: string | null
      if (kind === "view") {
        sql = await api.getViewDefinition({ connectionId: conn.id, database: db, schema, table })
        if (!sql) return toast.error("当前方言不支持读取视图定义")
      } else {
        sql = (await api.getTableDetail({ connectionId: conn.id, database: db, schema, table })).createSql
        if (!sql) return toast.error("未能获取建表语句")
      }
      openQueryWithSql(conn, db, schema, sql.endsWith(";") ? sql : `${sql};`, `${table} DDL`)
    } catch (e) {
      toast.error((e as Error).message)
    }
  }

  async function copyText(text: string, label: string) {
    try {
      await navigator.clipboard.writeText(text)
      toast.success(`已复制${label}`)
    } catch {
      toast.error("复制失败，请手动复制")
    }
  }

  /** 按范围失效资源树缓存（react-query 前缀匹配：tables/连接 → 该库 → 该 schema） */
  function refreshScope(t: MenuTarget, scope: "connection" | "database" | "schema") {
    if (scope === "connection") {
      queryClient.invalidateQueries({ queryKey: ["databases", t.conn.id] })
      queryClient.invalidateQueries({ queryKey: ["tables", t.conn.id] })
    } else if (scope === "database" && t.database) {
      queryClient.invalidateQueries({ queryKey: ["schemas", t.conn.id, t.database] })
      queryClient.invalidateQueries({ queryKey: ["tables", t.conn.id, t.database] })
    } else if (t.database && t.schema) {
      queryClient.invalidateQueries({ queryKey: ["tables", t.conn.id, t.database, t.schema] })
    }
  }

  /** 菜单动作统一分发：目标上下文缺省时回落到连接的默认库/Schema */
  function dispatch(action: MenuAction, t: MenuTarget) {
    const db = t.database ?? firstDbOf(t.conn)
    const sc = t.schema ?? defaultSchema(t.conn.dialect, db)
    switch (action.type) {
      case "editConnection":
        setEditing(t.conn)
        setDialogOpen(true)
        break
      case "deleteConnection":
        requestDeleteConnection(t.conn)
        break
      case "newTable":
        openDesigner(t.conn, db, sc)
        break
      case "newQuery":
        openQuery(t.conn, db, sc)
        break
      case "refresh":
        refreshScope(t, action.scope)
        break
      case "copy":
        void copyText(action.text, action.label)
        break
      case "openTable":
        if (t.table) openTable(t.conn, db, sc, t.table)
        break
      case "designTable":
        if (t.table) openDesigner(t.conn, db, sc, t.table)
        break
      case "showDdl":
        if (t.table) void showRelationDdl(t.conn, db, sc, t.table, action.kind)
        break
      case "quickSelect":
        if (t.table) openQueryWithSql(t.conn, db, sc, topNSelect(t.conn, sc, t.table), `${t.table} · 前 100 行`)
        break
      case "io":
        if (t.table) setIoTable({ conn: t.conn.id, db, schema: sc, table: t.table })
        break
      case "truncate":
        if (t.table) requestTruncate(t.conn, db, sc, t.table)
        break
      case "exportScope":
        setExportScope({
          conn: t.conn,
          database: db,
          schema: action.scope === "schema" ? sc : null,
          scope: action.scope,
        })
        break
      case "drop":
        if (t.table) requestDropTable(t.conn, db, sc, t.table, action.isView)
        break
      default: {
        // 穷尽性保护：MenuAction 新增变体而未分发时，编译期直接报错（防止静默无反应）
        const unreachable: never = action
        void unreachable
      }
    }
  }

  async function runConfirm() {
    if (!pendingConfirm) return
    const c = pendingConfirm
    try {
      if (c.type === "connection") {
        await api.deleteConnection(c.conn.id)
        toast.success("连接已删除")
        await queryClient.invalidateQueries({ queryKey: ["connections"] })
      } else if (c.type === "truncate") {
        const res = await api.truncateTable({ connectionId: c.conn.id, database: c.database, schema: c.schema, table: c.table })
        if (res.errors.length > 0) toast.error(res.errors[0])
        else toast.success(`表 ${c.table} 已清空`)
      } else {
        const res = await api.dropTable(
          { connectionId: c.conn.id, database: c.database, schema: c.schema, table: c.table },
          c.isView,
        )
        if (res.errors.length > 0) toast.error(res.errors[0])
        else toast.success(`${c.isView ? "视图" : "表"} ${c.table} 已删除`)
        // 表已不存在：同步关闭指向它的数据网格/设计器标签，避免后续保存操作失效表
        useWorkspace.getState().closeTableTabs(c.conn.id, c.database, c.schema, c.table)
        await queryClient.invalidateQueries({ queryKey: ["tables", c.conn.id, c.database, c.schema] })
      }
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setPendingConfirm(null)
    }
  }

  return (
    <aside className="flex h-full w-full flex-col overflow-hidden border-r">
      <div className="flex items-center gap-1 border-b px-2 py-1.5">
        <span className="text-xs font-medium text-muted-foreground">连接</span>
        <div className="ml-auto flex items-center gap-0.5">
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                className="size-6"
                onClick={() => queryClient.invalidateQueries({ queryKey: ["connections"] })}
              >
                <RefreshCw className={`size-3.5 ${connectionsFetching ? "animate-spin" : ""}`} />
              </Button>
            </TooltipTrigger>
            <TooltipContent>刷新</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                className="size-6"
                onClick={() => {
                  setEditing(null)
                  setDialogOpen(true)
                }}
              >
                <Plus className="size-4" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>新建连接</TooltipContent>
          </Tooltip>
        </div>
      </div>

      <ScrollArea className="min-h-0 flex-1">
        <div className="p-1 text-sm">
          {connections.isLoading && (
            <div className="space-y-2 p-2">
              {["w-3/4", "w-full", "w-2/3", "w-4/5"].map((w) => (
                <Skeleton key={w} className={`h-5 ${w}`} />
              ))}
            </div>
          )}
          {connections.data && (
            <Stagger className="space-y-0.5">
              {connections.data.map((conn) => (
                <StaggerItem key={conn.id}>
                  <ContextMenu>
                    <ContextMenuTrigger asChild>
                      <button
                        className="flex w-full items-center gap-1 rounded px-2 py-1 text-left transition-colors duration-150 hover:bg-accent/60"
                        onClick={() => toggleConn(conn.id)}
                      >
                        {/* 单 chevron 旋转替代图标切换，展开/收起方向连续 */}
                        <ChevronDown
                          className={`size-3.5 shrink-0 text-muted-foreground transition-transform duration-200 ${
                            openConns.has(conn.id) ? "rotate-180" : "-rotate-90"
                          }`}
                        />
                        <PlugZap className="size-3.5 shrink-0 text-emerald-600 dark:text-emerald-400" />
                        <span className="truncate">{conn.name}</span>
                        <Badge variant="outline" className="ml-auto shrink-0 text-[10px]">
                          {conn.dialect}
                        </Badge>
                        {conn.readOnly && (
                          <Badge variant="secondary" className="shrink-0 text-[10px]">
                            只读
                          </Badge>
                        )}
                      </button>
                    </ContextMenuTrigger>
                    <ContextMenuContent>
                      <TreeMenuContent items={connectionMenu(conn)} onRun={(a) => dispatch(a, { conn })} />
                    </ContextMenuContent>
                  </ContextMenu>

                  <Collapse open={openConns.has(conn.id)}>
                    <ConnDatabases
                      conn={conn}
                      openDbs={openDbs}
                      toggleDb={toggleDb}
                      dispatch={dispatch}
                    />
                  </Collapse>
                </StaggerItem>
              ))}
            </Stagger>
          )}
          {connections.data?.length === 0 && (
            <Empty className="border-none p-4">
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <Database />
                </EmptyMedia>
                <EmptyTitle className="text-xs">暂无连接</EmptyTitle>
                <EmptyDescription className="text-xs">点击右上角 + 新建连接</EmptyDescription>
              </EmptyHeader>
            </Empty>
          )}
        </div>
      </ScrollArea>

      <ConnectionDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        editing={editing}
      />
      {ioTable && (
        <ImportExportDialog
          open
          onOpenChange={(o) => !o && setIoTable(null)}
          connectionId={ioTable.conn}
          database={ioTable.db}
          schema={ioTable.schema}
          table={ioTable.table}
        />
      )}
      {exportScope && (
        <ExportScopeDialog
          open
          onOpenChange={(o) => !o && setExportScope(null)}
          connectionId={exportScope.conn.id}
          database={exportScope.database}
          schema={exportScope.schema}
          scope={exportScope.scope}
        />
      )}

      <AlertDialog open={pendingConfirm !== null} onOpenChange={(o) => !o && setPendingConfirm(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {pendingConfirm?.type === "connection"
                ? `删除连接「${pendingConfirm.conn.name}」？`
                : pendingConfirm?.type === "truncate"
                  ? `清空表「${pendingConfirm.table}」？`
                  : pendingConfirm
                    ? `删除${pendingConfirm.isView ? "视图" : "表"}「${pendingConfirm.table}」？`
                    : ""}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {pendingConfirm?.type === "connection"
                ? "仅删除 Ark 中保存的连接配置，不影响数据库本身。"
                : pendingConfirm?.type === "truncate"
                  ? "删除该表全部数据行，表结构保留，操作不可恢复。"
                  : "该操作不可恢复，表结构及其数据将一并删除。"}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={runConfirm}>
              {pendingConfirm?.type === "truncate" ? "清空" : "删除"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </aside>
  )
}

function firstDbOf(conn: ConnectionDto): string {
  return firstDatabaseOf(conn)
}

/** 动作 → 图标（渲染菜单项用） */
const ACTION_ICONS: Partial<Record<MenuAction["type"], LucideIcon>> = {
  editConnection: Pencil,
  deleteConnection: Trash2,
  newTable: Plus,
  newQuery: TerminalSquare,
  refresh: RefreshCw,
  copy: Copy,
  openTable: Table2,
  designTable: Pencil,
  showDdl: FileText,
  quickSelect: Play,
  io: FolderOpen,
  truncate: Eraser,
  drop: Trash2,
  exportScope: Download,
}

/** 统一渲染 tree-menus 构建的菜单项（分隔线/危险色/图标） */
function TreeMenuContent({ items, onRun }: { items: MenuItemSpec[]; onRun: (action: MenuAction) => void }) {
  return (
    <ContextMenuContent className="max-h-80 overflow-y-auto">
      {items.map((it, i) => {
        const Icon = ACTION_ICONS[it.action.type]
        return (
          <Fragment key={`${it.label}-${i}`}>
            {it.separatorBefore && <ContextMenuSeparator />}
            <ContextMenuItem
              className={it.danger ? "text-destructive" : undefined}
              onClick={() => onRun(it.action)}
            >
              {Icon && <Icon />}
              {it.label}
            </ContextMenuItem>
          </Fragment>
        )
      })}
    </ContextMenuContent>
  )
}

function ConnDatabases({
  conn,
  openDbs,
  toggleDb,
  dispatch,
}: {
  conn: ConnectionDto
  openDbs: Set<string>
  toggleDb: (key: string) => void
  dispatch: (action: MenuAction, target: MenuTarget) => void
}) {
  const dbs = useQuery({
    queryKey: ["databases", conn.id],
    queryFn: () => api.listDatabases(conn.id),
    enabled: true,
  })

  if (dbs.isLoading)
    return (
      <div className="space-y-1.5 py-1 pl-7">
        <Skeleton className="h-4 w-28" />
        <Skeleton className="h-4 w-24" />
      </div>
    )
  if (dbs.isError) return <div className="py-1 pl-7 text-xs text-destructive">加载失败: {dbs.error.message}</div>

  return (
    <div className="ml-[15px] border-l border-border/60 pl-2">
      {dbs.data?.map((db) => {
        const key = `${conn.id}/${db.name}`
        const open = openDbs.has(key)
        return (
          <div key={db.name}>
            <ContextMenu>
              <ContextMenuTrigger asChild>
                <button
                  className="flex w-full items-center gap-1 rounded px-2 py-1 text-left transition-colors duration-150 hover:bg-accent/60"
                  onClick={() => toggleDb(key)}
                >
                  <ChevronDown
                    className={`size-3.5 shrink-0 text-muted-foreground transition-transform duration-200 ${
                      open ? "rotate-180" : "-rotate-90"
                    }`}
                  />
                  <Database className="size-3.5 shrink-0 text-sky-600 dark:text-sky-400" />
                  <span className="truncate">{db.name}</span>
                </button>
              </ContextMenuTrigger>
              <ContextMenuContent>
                <TreeMenuContent
                  items={databaseMenu(conn, db.name)}
                  onRun={(a) => dispatch(a, { conn, database: db.name })}
                />
              </ContextMenuContent>
            </ContextMenu>
            <Collapse open={open}>
              {hasSchemas(conn.dialect) ? (
                <ConnSchemas
                  conn={conn}
                  database={db.name}
                  dispatch={dispatch}
                />
              ) : (
                <ConnTables
                  conn={conn}
                  database={db.name}
                  schema={defaultSchema(conn.dialect, db.name)}
                  dispatch={dispatch}
                />
              )}
            </Collapse>
          </div>
        )
      })}
    </div>
  )
}

/** PostgreSQL 多 schema 层级 */
function ConnSchemas({
  conn,
  database,
  dispatch,
}: {
  conn: ConnectionDto
  database: string
  dispatch: (action: MenuAction, target: MenuTarget) => void
}) {
  const [openSchemas, setOpenSchemas] = useState<Set<string>>(new Set())

  const schemas = useQuery({
    queryKey: ["schemas", conn.id, database],
    queryFn: () => api.listSchemas(conn.id, database),
  })

  const toggleSchema = (key: string) =>
    setOpenSchemas((s) => {
      const next = new Set(s)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })

  if (schemas.isLoading)
    return (
      <div className="space-y-1.5 py-1 pl-7">
        <Skeleton className="h-4 w-28" />
        <Skeleton className="h-4 w-24" />
      </div>
    )
  if (schemas.isError)
    return <div className="py-1 pl-7 text-xs text-destructive">加载失败: {schemas.error.message}</div>

  return (
    <div className="ml-[15px] border-l border-border/60 pl-2">
      {schemas.data?.map((sc) => {
        const key = `${conn.id}/${database}/${sc.name}`
        const open = openSchemas.has(key)
        return (
          <div key={sc.name}>
            <ContextMenu>
              <ContextMenuTrigger asChild>
                <button
                  className="flex w-full items-center gap-1 rounded px-2 py-1 text-left transition-colors duration-150 hover:bg-accent/60"
                  onClick={() => toggleSchema(key)}
                >
                  <ChevronDown
                    className={`size-3.5 shrink-0 text-muted-foreground transition-transform duration-200 ${
                      open ? "rotate-180" : "-rotate-90"
                    }`}
                  />
                  <Layers className="size-3.5 shrink-0 text-violet-500 dark:text-violet-400" />
                  <span className="truncate">{sc.name}</span>
                </button>
              </ContextMenuTrigger>
              <ContextMenuContent>
                <TreeMenuContent
                  items={schemaMenu(conn, database, sc.name)}
                  onRun={(a) => dispatch(a, { conn, database, schema: sc.name })}
                />
              </ContextMenuContent>
            </ContextMenu>
            <Collapse open={open}>
              <ConnTables
                conn={conn}
                database={database}
                schema={sc.name}
                dispatch={dispatch}
              />
            </Collapse>
          </div>
        )
      })}
    </div>
  )
}

function ConnTables({
  conn,
  database,
  schema,
  dispatch,
}: {
  conn: ConnectionDto
  database: string
  schema: string
  dispatch: (action: MenuAction, target: MenuTarget) => void
}) {
  const tables = useQuery({
    queryKey: ["tables", conn.id, database, schema],
    queryFn: () => api.listTables(conn.id, database, schema),
    staleTime: 15_000,
  })

  if (tables.isLoading)
    return (
      <div className="space-y-1.5 py-1 pl-7">
        <Skeleton className="h-4 w-32" />
        <Skeleton className="h-4 w-28" />
        <Skeleton className="h-4 w-24" />
      </div>
    )
  if (tables.isError)
    return <div className="py-1 pl-7 text-xs text-destructive">加载失败: {tables.error.message}</div>

  return (
    <div className="ml-[15px] border-l border-border/60 pl-2">
      {tables.data?.map((t) => (
        <ContextMenu key={t.name}>
          <ContextMenuTrigger asChild>
            <button
              className="flex w-full items-center gap-1 rounded px-2 py-1 text-left transition-colors duration-150 hover:bg-accent/60"
              onClick={() =>
                !t.name.startsWith("view:") &&
                dispatch({ type: "openTable" }, { conn, database, schema, table: t.name, kind: t.kind === "view" ? "view" : "table" })
              }
            >
              <Table2 className={`size-3.5 shrink-0 ${t.kind === "view" ? "text-violet-500 dark:text-violet-400" : "text-amber-600 dark:text-amber-400"}`} />
              <span className="truncate">{t.name}</span>
              {t.kind === "view" && (
                <Badge variant="secondary" className="ml-auto shrink-0 text-[10px]">
                  视图
                </Badge>
              )}
            </button>
          </ContextMenuTrigger>
          <ContextMenuContent>
            <TreeMenuContent
              items={relationMenu(
                conn,
                database,
                schema,
                t.name,
                t.kind === "view" ? "view" : "table",
              )}
              onRun={(a) => dispatch(a, { conn, database, schema, table: t.name, kind: t.kind === "view" ? "view" : "table" })}
            />
          </ContextMenuContent>
        </ContextMenu>
      ))}
      {tables.data?.length === 0 && <div className="py-1 pl-7 text-xs text-muted-foreground">无表</div>}
    </div>
  )
}

