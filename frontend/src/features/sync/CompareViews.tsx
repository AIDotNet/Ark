import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { ArrowLeft, CircleAlert, Table2 } from "lucide-react"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { api } from "@/lib/api"
import type { DiffRowsResponse, SyncTaskDto, TableCompareResult } from "@/types/api"

/** 差异统计胶囊：+插入 ~更新 -删除 */
export function StatPill({ r }: { r: TableCompareResult }) {
  return (
    <span className="inline-flex items-center gap-1.5 font-mono text-[11px]">
      {r.inserted > 0 && <span className="text-emerald-600 dark:text-emerald-400">+{r.inserted}</span>}
      {r.updated > 0 && <span className="text-amber-600 dark:text-amber-400">~{r.updated}</span>}
      {r.deleted > 0 && <span className="text-destructive">-{r.deleted}</span>}
      {r.inserted + r.updated + r.deleted === 0 && <span className="text-muted-foreground">无数据差异</span>}
      {r.structureChanges > 0 && (
        <Badge variant="outline" className="text-[10px]">
          结构 {r.structureChanges}
        </Badge>
      )}
    </span>
  )
}

/** 对比结果：表卡片列表。 */
export function CompareResultView({
  taskId,
  onOpenTable,
}: {
  taskId: string
  onOpenTable: (table: string) => void
}) {
  const task = useQuery({
    queryKey: ["syncTask", taskId],
    queryFn: () => api.getSyncTask(taskId),
    refetchInterval: (q) => {
      const t = q.state.data as SyncTaskDto | undefined
      return t && (t.status === "Running" || t.status === "Queued") ? 1500 : false
    },
  })

  if (task.isLoading) {
    return (
      <div className="space-y-2">
        {Array.from({ length: 3 }).map((_, i) => (
          <Skeleton key={i} className="h-14 w-full" />
        ))}
      </div>
    )
  }

  const compare = task.data?.compare
  if (!compare) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <CircleAlert className="size-4" />
        {task.data?.status === "Running" ? "对比进行中…" : "对比结果不可用"}
      </div>
    )
  }

  const diffTables = compare.tables.filter((t) => !t.error)
  const cleanTables = diffTables.filter((t) => t.inserted + t.updated + t.deleted === 0)

  return (
    <div className="space-y-2">
      {compare.tables.map((t) => (
        <div key={t.table} className="rounded-lg border p-3">
          <div className="flex items-center gap-2 text-sm font-medium">
            <Table2 className="size-4 text-muted-foreground" />
            {t.table}
            {t.error ? (
              <Badge variant="destructive">{t.error}</Badge>
            ) : (
              <StatPill r={t} />
            )}
            {!t.error && (
              <Button
                size="xs"
                variant="outline"
                className="ml-auto"
                disabled={t.inserted + t.updated + t.deleted === 0}
                onClick={() => onOpenTable(t.table)}
              >
                查看差异行
              </Button>
            )}
          </div>
          {t.verification && !t.verification.countsMatch && (
            <div className="mt-1 text-xs text-destructive">
              行数不一致：源 {t.verification.sourceRows} ≠ 目标 {t.verification.targetRows}
            </div>
          )}
          {t.samplesTruncated && (
            <div className="mt-1 text-[11px] text-muted-foreground">差异键过多，仅保留前若干条（应用阶段仍会全量处理）</div>
          )}
        </div>
      ))}
      {cleanTables.length === diffTables.length && diffTables.length > 0 && (
        <div className="rounded-lg border border-emerald-500/40 bg-emerald-500/5 p-3 text-sm">
          所选表数据完全一致。
        </div>
      )}
    </div>
  )
}

type DiffType = "inserted" | "updated" | "deleted"

/** 差异行查看器：按类型分页拉取差异键与两侧行值。 */
export function DiffViewer({
  taskId,
  table,
  onBack,
}: {
  taskId: string
  table: string
  onBack: () => void
}) {
  const [type, setType] = useState<DiffType>("inserted")
  const [rows, setRows] = useState<DiffRowsResponse | null>(null)

  const q = useQuery({
    queryKey: ["diffRows", taskId, table, type],
    queryFn: () => api.fetchDiffRows(taskId, table, type, 0, 100),
  })

  function switchType(t: string) {
    setType(t as DiffType)
    setRows(null)
  }

  function loadMore() {
    if (!q.data) return
    const base = rows ?? q.data
    api
      .fetchDiffRows(taskId, table, type, base.cursor, 100)
      .then((next) => {
        setRows({
          ...next,
          rows: [...base.rows, ...next.rows],
        })
      })
      .catch(() => {})
  }

  const data = rows ?? q.data
  const emptyCell = (v: string | null | undefined) =>
    v === undefined ? null : v === "s:" ? "" : v

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <Button variant="outline" size="sm" onClick={onBack}>
          <ArrowLeft /> 返回对比
        </Button>
        <span className="text-sm font-medium">{table}</span>
      </div>

      <Tabs value={type} onValueChange={switchType}>
        <TabsList>
          <TabsTrigger value="inserted">新增 ({q.data?.total ?? 0})</TabsTrigger>
          <TabsTrigger value="updated">变更 ({q.data?.total ?? 0})</TabsTrigger>
          <TabsTrigger value="deleted">删除 ({q.data?.total ?? 0})</TabsTrigger>
        </TabsList>
        <TabsContent value={type} className="mt-3">
          {q.isLoading ? (
            <Skeleton className="h-40 w-full" />
          ) : !data || data.rows.length === 0 ? (
            <div className="rounded border p-6 text-center text-xs text-muted-foreground">无差异</div>
          ) : (
            <ScrollArea className="max-h-[420px] rounded-md border">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>主键</TableHead>
                    {data.columns.map((c) => (
                      <TableHead key={c}>{c}</TableHead>
                    ))}
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {data.rows.map((r) => (
                    <TableRow key={r.key}>
                      <TableCell className="max-w-32 truncate font-mono text-[11px] text-muted-foreground">
                        {r.key.replaceAll("\x1F", ",").replace(/s:/g, "").replace(/\x01NULL/g, "NULL")}
                      </TableCell>
                      {data.columns.map((c, i) => {
                        const src = emptyCell(r.source?.[i])
                        const tgt = emptyCell(r.target?.[i])
                        const changed = type === "updated" && src !== tgt
                        return (
                          <TableCell key={c} className="max-w-52 truncate text-[11px]">
                            {changed ? (
                              <span>
                                <span className="mr-1 text-destructive line-through">{tgt}</span>
                                <span className="text-emerald-600 dark:text-emerald-400">{src}</span>
                              </span>
                            ) : (
                              (type === "deleted" ? (tgt ?? "") : (src ?? ""))
                            )}
                          </TableCell>
                        )
                      })}
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </ScrollArea>
          )}
          {data && data.rows.length > 0 && data.cursor < data.total && (
            <div className="mt-2 text-center">
              <Button variant="ghost" size="sm" onClick={loadMore}>
                加载更多（{data.rows.length}/{data.total}）
              </Button>
              {data.truncated && (
                <span className="ml-2 text-[11px] text-muted-foreground">差异键列表被截断，仅展示已捕获部分</span>
              )}
            </div>
          )}
        </TabsContent>
      </Tabs>
    </div>
  )
}
