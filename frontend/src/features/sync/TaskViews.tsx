import { useEffect, useRef, useState } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Ban, Clock, Download, RotateCcw, StepForward } from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Progress } from "@/components/ui/progress"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { api } from "@/lib/api"
import { Stagger, StaggerItem } from "@/components/ui/motion"
import { CompareResultView, DiffViewer } from "@/features/sync/CompareViews"
import type { SyncTaskDto, SyncTaskStatus, TableSyncReport } from "@/types/api"

export function statusVariant(status: SyncTaskStatus): "default" | "destructive" | "secondary" | "outline" {
  if (status === "Completed") return "default"
  if (status === "Failed" || status === "PartiallyFailed") return "destructive"
  if (status === "Running" || status === "CancelRequested") return "outline"
  return "secondary"
}

/** SSE 订阅任务状态；出错自动降级为轮询。 */
export function useTaskSse(taskId: string | null | undefined) {
  const [task, setTask] = useState<SyncTaskDto | undefined>()
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const qc = useQueryClient()

  useEffect(() => {
    setTask(undefined)
    if (!taskId) return
    let closed = false
    const startPolling = () => {
      if (closed || pollRef.current) return
      pollRef.current = setInterval(() => {
        api.getSyncTask(taskId).then(setTask).catch(() => {})
      }, 1500)
    }
    const es = new EventSource(`/api/sync/tasks/${taskId}/events`)
    es.addEventListener("state", (ev) => {
      try {
        setTask(JSON.parse((ev as MessageEvent).data) as SyncTaskDto)
      } catch { /* 忽略坏帧 */ }
    })
    es.onerror = () => {
      es.close()
      startPolling()
    }
    return () => {
      closed = true
      es.close()
      if (pollRef.current) clearInterval(pollRef.current)
      pollRef.current = null
      void qc.invalidateQueries({ queryKey: ["syncTasks"] })
    }
  }, [taskId, qc])

  return task
}

const TERMINAL: SyncTaskStatus[] = ["Completed", "Failed", "Cancelled", "PartiallyFailed", "Interrupted"]

/** 运行详情：进度 / 日志 / 报告（SSE 驱动）。 */
export function TaskRunView({ taskId }: { taskId: string }) {
  const task = useTaskSse(taskId)
  const logRef = useRef<HTMLDivElement>(null)
  const qc = useQueryClient()

  useEffect(() => {
    if (logRef.current) logRef.current.scrollTop = logRef.current.scrollHeight
  }, [task?.log.length])

  if (!task) {
    return (
      <div className="space-y-2 p-4">
        <Skeleton className="h-8 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    )
  }

  const running = task.status === "Running" || task.status === "Queued" || task.status === "CancelRequested"

  async function act(fn: () => Promise<unknown>, msg: string) {
    try {
      await fn()
      toast.success(msg)
      qc.invalidateQueries({ queryKey: ["syncTasks"] })
    } catch (e) {
      toast.error((e as Error).message)
    }
  }

  return (
    <div className="animate-in space-y-3 fade-in slide-in-from-bottom-1 duration-300">
      <div className="rounded-lg border p-4">
        <div className="mb-2 flex items-center gap-2 text-sm font-medium">
          执行进度
          <Badge variant={statusVariant(task.status)} className={running ? "animate-pulse-dot" : undefined}>
            {task.status}
          </Badge>
          {task.kind === "Compare" && <Badge variant="outline" className="text-[10px]">对比</Badge>}
          {task.currentTable && <span className="text-xs text-muted-foreground">当前: {task.currentTable}</span>}
          <span className="ml-auto text-xs">
            {task.rowsTotal > 0 && `≈${task.rowsDone}/${task.rowsTotal} 行 · `}{task.percent}%
          </span>
          <div className="flex gap-1">
            {running && (
              <Button size="xs" variant="destructive" onClick={() => act(() => api.cancelSyncTask(task.id), "已请求取消")}>
                <Ban /> 取消
              </Button>
            )}
            {(task.status === "Interrupted" || task.status === "Cancelled" || task.status === "Failed") && (
              <Button size="xs" variant="outline" onClick={() => act(() => api.resumeSyncTask(task.id), "已提交续传")}>
                <StepForward /> 续传
              </Button>
            )}
            {TERMINAL.includes(task.status) && task.kind === "Sync" && (
              <Button size="xs" variant="outline" onClick={() => act(() => api.retrySyncTask(task.id), "已重新执行")}>
                <RotateCcw /> 重跑
              </Button>
            )}
            {TERMINAL.includes(task.status) && task.kind === "Sync" && (
              <Button size="xs" variant="outline" onClick={() => act(() => api.exportSyncScript(task.id), "脚本已导出")}>
                <Download /> 脚本
              </Button>
            )}
          </div>
        </div>
        <Progress value={task.percent} />
        {task.message && <div className="mt-2 text-xs text-muted-foreground">{task.message}</div>}
        {task.error && <div className="mt-2 text-sm text-destructive">{task.error}</div>}
      </div>

      <div className="rounded-lg border p-3">
        <div className="mb-1 text-xs font-medium text-muted-foreground">日志</div>
        <div ref={logRef} className="max-h-40 overflow-auto rounded bg-muted p-2 font-mono text-xs leading-5">
          {task.log.map((l, i) => (
            <div key={i}>{l}</div>
          ))}
          {task.log.length === 0 && <div className="text-muted-foreground">等待输出…</div>}
        </div>
      </div>

      {task.reports.length > 0 && <ReportView reports={task.reports} />}
    </div>
  )
}

/** 报告表：DDL / 数据统计 + 校验列。 */
export function ReportView({ reports }: { reports: TableSyncReport[] }) {
  return (
    <div className="rounded-lg border p-3">
      <div className="mb-2 text-xs font-medium text-muted-foreground">报告</div>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>表</TableHead>
            <TableHead>DDL 成功/失败</TableHead>
            <TableHead>插入</TableHead>
            <TableHead>更新</TableHead>
            <TableHead>删除</TableHead>
            <TableHead>校验</TableHead>
            <TableHead>耗时</TableHead>
            <TableHead>状态</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {reports.map((r) => (
            <TableRow key={r.table}>
              <TableCell className="text-xs">{r.table}</TableCell>
              <TableCell className="text-xs">{r.ddlExecuted}/{r.ddlFailed}</TableCell>
              <TableCell className="text-xs">{r.rowsInserted}</TableCell>
              <TableCell className="text-xs">{r.rowsUpdated}</TableCell>
              <TableCell className="text-xs">{r.rowsDeleted}</TableCell>
              <TableCell className="text-xs">
                {r.verification ? (
                  r.verification.match ? (
                    <span className="text-emerald-600 dark:text-emerald-400">
                      ✓ {r.verification.sourceRows}
                      {r.verification.sampleMatch === false && " (抽样不一致)"}
                    </span>
                  ) : (
                    <span className="text-destructive">
                      ✗ {r.verification.sourceRows}≠{r.verification.targetRows}
                    </span>
                  )
                ) : (
                  "—"
                )}
              </TableCell>
              <TableCell className="text-xs">{(r.elapsedMs / 1000).toFixed(2)}s</TableCell>
              <TableCell className="text-xs">
                {r.error ? <Badge variant="destructive">失败</Badge> : <Badge>成功</Badge>}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {reports.some((r) => r.error) && (
        <div className="mt-2 space-y-1">
          {reports
            .filter((r) => r.error)
            .map((r) => (
              <div key={r.table} className="text-xs text-destructive">
                {r.table}: {r.error}
              </div>
            ))}
        </div>
      )}
    </div>
  )
}

/** 任务历史列表。 */
export function TaskHistory({ onView }: { onView: (id: string) => void }) {
  const tasks = useQuery({
    queryKey: ["syncTasks"],
    queryFn: () => api.listSyncTasks({ limit: 50 }),
    refetchInterval: (q) =>
      q.state.data?.some((t) => t.status === "Running" || t.status === "Queued") ? 2000 : 8000,
  })

  if (tasks.isLoading) {
    return (
      <div className="space-y-2 p-4">
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-10 w-full" />
        ))}
      </div>
    )
  }

  return (
    <ScrollArea className="min-h-0 flex-1">
      <div className="mx-auto max-w-4xl space-y-3 p-4">
        <div className="rounded-lg border p-3">
          <div className="mb-2 text-sm font-medium">同步任务</div>
          {tasks.data && tasks.data.length === 0 && (
            <div className="py-8 text-center text-xs text-muted-foreground">还没有同步任务</div>
          )}
          <Stagger className="space-y-1">
            {tasks.data?.map((t) => (
              <StaggerItem key={t.id}>
                <div className="flex items-center gap-3 rounded border px-3 py-2 text-xs">
                  <Badge
                    variant={statusVariant(t.status)}
                    className={t.status === "Running" || t.status === "Queued" ? "animate-pulse-dot" : undefined}
                  >
                    {t.status}
                  </Badge>
                  {t.kind === "Compare" && <Badge variant="outline" className="text-[10px]">对比</Badge>}
                  <span className="text-muted-foreground">{new Date(t.startedAt).toLocaleString()}</span>
                  {t.currentTable && <span className="min-w-0 truncate">当前: {t.currentTable}</span>}
                  <span className="ml-auto flex items-center gap-2">
                    <Progress value={t.percent} className="h-1.5 w-24" />
                    <span>{t.percent}%</span>
                  </span>
                  <Button variant="outline" size="xs" onClick={() => onView(t.id)}>
                    <Clock /> 查看
                  </Button>
                </div>
              </StaggerItem>
            ))}
          </Stagger>
        </div>
      </div>
    </ScrollArea>
  )
}

/** 任务详情页（含返回）。 */
export function TaskDetailPage({ taskId, onBack }: { taskId: string; onBack: () => void }) {
  const task = useQuery({ queryKey: ["syncTask", taskId], queryFn: () => api.getSyncTask(taskId) })
  return (
    <ScrollArea className="min-h-0 flex-1">
      <div className="mx-auto max-w-4xl space-y-3 p-4">
        <Button variant="outline" size="sm" onClick={onBack}>
          ← 返回列表
        </Button>
        {task.data?.kind === "Compare" && task.data.compare ? (
          <CompareDetailInline taskId={taskId} />
        ) : (
          <TaskRunView taskId={taskId} />
        )}
      </div>
    </ScrollArea>
  )
}

function CompareDetailInline({ taskId }: { taskId: string }) {
  const [table, setTable] = useState<string | null>(null)
  return table ? (
    <DiffViewer taskId={taskId} table={table} onBack={() => setTable(null)} />
  ) : (
    <CompareResultView taskId={taskId} onOpenTable={setTable} />
  )
}
