import { useState } from "react"
import { useIsFetching, useQuery, useQueryClient } from "@tanstack/react-query"
import {
  ChevronDown,
  Database,
  FolderOpen,
  Layers,
  Pencil,
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
import type { ConnectionDto, Dialect } from "@/types/api"
import { ConnectionDialog } from "@/features/connections/ConnectionDialog"
import { ImportExportDialog } from "@/features/io/ImportExportDialog"

export function defaultSchema(dialect: Dialect, database: string): string {
  if (dialect === "PostgreSQL") return "public"
  if (dialect === "MySQL") return database
  return "main"
}

type PendingConfirm =
  | { type: "connection"; conn: ConnectionDto }
  | { type: "table"; conn: ConnectionDto; database: string; schema: string; table: string }

export function ConnectionTree() {
  const queryClient = useQueryClient()
  const { openTab } = useWorkspace()
  const [openConns, setOpenConns] = useState<Set<string>>(new Set())
  const [openDbs, setOpenDbs] = useState<Set<string>>(new Set())
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<ConnectionDto | null>(null)
  const [ioTable, setIoTable] = useState<{ conn: string; db: string; schema: string; table: string } | null>(null)
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

  /** 删除表：弹 AlertDialog 确认 */
  const requestDropTable = (conn: ConnectionDto, db: string, schema: string, table: string) =>
    setPendingConfirm({ type: "table", conn, database: db, schema, table })

  async function runConfirm() {
    if (!pendingConfirm) return
    const c = pendingConfirm
    try {
      if (c.type === "connection") {
        await api.deleteConnection(c.conn.id)
        toast.success("连接已删除")
        await queryClient.invalidateQueries({ queryKey: ["connections"] })
      } else {
        const res = await api.dropTable({ connectionId: c.conn.id, database: c.database, schema: c.schema, table: c.table })
        if (res.errors.length > 0) toast.error(res.errors[0])
        else toast.success(`表 ${c.table} 已删除`)
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

      <ScrollArea className="flex-1">
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
                      <ContextMenuItem
                        onClick={() => {
                          setEditing(conn)
                          setDialogOpen(true)
                        }}
                      >
                        <Pencil /> 编辑连接
                      </ContextMenuItem>
                      <ContextMenuItem onClick={() => openDesigner(conn, firstDbOf(conn), defaultSchema(conn.dialect, firstDbOf(conn)))}>
                        <Plus /> 新建表
                      </ContextMenuItem>
                      <ContextMenuItem
                        onClick={() => openQuery(conn, firstDbOf(conn), defaultSchema(conn.dialect, firstDbOf(conn)))}
                      >
                        <TerminalSquare /> 新建查询
                      </ContextMenuItem>
                      <ContextMenuSeparator />
                      <ContextMenuItem className="text-destructive" onClick={() => requestDeleteConnection(conn)}>
                        <Trash2 /> 删除连接
                      </ContextMenuItem>
                    </ContextMenuContent>
                  </ContextMenu>

                  <Collapse open={openConns.has(conn.id)}>
                    <ConnDatabases
                      conn={conn}
                      openDbs={openDbs}
                      toggleDb={toggleDb}
                      openTable={openTable}
                      openDesigner={openDesigner}
                      openQuery={openQuery}
                      setIoTable={setIoTable}
                      dropTable={requestDropTable}
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

      <AlertDialog open={pendingConfirm !== null} onOpenChange={(o) => !o && setPendingConfirm(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {pendingConfirm?.type === "connection"
                ? `删除连接「${pendingConfirm.conn.name}」？`
                : pendingConfirm
                  ? `删除表「${pendingConfirm.table}」？`
                  : ""}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {pendingConfirm?.type === "connection"
                ? "仅删除 Ark 中保存的连接配置，不影响数据库本身。"
                : "该操作不可恢复，表结构及其数据将一并删除。"}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={runConfirm}>
              删除
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </aside>
  )
}

function firstDbOf(conn: ConnectionDto): string {
  return conn.dialect === "SQLite" ? "main" : (conn.database ?? "")
}

function ConnDatabases({
  conn,
  openDbs,
  toggleDb,
  openTable,
  openDesigner,
  openQuery,
  setIoTable,
  dropTable,
}: {
  conn: ConnectionDto
  openDbs: Set<string>
  toggleDb: (key: string) => void
  openTable: (conn: ConnectionDto, db: string, schema: string, table: string) => void
  openDesigner: (conn: ConnectionDto, db: string, schema: string, table?: string) => void
  openQuery: (conn: ConnectionDto, db: string, schema: string) => void
  setIoTable: (v: { conn: string; db: string; schema: string; table: string }) => void
  dropTable: (conn: ConnectionDto, db: string, schema: string, table: string) => void
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
                <ContextMenuItem
                  onClick={() => openQuery(conn, db.name, defaultSchema(conn.dialect, db.name))}
                >
                  <TerminalSquare /> 新建查询
                </ContextMenuItem>
              </ContextMenuContent>
            </ContextMenu>
            <Collapse open={open}>
              {conn.dialect === "PostgreSQL" ? (
                <ConnSchemas
                  conn={conn}
                  database={db.name}
                  openTable={openTable}
                  openDesigner={openDesigner}
                  setIoTable={setIoTable}
                  dropTable={dropTable}
                />
              ) : (
                <ConnTables
                  conn={conn}
                  database={db.name}
                  schema={defaultSchema(conn.dialect, db.name)}
                  openTable={openTable}
                  openDesigner={openDesigner}
                  setIoTable={setIoTable}
                  dropTable={dropTable}
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
  openTable,
  openDesigner,
  setIoTable,
  dropTable,
}: {
  conn: ConnectionDto
  database: string
  openTable: (conn: ConnectionDto, db: string, schema: string, table: string) => void
  openDesigner: (conn: ConnectionDto, db: string, schema: string, table?: string) => void
  setIoTable: (v: { conn: string; db: string; schema: string; table: string }) => void
  dropTable: (conn: ConnectionDto, db: string, schema: string, table: string) => void
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
            <Collapse open={open}>
              <ConnTables
                conn={conn}
                database={database}
                schema={sc.name}
                openTable={openTable}
                openDesigner={openDesigner}
                setIoTable={setIoTable}
                dropTable={dropTable}
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
  openTable,
  openDesigner,
  setIoTable,
  dropTable,
}: {
  conn: ConnectionDto
  database: string
  schema: string
  openTable: (conn: ConnectionDto, db: string, schema: string, table: string) => void
  openDesigner: (conn: ConnectionDto, db: string, schema: string, table?: string) => void
  setIoTable: (v: { conn: string; db: string; schema: string; table: string }) => void
  dropTable: (conn: ConnectionDto, db: string, schema: string, table: string) => void
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
              onClick={() => !t.name.startsWith("view:") && openTable(conn, database, schema, t.name)}
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
            <ContextMenuItem onClick={() => openTable(conn, database, schema, t.name)}>
              <Table2 /> 打开数据
            </ContextMenuItem>
            {t.kind === "table" && (
              <ContextMenuItem onClick={() => openDesigner(conn, database, schema, t.name)}>
                <Pencil /> 设计表
              </ContextMenuItem>
            )}
            <ContextMenuItem
              onClick={() => setIoTable({ conn: conn.id, db: database, schema, table: t.name })}
            >
              <FolderOpen /> 导入 / 导出
            </ContextMenuItem>
            {t.kind === "table" && (
              <>
                <ContextMenuSeparator />
                <ContextMenuItem className="text-destructive" onClick={() => dropTable(conn, database, schema, t.name)}>
                  <Trash2 /> 删除表
                </ContextMenuItem>
              </>
            )}
          </ContextMenuContent>
        </ContextMenu>
      ))}
      {tables.data?.length === 0 && <div className="py-1 pl-7 text-xs text-muted-foreground">无表</div>}
    </div>
  )
}
