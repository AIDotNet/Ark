import { useEffect, useMemo, useRef, useState } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { flexRender, getCoreRowModel, useReactTable, type ColumnDef } from "@tanstack/react-table"
import { useVirtualizer } from "@tanstack/react-virtual"
import { ArrowDownAZ, ArrowUpAZ, Copy, FileJson, Loader2, Plus, RefreshCw, Save, Trash2, Undo2 } from "lucide-react"
import { toast } from "sonner"
import { cn } from "cn"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuTrigger,
} from "@/components/ui/context-menu"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { api, type TableRefArgs } from "@/lib/api"
import type { FilterClause, RowPage } from "@/types/api"

const OPS = [
  { v: "=", label: "=" },
  { v: "!=", label: "≠" },
  { v: ">", label: ">" },
  { v: "<", label: "<" },
  { v: ">=", label: "≥" },
  { v: "<=", label: "≤" },
  { v: "like", label: "包含" },
  { v: "is_null", label: "为空" },
  { v: "is_not_null", label: "非空" },
]

const ROW_HEIGHT = 33
const DEFAULT_COL_WIDTH = 180
const MIN_COL_WIDTH = 60

function isNumericType(dataType: string) {
  const d = dataType.toUpperCase()
  return /INT|DEC|NUM|REAL|DOUB|FLOA|SERIAL/.test(d)
}
function isBoolType(dataType: string) {
  return /BOOL|BIT\(|BIT$/.test(dataType.toUpperCase())
}

function pkKeyOf(row: (unknown | null)[], cols: RowPage["columns"], pkCols: string[]): string {
  const idx = new Map(cols.map((c, i) => [c.name, i]))
  return pkCols.map((pk) => JSON.stringify(row[idx.get(pk)!])).join("\x1f")
}

function pkObjectOf(row: (unknown | null)[], cols: RowPage["columns"], pkCols: string[]) {
  const idx = new Map(cols.map((c, i) => [c.name, i]))
  return Object.fromEntries(pkCols.map((pk) => [pk, row[idx.get(pk)!]]))
}

function cellText(v: unknown | null): string {
  if (v === null || v === undefined) return "NULL"
  if (typeof v === "object") return JSON.stringify(v)
  return String(v)
}

function copyText(text: string, label: string) {
  void navigator.clipboard.writeText(text)
  toast.success(`已复制${label}`)
}

export function DataGrid({ connectionId, database, schema, table: tableName }: Omit<TableRefArgs, "connectionId"> & { connectionId: string }) {
  const queryClient = useQueryClient()
  const args: TableRefArgs = { connectionId, database, schema, table: tableName }

  const [pageSize, setPageSize] = useState(50)
  const [page, setPage] = useState(0)
  const [filters, setFilters] = useState<FilterClause[]>([])
  const [draft, setDraft] = useState({ column: "", op: "=", value: "" })
  const [sort, setSort] = useState<{ column: string; descending: boolean } | null>(null)
  const [pageInput, setPageInput] = useState("1")

  // 编辑状态
  const [edits, setEdits] = useState<Record<string, Record<string, unknown>>>({})
  const [newRows, setNewRows] = useState<Record<string, unknown>[]>([])
  const [deletedKeys, setDeletedKeys] = useState<Set<string>>(new Set())
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const [editing, setEditing] = useState<{ key: string; col: string } | null>(null)
  const [saving, setSaving] = useState(false)

  const rows = useQuery({
    queryKey: ["rows", connectionId, database, schema, tableName, page, pageSize, filters, sort],
    queryFn: () =>
      api.queryRows(args, {
        limit: pageSize,
        offset: page * pageSize,
        filters,
        orderBy: sort ? [sort] : [],
      }),
  })

  const cols = rows.data?.columns ?? []
  const pkCols = rows.data?.primaryKeyColumns ?? []
  const readOnly = pkCols.length === 0
  const total = rows.data?.total ?? 0
  const pageCount = Math.max(1, Math.ceil(total / pageSize))

  const dirtyCount = Object.keys(edits).length + newRows.length + deletedKeys.size

  useEffect(() => setPageInput(String(page + 1)), [page])

  const visibleRows = useMemo(
    () =>
      (rows.data?.rows ?? []).map((row, index) => ({
        kind: "db" as const,
        key: pkKeyOf(row, cols, pkCols),
        row,
        index,
      })),
    [rows.data, cols, pkCols]
  )

  function coerce(col: string, raw: string): unknown {
    if (raw === "") return null
    const dataType = cols.find((c) => c.name === col)?.dataType ?? ""
    if (isBoolType(dataType)) {
      if (/^(true|1)$/i.test(raw)) return true
      if (/^(false|0)$/i.test(raw)) return false
      return raw
    }
    if (isNumericType(dataType)) {
      const n = Number(raw)
      if (!Number.isNaN(n)) return n
    }
    return raw
  }

  function startEdit(key: string, col: string) {
    if (readOnly || deletedKeys.has(key)) return
    setEditing({ key, col })
  }

  function commitEdit(key: string, col: string, raw: string) {
    setEditing(null)
    const colDef = cols.find((c) => c.name === col)
    if (!colDef) return
    const current = visibleRows.find((r) => r.key === key)
    const original = current ? current.row[cols.findIndex((c) => c.name === col)] : null
    const value = coerce(col, raw)
    if (JSON.stringify(value) === JSON.stringify(original ?? null)) return
    setEdits((e) => ({ ...e, [key]: { ...e[key], [col]: value } }))
  }

  function editValueOf(key: string, col: string): string {
    if (edits[key]?.[col] !== undefined) return cellText(edits[key][col])
    const r = visibleRows.find((x) => x.key === key)
    if (!r) return ""
    return cellText(r.row[cols.findIndex((c) => c.name === col)])
  }

  async function saveChanges() {
    setSaving(true)
    try {
      const keyMap = new Map(visibleRows.map((r) => [r.key, r.row]))
      const updates = Object.entries(edits)
        .filter(([key]) => !newRows.length || keyMap.has(key))
        .map(([key, values]) => ({ key: pkObjectOf(keyMap.get(key)!, cols, pkCols), values }))
      const inserts = newRows
        .filter((r) => Object.values(r).some((v) => v !== null && v !== ""))
        .map((r) => ({ values: r }))
      const deletes = [...deletedKeys]
        .filter((key) => keyMap.has(key))
        .map((key) => ({ key: pkObjectOf(keyMap.get(key)!, cols, pkCols) }))

      if (updates.length + inserts.length + deletes.length === 0) {
        toast.info("没有需要保存的变更")
        return
      }
      const res = await api.applyChanges(args, { inserts, updates, deletes })
      toast.success(`已保存：插入 ${res.inserted}，更新 ${res.updated}，删除 ${res.deleted}`)
      setEdits({})
      setNewRows([])
      setDeletedKeys(new Set())
      setChecked(new Set())
      await queryClient.invalidateQueries({ queryKey: ["rows"] })
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  function addFilter() {
    if (!draft.column) return
    if (draft.op === "is_null" || draft.op === "is_not_null") {
      setFilters((f) => [...f, { column: draft.column, op: draft.op }])
    } else {
      if (draft.value === "") {
        toast.error("请输入筛选值")
        return
      }
      const value = coerce(draft.column, draft.value)
      setFilters((f) => [...f, { column: draft.column, op: draft.op, value }])
    }
    setDraft({ column: "", op: "=", value: "" })
  }

  function toggleSort(col: string) {
    setSort((s) => {
      if (!s || s.column !== col) return { column: col, descending: false }
      if (!s.descending) return { column: col, descending: true }
      return null
    })
  }

  // ---------- TanStack Table（列宽 / 表头模型内核） ----------
  const data = useMemo(() => visibleRows.map((r) => r.row), [visibleRows])
  const columns = useMemo<ColumnDef<(unknown | null)[]>[]>(
    () => [
      {
        id: "__check",
        size: 44,
        enableResizing: false,
        header: () => {
          const selectable = visibleRows.filter((r) => !deletedKeys.has(r.key))
          const allChecked = selectable.length > 0 && selectable.every((r) => checked.has(r.key))
          return (
            <Checkbox
              aria-label="全选本页"
              checked={selectable.length > 0 && allChecked}
              onCheckedChange={(v) =>
                setChecked((s) => {
                  const n = new Set(s)
                  for (const r of selectable) {
                    if (v) n.add(r.key)
                    else n.delete(r.key)
                  }
                  return n
                })
              }
            />
          )
        },
      },
      {
        id: "__row",
        size: 56,
        enableResizing: false,
        header: () => "#",
      },
      ...cols.map<ColumnDef<(unknown | null)[]>>((c) => ({
        id: c.name,
        size: DEFAULT_COL_WIDTH,
        minSize: MIN_COL_WIDTH,
        header: () => (
          <span
            className="inline-flex cursor-pointer items-center gap-1 select-none hover:text-foreground"
            onClick={(e) => {
              e.stopPropagation()
              toggleSort(c.name)
            }}
          >
            {c.name}
            <span className="text-[10px] font-normal text-muted-foreground">{c.dataType}</span>
            {sort?.column === c.name &&
              (sort.descending ? <ArrowDownAZ className="size-3" /> : <ArrowUpAZ className="size-3" />)}
          </span>
        ),
      })),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [cols, visibleRows, checked, deletedKeys, sort]
  )

  const table = useReactTable({
    data,
    columns,
    getCoreRowModel: getCoreRowModel(),
    columnResizeMode: "onChange",
  })

  const colWidth = (id: string) => table.getColumn(id)?.getSize() ?? DEFAULT_COL_WIDTH
  const totalWidth = table.getTotalSize()

  // ---------- 虚拟滚动 ----------
  const scrollRef = useRef<HTMLDivElement>(null)
  const rowVirtualizer = useVirtualizer({
    count: visibleRows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
  })

  const isLoading = rows.isLoading
  const nonDeleted = visibleRows.filter((r) => !deletedKeys.has(r.key))

  function renderDbCell(r: (typeof visibleRows)[number], c: RowPage["columns"][number], ci: number) {
    const isEditing = editing?.key === r.key && editing.col === c.name
    const isEdited = edits[r.key]?.[c.name] !== undefined
    const value = r.row[ci]
    return (
      <ContextMenu key={c.name}>
        <ContextMenuTrigger asChild>
          <div
            className={cn(
              "flex shrink-0 items-center overflow-hidden border-r px-0 text-xs",
              isEdited && "animate-flash bg-amber-100/70 dark:bg-amber-900/30"
            )}
            style={{ width: colWidth(c.name), height: ROW_HEIGHT }}
            onDoubleClick={() => startEdit(r.key, c.name)}
          >
            {isEditing ? (
              <input
                autoFocus
                defaultValue={editValueOf(r.key, c.name) === "NULL" ? "" : editValueOf(r.key, c.name)}
                className="h-full w-full bg-background px-2 outline-2 outline-primary"
                onBlur={(e) => commitEdit(r.key, c.name, e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") commitEdit(r.key, c.name, (e.target as HTMLInputElement).value)
                  if (e.key === "Escape") setEditing(null)
                }}
              />
            ) : (
              <span
                className={cn(
                  "block w-full truncate px-2 py-1.5",
                  value === null && "text-muted-foreground/50 italic"
                )}
              >
                {cellText(value)}
              </span>
            )}
          </div>
        </ContextMenuTrigger>
        <ContextMenuContent>
          <ContextMenuItem onClick={() => copyText(value === null ? "NULL" : cellText(value), "单元格")}>
            <Copy /> 复制单元格
          </ContextMenuItem>
          <ContextMenuItem
            onClick={() =>
              copyText(JSON.stringify(Object.fromEntries(cols.map((c2, i) => [c2.name, r.row[i]])), null, 2), "整行")
            }
          >
            <FileJson /> 复制整行（JSON）
          </ContextMenuItem>
        </ContextMenuContent>
      </ContextMenu>
    )
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* 工具栏 */}
      <div className="flex flex-wrap items-center gap-2 border-b px-3 py-2">
        <Select value={draft.column || undefined} onValueChange={(v) => setDraft((d) => ({ ...d, column: v }))}>
          <SelectTrigger className="w-36" size="sm">
            <SelectValue placeholder="筛选列" />
          </SelectTrigger>
          <SelectContent>
            {cols.map((c) => (
              <SelectItem key={c.name} value={c.name}>
                {c.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={draft.op} onValueChange={(v) => setDraft((d) => ({ ...d, op: v }))}>
          <SelectTrigger className="w-20" size="sm">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {OPS.map((o) => (
              <SelectItem key={o.v} value={o.v}>
                {o.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Input
          className="h-8 w-40"
          placeholder="值"
          value={draft.value}
          onChange={(e) => setDraft((d) => ({ ...d, value: e.target.value }))}
          onKeyDown={(e) => e.key === "Enter" && addFilter()}
        />
        <Button variant="outline" size="sm" onClick={addFilter}>
          添加筛选
        </Button>
        {filters.map((f, i) => (
          <Badge key={i} variant="secondary" className="animate-pop-in gap-1">
            {f.column} {OPS.find((o) => o.v === f.op)?.label}
            {f.op !== "is_null" && f.op !== "is_not_null" ? ` ${String(f.value)}` : ""}
            <button
              className="ml-0.5 opacity-60 hover:opacity-100"
              onClick={() => setFilters((arr) => arr.filter((_, j) => j !== i))}
            >
              ×
            </button>
          </Badge>
        ))}

        <div className="ml-auto flex items-center gap-2">
          {readOnly && (
            <Badge variant="secondary" title="该表没有主键，无法定位行进行编辑">
              无主键 · 只读
            </Badge>
          )}
          {dirtyCount > 0 && (
            <>
              <Badge className="animate-pop-in">{dirtyCount} 处变更</Badge>
              <Button size="sm" variant="outline" onClick={() => { setEdits({}); setNewRows([]); setDeletedKeys(new Set()) }}>
                <Undo2 /> 放弃
              </Button>
              <Button size="sm" onClick={saveChanges} disabled={saving}>
                {saving ? <Loader2 className="animate-spin" /> : <Save />} 保存
              </Button>
            </>
          )}
          <Button
            variant="ghost"
            size="sm"
            disabled={readOnly}
            onClick={() => setNewRows((n) => [...n, Object.fromEntries(cols.map((c) => [c.name, null]))])}
          >
            <Plus /> 新增行
          </Button>
          <Button
            variant="ghost"
            size="sm"
            disabled={checked.size === 0 || readOnly}
            onClick={() => setDeletedKeys((s) => new Set([...s, ...checked]))}
          >
            <Trash2 /> 删除选中
          </Button>
          <Button
            variant="ghost"
            size="icon"
            className="size-7"
            onClick={() => queryClient.invalidateQueries({ queryKey: ["rows"] })}
          >
            <RefreshCw className="size-3.5" />
          </Button>
        </div>
      </div>

      {/* 数据区：虚拟滚动 + 列宽拖拽 */}
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto">
        {isLoading ? (
          <div className="space-y-2 p-4">
            {Array.from({ length: 10 }).map((_, i) => (
              <Skeleton key={i} className={cn("h-7", i % 3 === 2 ? "w-4/5" : "w-full")} />
            ))}
          </div>
        ) : rows.isError ? (
          <div className="p-4 text-sm text-destructive">加载失败：{rows.error.message}</div>
        ) : (
          <div style={{ width: totalWidth }}>
            {/* 表头 */}
            <div className="sticky top-0 z-10 flex items-stretch border-b bg-background text-xs font-medium">
              {table.getHeaderGroups().map((hg) => (
                <div key={hg.id} className="flex items-stretch">
                  {hg.headers.map((header) => (
                    <div
                      key={header.id}
                      className={cn(
                        "relative flex shrink-0 items-center border-r px-2",
                        header.column.id !== "__check" && header.column.id !== "__row" && "cursor-default"
                      )}
                      style={{ width: header.getSize(), height: ROW_HEIGHT }}
                    >
                      {header.column.id === "__check" || header.column.id === "__row" ? (
                        <span className="truncate">{flexRender(header.column.columnDef.header, header.getContext())}</span>
                      ) : (
                        flexRender(header.column.columnDef.header, header.getContext())
                      )}
                      {header.column.getCanResize() && (
                        <div
                          aria-hidden
                          onMouseDown={header.getResizeHandler()}
                          onTouchStart={header.getResizeHandler()}
                          onDoubleClick={() => header.column.resetSize()}
                          className={cn(
                            "absolute right-0 top-0 h-full w-1 cursor-col-resize touch-none select-none hover:bg-border",
                            header.column.getIsResizing() && "bg-primary"
                          )}
                        />
                      )}
                    </div>
                  ))}
                </div>
              ))}
            </div>

            {/* 新增行（少量，不参与虚拟化） */}
            {newRows.map((r, i) => (
              <div
                key={`new-${i}`}
                className="flex animate-in items-stretch border-b border-solid bg-amber-50/60 slide-in-from-top-1 fade-in duration-200 dark:bg-amber-950/20"
                style={{ height: ROW_HEIGHT }}
              >
                <div className="flex w-11 shrink-0 items-center justify-center border-r">
                  <Button
                    variant="ghost"
                    size="icon"
                    className="size-5"
                    onClick={() => setNewRows((n) => n.filter((_, j) => j !== i))}
                  >
                    ×
                  </Button>
                </div>
                <div className="flex w-14 shrink-0 items-center justify-center border-r text-[10px] text-muted-foreground">
                  新{i + 1}
                </div>
                {cols.map((c) => (
                  <div key={c.name} className="flex shrink-0 items-center border-r" style={{ width: colWidth(c.name) }}>
                    <input
                      className="h-full w-full bg-transparent px-2 text-xs outline-none"
                      value={cellText(r[c.name]) === "NULL" ? "" : cellText(r[c.name])}
                      placeholder={c.name}
                      onChange={(e) => {
                        const value = coerce(c.name, e.target.value)
                        setNewRows((n) => n.map((row, j) => (j === i ? { ...row, [c.name]: value } : row)))
                      }}
                    />
                  </div>
                ))}
              </div>
            ))}

            {/* 数据行：绝对定位虚拟列表 */}
            <div className="relative" style={{ height: rowVirtualizer.getTotalSize() }}>
              {rowVirtualizer.getVirtualItems().map((vi) => {
                const r = visibleRows[vi.index]
                const deleted = deletedKeys.has(r.key)
                return (
                  <div
                    key={r.key}
                    data-deleted={deleted || undefined}
                    className={cn(
                      "absolute left-0 top-0 flex items-stretch border-b text-xs transition-[background-color,opacity] duration-150 hover:bg-accent/25",
                      deleted && "line-through opacity-40 hover:bg-transparent",
                      checked.has(r.key) && !deleted && "bg-accent/40"
                    )}
                    style={{ height: vi.size, transform: `translateY(${vi.start}px)`, width: totalWidth }}
                  >
                    <div className="flex w-11 shrink-0 items-center justify-center border-r">
                      {deleted ? (
                        <Button
                          variant="ghost"
                          size="icon"
                          className="size-5"
                          onClick={() =>
                            setDeletedKeys((s) => {
                              const n = new Set(s)
                              n.delete(r.key)
                              return n
                            })
                          }
                        >
                          ×
                        </Button>
                      ) : (
                        <Checkbox
                          checked={checked.has(r.key)}
                          onCheckedChange={(v) =>
                            setChecked((s) => {
                              const n = new Set(s)
                              if (v) n.add(r.key)
                              else n.delete(r.key)
                              return n
                            })
                          }
                        />
                      )}
                    </div>
                    <div className="flex w-14 shrink-0 items-center justify-center border-r text-[10px] text-muted-foreground">
                      {page * pageSize + vi.index + 1}
                    </div>
                    {cols.map((c, ci) => renderDbCell(r, c, ci))}
                  </div>
                )
              })}
            </div>

            {visibleRows.length === 0 && newRows.length === 0 && (
              <div className="flex h-32 animate-in items-center justify-center text-sm text-muted-foreground fade-in duration-300">无数据</div>
            )}
          </div>
        )}
      </div>

      {/* 分页栏 */}
      <div className="flex h-9 shrink-0 items-center gap-3 border-t px-3 text-xs text-muted-foreground">
        <span>共 {total} 行</span>
        <span className="flex items-center gap-1">
          每页
          <Select value={String(pageSize)} onValueChange={(v) => { setPageSize(Number(v)); setPage(0) }}>
            <SelectTrigger className="h-6 w-16" size="sm">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {[20, 50, 100, 200, 500].map((n) => (
                <SelectItem key={n} value={String(n)}>{n}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </span>
        <span className="flex items-center gap-1">
          <Button variant="outline" size="sm" className="h-6" disabled={page === 0} onClick={() => setPage(0)}>首页</Button>
          <Button variant="outline" size="sm" className="h-6" disabled={page === 0} onClick={() => setPage((p) => p - 1)}>上一页</Button>
          <span>
            第
            <Input
              className="mx-1 h-6 w-12 px-1 text-center"
              value={pageInput}
              onChange={(e) => setPageInput(e.target.value.replace(/\D/g, ""))}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  const p = Math.min(Math.max((Number(pageInput) || 1) - 1, 0), pageCount - 1)
                  setPage(p)
                  ;(e.target as HTMLInputElement).blur()
                }
              }}
            />
            / {pageCount} 页
          </span>
          <Button variant="outline" size="sm" className="h-6" disabled={page + 1 >= pageCount} onClick={() => setPage((p) => p + 1)}>下一页</Button>
          <Button variant="outline" size="sm" className="h-6" disabled={page + 1 >= pageCount} onClick={() => setPage(pageCount - 1)}>末页</Button>
        </span>
        {rows.data && <span className="ml-auto">{rows.data.columns.length} 列 · 主键: {pkCols.join(", ") || "无"}{nonDeleted.length > 0 && ` · 已选 ${nonDeleted.filter((r) => checked.has(r.key)).length} 行`}</span>}
      </div>
    </div>
  )
}
