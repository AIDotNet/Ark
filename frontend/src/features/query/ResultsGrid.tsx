import { useMemo, useRef, useState } from "react"
import { useVirtualizer } from "@tanstack/react-virtual"
import { ArrowDown, ArrowUp, ArrowUpDown, Braces, Copy, FileJson, Table2 } from "lucide-react"
import { toast } from "sonner"
import { cn } from "cn"
import { Button } from "@/components/ui/button"
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@/components/ui/context-menu"
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import type { QueryResultSet } from "@/types/api"

const ROW_HEIGHT = 28
const ROW_NO_WIDTH = 52
const DEFAULT_COL_WIDTH = 160
const MIN_COL_WIDTH = 56
const LONG_TEXT = 80

function cellText(v: unknown): string {
  if (v === null || v === undefined) return ""
  if (typeof v === "object") return JSON.stringify(v)
  return String(v)
}

function isNumericType(dataType: string): boolean {
  return /INT|DEC|NUM|REAL|DOUB|FLOA|SERIAL|YEAR/i.test(dataType)
}

function isJsonish(v: unknown): boolean {
  if (typeof v === "string") {
    const s = v.trim()
    return (s.startsWith("{") || s.startsWith("[")) && s.length > 1
  }
  return typeof v === "object" && v !== null
}

function copyClipboard(text: string, label: string) {
  void navigator.clipboard.writeText(text)
  toast.success(`已复制${label}`)
}

/** 行值 → INSERT VALUES 字面量（文本加引号转义，NULL/数字/布尔裸值） */
function sqlLiteral(v: unknown): string {
  if (v === null || v === undefined) return "NULL"
  if (typeof v === "number") return Number.isFinite(v) ? String(v) : "NULL"
  if (typeof v === "boolean") return v ? "TRUE" : "FALSE"
  if (typeof v === "bigint") return String(v)
  const s = typeof v === "object" ? JSON.stringify(v) : String(v)
  return `'${s.replace(/'/g, "''")}'`
}

function toInsert(columns: string[], row: unknown[], tableName = "table"): string {
  const cols = columns.map((c) => `"${c}"`).join(", ")
  const vals = row.map(sqlLiteral).join(", ")
  return `INSERT INTO ${tableName} (${cols}) VALUES (${vals});`
}

type SortState = { col: number; desc: boolean } | null

export function ResultsGrid({ rs, tableName }: { rs: QueryResultSet; tableName?: string }) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const [widths, setWidths] = useState<Record<number, number>>({})
  const [sort, setSort] = useState<SortState>(null)
  const [filter, setFilter] = useState("")
  const [selected, setSelected] = useState<{ r: number; c: number } | null>(null)
  const [viewer, setViewer] = useState<{ r: number; c: number } | null>(null)

  const colWidth = (i: number) => widths[i] ?? DEFAULT_COL_WIDTH
  const totalWidth = ROW_NO_WIDTH + rs.columns.reduce((s, _, i) => s + colWidth(i), 0)

  // 客户端过滤（全列 includes 匹配）
  const filteredRows = useMemo(() => {
    const kw = filter.trim().toLowerCase()
    if (!kw) return rs.rows
    return rs.rows.filter((row) => row.some((v) => cellText(v).toLowerCase().includes(kw)))
  }, [rs.rows, filter])

  // 客户端排序（数值感知）
  const sortedRows = useMemo(() => {
    if (!sort) return filteredRows
    const { col, desc } = sort
    const numeric = isNumericType(rs.columns[col]?.dataType ?? "")
    const arr = [...filteredRows]
    arr.sort((a, b) => {
      const va = a[col]
      const vb = b[col]
      if (va === null || va === undefined) return vb === null || vb === undefined ? 0 : 1
      if (vb === null || vb === undefined) return -1
      let cmp: number
      if (numeric) cmp = Number(va) - Number(vb)
      else cmp = cellText(va).localeCompare(cellText(vb), undefined, { numeric: true })
      return desc ? -cmp : cmp
    })
    return arr
  }, [filteredRows, sort, rs.columns])

  const virtualizer = useVirtualizer({
    count: sortedRows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 20,
  })

  // 无列的结果集（UPDATE/DDL 等语句）：展示受影响行数
  if (rs.columns.length === 0) {
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center p-6 text-sm text-muted-foreground">
        执行成功{rs.affectedRows > 0 ? ` · 受影响 ${rs.affectedRows} 行` : ""}
      </div>
    )
  }

  const viewRow = viewer ? sortedRows[viewer.r] : null
  const viewText = viewRow && viewer ? viewRow[viewer.c] : null
  const viewIsJson = viewText !== null && isJsonish(viewText)

  const cycleSort = (i: number) =>
    setSort((s) => (s?.col !== i ? { col: i, desc: false } : s.desc ? null : { col: i, desc: true }))

  const startResize = (i: number, e: React.MouseEvent) => {
    e.preventDefault()
    e.stopPropagation()
    const startX = e.clientX
    const startW = colWidth(i)
    const onMove = (ev: MouseEvent) =>
      setWidths((w) => ({ ...w, [i]: Math.max(MIN_COL_WIDTH, startW + ev.clientX - startX) }))
    const onUp = () => {
      window.removeEventListener("mousemove", onMove)
      window.removeEventListener("mouseup", onUp)
    }
    window.addEventListener("mousemove", onMove)
    window.addEventListener("mouseup", onUp)
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "c" && selected) {
      const row = sortedRows[selected.r]
      if (row) copyClipboard(cellText(row[selected.c]), "单元格")
    }
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col" tabIndex={0} onKeyDown={onKeyDown}>
      {/* 过滤栏 */}
      <div className="flex h-9 shrink-0 items-center gap-2 border-b px-2">
        <Input
          className="h-7 w-56 text-xs"
          placeholder="在结果中过滤…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        {filter && (
          <span className="text-xs text-muted-foreground">
            命中 {sortedRows.length}/{rs.rows.length} 行
          </span>
        )}
        {sort && (
          <button
            className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
            onClick={() => setSort(null)}
          >
            {rs.columns[sort.col]?.name} {sort.desc ? "降序" : "升序"} · 取消
          </button>
        )}
      </div>

      {/* 数据区：虚拟滚动 + 行号 + 列宽拖拽 */}
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto">
        <div style={{ width: totalWidth }}>
          {/* 表头 */}
          <div className="sticky top-0 z-10 flex items-stretch border-b bg-background text-xs font-medium">
            <div
              className="flex shrink-0 items-center justify-center border-r text-[10px] text-muted-foreground"
              style={{ width: ROW_NO_WIDTH }}
            >
              #
            </div>
            {rs.columns.map((c, i) => (
              <div
                key={`${c.name}-${i}`}
                className="relative flex shrink-0 cursor-default select-none items-center gap-1 border-r px-2"
                style={{ width: colWidth(i), height: ROW_HEIGHT }}
                onClick={() => cycleSort(i)}
              >
                <span className="truncate">{c.name}</span>
                {sort?.col === i ? (
                  sort.desc ? (
                    <ArrowDown className="size-3 shrink-0 text-primary" />
                  ) : (
                    <ArrowUp className="size-3 shrink-0 text-primary" />
                  )
                ) : (
                  <ArrowUpDown className="size-3 shrink-0 text-muted-foreground/40" />
                )}
                <span className="ml-auto shrink-0 text-[10px] font-normal text-muted-foreground/70">{c.dataType}</span>
                <div
                  aria-hidden
                  onMouseDown={(e) => startResize(i, e)}
                  className="absolute right-0 top-0 h-full w-1 cursor-col-resize touch-none select-none hover:bg-border"
                />
              </div>
            ))}
          </div>

          {/* 数据行 */}
          <div className="relative" style={{ height: virtualizer.getTotalSize() }}>
            {virtualizer.getVirtualItems().map((vi) => {
              const row = sortedRows[vi.index]!
              return (
                <div
                  key={vi.key}
                  className="absolute left-0 top-0 flex items-stretch border-b text-xs hover:bg-accent/30"
                  style={{ height: vi.size, transform: `translateY(${vi.start}px)`, width: totalWidth }}
                >
                  <div
                    className="flex shrink-0 items-center justify-center border-r text-[10px] text-muted-foreground"
                    style={{ width: ROW_NO_WIDTH }}
                  >
                    {vi.index + 1}
                  </div>
                  {rs.columns.map((c, ci) => {
                    const v = row[ci]
                    const text = cellText(v)
                    const isLong = text.length > LONG_TEXT
                    const isSel = selected?.r === vi.index && selected?.c === ci
                    return (
                      <ContextMenu key={`${c.name}-${ci}`}>
                        <ContextMenuTrigger asChild>
                          <div
                            className={cn(
                              "flex shrink-0 cursor-default items-center border-r px-2",
                              isSel && "ring-1 ring-primary ring-inset bg-accent/50",
                            )}
                            style={{ width: colWidth(ci) }}
                            onMouseDown={() => setSelected({ r: vi.index, c: ci })}
                            onDoubleClick={() => (isJsonish(v) || text.length > 200) && setViewer({ r: vi.index, c: ci })}
                          >
                            {v === null || v === undefined ? (
                              <span className="italic text-muted-foreground/40">NULL</span>
                            ) : (
                              <span className={cn("truncate", typeof v === "boolean" && "text-amber-600 dark:text-amber-400")}>
                                {text}
                              </span>
                            )}
                            {isLong && (
                              <span className="pointer-events-none absolute right-1 text-[9px] text-muted-foreground/50">
                                ⤢
                              </span>
                            )}
                          </div>
                        </ContextMenuTrigger>
                        <ContextMenuContent>
                          <ContextMenuItem onClick={() => copyClipboard(v === null || v === undefined ? "NULL" : text, "单元格")}>
                            <Copy /> 复制单元格
                          </ContextMenuItem>
                          <ContextMenuItem
                            onClick={() =>
                              copyClipboard(
                                JSON.stringify(Object.fromEntries(rs.columns.map((c2, i2) => [c2.name, row[i2]])), null, 2),
                                "整行 JSON",
                              )
                            }
                          >
                            <FileJson /> 复制整行（JSON）
                          </ContextMenuItem>
                          <ContextMenuItem
                            onClick={() =>
                              copyClipboard(
                                toInsert(
                                  rs.columns.map((c2) => c2.name),
                                  row,
                                  tableName,
                                ),
                                "INSERT 语句",
                              )
                            }
                          >
                            <Table2 /> 复制为 INSERT
                          </ContextMenuItem>
                          <ContextMenuSeparator />
                          <ContextMenuItem
                            onClick={() =>
                              copyClipboard(
                                [rs.columns[ci]!.name, ...rs.rows.map((r2) => cellText(r2[ci]))].join("\n"),
                                "整列",
                              )
                            }
                          >
                            <Copy /> 复制整列
                          </ContextMenuItem>
                          <ContextMenuItem onClick={() => setViewer({ r: vi.index, c: ci })}>
                            <Braces /> 查看内容
                          </ContextMenuItem>
                        </ContextMenuContent>
                      </ContextMenu>
                    )
                  })}
                </div>
              )
            })}
          </div>

          {sortedRows.length === 0 && (
            <div className="flex h-24 items-center justify-center text-sm text-muted-foreground">
              {filter ? "无匹配行" : "空结果集"}
            </div>
          )}
        </div>
      </div>

      {/* 单元格查看器 */}
      <Dialog open={viewer !== null} onOpenChange={(o) => !o && setViewer(null)}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2 text-sm font-medium">
              {viewer ? `${rs.columns[viewer.c]?.name} · 第 ${(viewer.r ?? 0) + 1} 行` : ""}
              {viewIsJson && (
                <span className="rounded bg-muted px-1.5 py-0.5 text-[10px] text-muted-foreground">JSON</span>
              )}
              <Button
                variant="ghost"
                size="sm"
                className="ml-auto h-6"
                onClick={() =>
                  copyClipboard(
                    viewIsJson
                      ? JSON.stringify(JSON.parse(cellText(viewText)), null, 2)
                      : (cellText(viewText) || "NULL"),
                    "内容",
                  )
                }
              >
                <Copy /> 复制
              </Button>
            </DialogTitle>
          </DialogHeader>
          <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap break-all rounded-md bg-muted/50 p-3 font-mono text-xs leading-relaxed">
            {viewText === null || viewText === undefined
              ? "NULL"
              : viewIsJson
                ? (() => {
                    try {
                      return JSON.stringify(JSON.parse(cellText(viewText)), null, 2)
                    } catch {
                      return cellText(viewText)
                    }
                  })()
                : cellText(viewText)}
          </pre>
        </DialogContent>
      </Dialog>
    </div>
  )
}
