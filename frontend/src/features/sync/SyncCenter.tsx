import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { CalendarClock, Copy, Pencil, Play, Plus, RefreshCw, Trash2 } from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Skeleton } from "@/components/ui/skeleton"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { TooltipProvider } from "@/components/ui/tooltip"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from "@/components/ui/alert-dialog"
import { api } from "@/lib/api"
import { CronField } from "@/features/sync/CronField"
import { NewSyncFlow } from "@/features/sync/NewSyncFlow"
import { TaskDetailPage, TaskHistory } from "@/features/sync/TaskViews"
import type { SyncProfileDto } from "@/types/api"

/** 同步中心：配置（Profile）列表 + 任务历史 + 快速同步入口。 */
export function SyncCenter() {
  const [tab, setTab] = useState("profiles")
  const [flowOpen, setFlowOpen] = useState(false)
  const [editing, setEditing] = useState<SyncProfileDto | undefined>()
  const [viewTaskId, setViewTaskId] = useState<string | null>(null)
  const [deleting, setDeleting] = useState<SyncProfileDto | null>(null)
  const qc = useQueryClient()

  const profiles = useQuery({ queryKey: ["syncProfiles"], queryFn: api.listSyncProfiles })

  const runProfile = useMutation({
    mutationFn: (id: string) => api.runSyncProfile(id),
    onSuccess: (t) => {
      toast.success("任务已提交")
      setViewTaskId(t.id)
      setTab("tasks")
      void qc.invalidateQueries({ queryKey: ["syncTasks"] })
    },
    onError: (e) => toast.error((e as Error).message),
  })

  const deleteProfile = useMutation({
    mutationFn: (id: string) => api.deleteSyncProfile(id),
    onSuccess: () => {
      toast.success("已删除")
      setDeleting(null)
      void qc.invalidateQueries({ queryKey: ["syncProfiles"] })
    },
    onError: (e) => toast.error((e as Error).message),
  })

  const toggleSchedule = useMutation({
    mutationFn: (p: { profile: SyncProfileDto; cron: string | null; enabled: boolean }) =>
      api.updateSyncProfileSchedule(p.profile.id, { cron: p.cron, scheduleEnabled: p.enabled }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["syncProfiles"] }),
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <TooltipProvider>
      {/* Tabs 作为布局根：TabsContent 必须位于 Tabs 内部，否则 Radix 抛错卸载整树（白屏） */}
      <Tabs value={tab} onValueChange={setTab} className="flex min-h-0 flex-1 flex-col">
        <div className="flex shrink-0 items-center gap-2 border-b px-4 py-2">
          <TabsList>
            <TabsTrigger value="profiles">同步配置</TabsTrigger>
            <TabsTrigger value="tasks">任务历史</TabsTrigger>
          </TabsList>
          <div className="ml-auto flex gap-1">
            <Button
              size="sm"
              onClick={() => {
                setEditing(undefined)
                setFlowOpen(true)
              }}
            >
              <Plus /> 新建同步
            </Button>
          </div>
        </div>

        {viewTaskId ? (
          <TaskDetailPage taskId={viewTaskId} onBack={() => setViewTaskId(null)} />
        ) : (
          <>
            <TabsContent value="profiles" className="min-h-0 flex-1 overflow-hidden border-0 p-0">
              <ScrollArea className="min-h-0 flex-1">
                <div className="mx-auto max-w-4xl space-y-3 p-4">
                  {profiles.isLoading && (
                    <div className="space-y-2">
                      {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-20 w-full" />)}
                    </div>
                  )}
                  {profiles.data && profiles.data.length === 0 && (
                    <div className="rounded-lg border border-dashed p-10 text-center text-sm text-muted-foreground">
                      还没有同步配置。点击「新建同步」创建一次同步，可顺手保存为配置（支持定时调度）。
                    </div>
                  )}
                  <div className="grid gap-3 md:grid-cols-2">
                    {profiles.data?.map((p) => (
                      <ProfileCard
                        key={p.id}
                        profile={p}
                        onRun={() => runProfile.mutate(p.id)}
                        running={runProfile.isPending && runProfile.variables === p.id}
                        onEdit={() => {
                          setEditing(p)
                          setFlowOpen(true)
                        }}
                        onDuplicate={async () => {
                          try {
                            await api.createSyncProfile({
                              name: `${p.name} 副本`,
                              config: p.config,
                              cron: p.cron,
                              scheduleEnabled: false,
                            })
                            void qc.invalidateQueries({ queryKey: ["syncProfiles"] })
                          } catch (e) {
                            toast.error((e as Error).message)
                          }
                        }}
                        onDelete={() => setDeleting(p)}
                        onScheduleChange={(cron, enabled) =>
                          toggleSchedule.mutate({ profile: p, cron, enabled })
                        }
                      />
                    ))}
                  </div>
                </div>
              </ScrollArea>
            </TabsContent>

            <TabsContent value="tasks" className="min-h-0 flex-1 overflow-hidden border-0 p-0">
              <TaskHistory onView={(id) => { setViewTaskId(id); setTab("tasks") }} />
            </TabsContent>
          </>
        )}

        <NewSyncFlow
          open={flowOpen}
          onOpenChange={setFlowOpen}
          initial={editing && {
            name: editing.name,
            config: editing.config,
            cron: editing.cron,
            scheduleEnabled: editing.scheduleEnabled,
            profileId: editing.id,
          }}
          onSaved={() => void qc.invalidateQueries({ queryKey: ["syncProfiles"] })}
        />

        <AlertDialog open={!!deleting} onOpenChange={(v) => !v && setDeleting(null)}>
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>删除同步配置「{deleting?.name}」？</AlertDialogTitle>
              <AlertDialogDescription>
                仅删除配置本身，不影响已创建的任务历史与数据库数据。
              </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
              <AlertDialogCancel>取消</AlertDialogCancel>
              <AlertDialogAction
                className="bg-destructive text-white"
                onClick={() => deleting && deleteProfile.mutate(deleting.id)}
              >
                删除
              </AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      </Tabs>
    </TooltipProvider>
  )
}

function ProfileCard({
  profile,
  onRun,
  onEdit,
  onDuplicate,
  onDelete,
  onScheduleChange,
  running,
}: {
  profile: SyncProfileDto
  onRun: () => void
  onEdit: () => void
  onDuplicate: () => void
  onDelete: () => void
  onScheduleChange: (cron: string | null, enabled: boolean) => void
  running: boolean
}) {
  const cfg = profile.config
  const connNames = useQuery({ queryKey: ["connections"], queryFn: api.listConnections })
  // 历史配置的连接 ID/库名可能为空（如旧版 smoke 配置），展示时兜底避免渲染崩溃
  const nameOf = (id: string | null | undefined, db: string | null | undefined) => {
    const c = id ? connNames.data?.find((x) => x.id === id) : undefined
    return `${c?.name ?? (id ? id.slice(0, 8) : "未知连接")}·${db || "未知库"}`
  }
  const tables = cfg.tables?.length ?? 0

  return (
    <div className="flex flex-col gap-2 rounded-lg border p-3">
      <div className="flex items-center gap-2">
        <span className="truncate text-sm font-medium">{profile.name}</span>
        {profile.scheduleEnabled && (
          <Badge variant="outline" className="gap-1 text-[10px] text-emerald-600 dark:text-emerald-400">
            <CalendarClock className="size-3" />
            {profile.nextRun ? new Date(profile.nextRun).toLocaleString() : profile.cron}
          </Badge>
        )}
        <div className="ml-auto flex gap-0.5">
          <Button size="icon" variant="ghost" title="运行" onClick={onRun} disabled={running}>
            {running ? <RefreshCw className="animate-spin" /> : <Play />}
          </Button>
          <Button size="icon" variant="ghost" title="编辑" onClick={onEdit}>
            <Pencil />
          </Button>
          <Button size="icon" variant="ghost" title="复制" onClick={onDuplicate}>
            <Copy />
          </Button>
          <Button size="icon" variant="ghost" title="删除" onClick={onDelete}>
            <Trash2 />
          </Button>
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
        <span className="font-mono">
          {nameOf(cfg.sourceConnectionId, cfg.sourceDatabase)} → {nameOf(cfg.targetConnectionId, cfg.targetDatabase)}
        </span>
        <Badge variant="secondary" className="text-[10px]">
          {cfg.mode === "StructureOnly" ? "仅结构" : cfg.dataMethod === "RowDiff" ? "结构+Diff" : "结构+全量"}
        </Badge>
        <Badge variant="outline" className="text-[10px]">{tables} 张表</Badge>
        {cfg.conflictMode === "Truncate" && <Badge variant="outline" className="text-[10px]">先清空</Badge>}
        {cfg.conflictMode === "SkipExisting" && <Badge variant="outline" className="text-[10px]">跳过已存在</Badge>}
      </div>
      <div className="border-t pt-2">
        <CronField
          cron={profile.cron}
          enabled={profile.scheduleEnabled}
          onChange={(cron, enabled) => onScheduleChange(cron, enabled)}
        />
      </div>
      {profile.lastRunAt && (
        <div className="text-[11px] text-muted-foreground">上次运行：{new Date(profile.lastRunAt).toLocaleString()}</div>
      )}
    </div>
  )
}
