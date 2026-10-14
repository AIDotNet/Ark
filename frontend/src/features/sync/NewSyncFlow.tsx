import { useEffect, useMemo, useState, type ReactNode } from "react"
import { useQuery } from "@tanstack/react-query"
import { motion } from "motion/react"
import { ArrowRight, Check, ChevronDown, CircleAlert, GitCompareArrows, Loader2, Play, Search, TriangleAlert } from "lucide-react"
import { toast } from "sonner"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
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
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group"
import { Skeleton } from "@/components/ui/skeleton"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"
import { api } from "@/lib/api"
import { useWorkspace } from "@/stores/workspace"
import { defaultSchema } from "@/features/connections/dialects"
import { Collapse, springSoft, stepTransition } from "@/components/ui/motion"
import { CronField } from "@/features/sync/CronField"
import { CompareResultView } from "@/features/sync/CompareViews"
import { TaskRunView } from "@/features/sync/TaskViews"
import type {
  ConflictMode,
  DataSyncMethod,
  ExecutionPlan,
  ExtensionDependency,
  SyncMode,
  SyncPlanRequest,
  TableSelection,
  VerifyMode,
} from "@/types/api"

type Step = 0 | 1 | 2 | 3
const STEP_TITLES = ["源与目标", "范围与选项", "审阅", "执行"]

interface Draft {
  srcConn: string
  srcDb: string
  tgtConn: string
  tgtDb: string
  tgtSchema: string
  mode: SyncMode
  dataMethod: DataSyncMethod
  conflictMode: ConflictMode
  deleteExtraRows: boolean
  dropExtraTables: boolean
  alignAutoIncrement: boolean
  deferIndexes: boolean
  syncExtensions: boolean
  verifyMode: VerifyMode
  batchSize: string
  chunkRows: string
  maxParallelTables: string
  compareFirst: boolean
}

const initialDraft: Draft = {
  srcConn: "",
  srcDb: "",
  tgtConn: "",
  tgtDb: "",
  tgtSchema: "",
  mode: "StructureAndData",
  dataMethod: "FullCopy",
  conflictMode: "Error",
  deleteExtraRows: false,
  dropExtraTables: false,
  alignAutoIncrement: true,
  deferIndexes: false,
  syncExtensions: false,
  verifyMode: "Sample",
  batchSize: "1000",
  chunkRows: "2000",
  maxParallelTables: "2",
  compareFirst: false,
}

/** 由已保存的 Profile 配置还原表单草稿（编辑模式打开时使用）。 */
function draftFromConfig(c: SyncPlanRequest): Partial<Draft> {
  return {
    srcConn: c.sourceConnectionId,
    srcDb: c.sourceDatabase,
    tgtConn: c.targetConnectionId,
    tgtDb: c.targetDatabase,
    tgtSchema: c.targetSchema ?? "",
    mode: c.mode,
    dataMethod: c.dataMethod,
    conflictMode: c.conflictMode,
    deleteExtraRows: c.deleteExtraRows,
    dropExtraTables: c.dropExtraTables,
    alignAutoIncrement: c.alignAutoIncrement,
    deferIndexes: c.deferIndexes,
    syncExtensions: c.syncExtensions ?? false,
    verifyMode: c.verifyMode,
    batchSize: String(c.batchSize),
    chunkRows: String(c.chunkRows),
    maxParallelTables: String(c.maxParallelTables),
  }
}

/** 高级参数是否偏离默认值（偏离时自动展开高级选项区）。 */
function hasAdvanced(d: Partial<Draft>): boolean {
  return (
    d.batchSize !== initialDraft.batchSize ||
    d.chunkRows !== initialDraft.chunkRows ||
    d.maxParallelTables !== initialDraft.maxParallelTables ||
    !!d.deferIndexes ||
    !!d.syncExtensions
  )
}

/** 新建/快速同步向导（4 步），支持“先对比”模式。 */
export function NewSyncFlow({
  open,
  onOpenChange,
  initial,
  onSaved,
}: {
  open: boolean
  onOpenChange: (v: boolean) => void
  /** 编辑 Profile 时带入的初始配置 */
  initial?: { name?: string; config?: SyncPlanRequest; cron?: string | null; scheduleEnabled?: boolean; profileId?: string }
  onSaved?: () => void
}) {
  const [step, setStep] = useState<Step>(0)
  // 步骤方向：前进左滑入、后退右滑入（state 而非 ref：render 期需要读取）
  const [stepDir, setStepDir] = useState<1 | -1>(1)
  const go = (next: Step) => {
    setStepDir(next >= step ? 1 : -1)
    setStep(next)
  }
  const [d, setD] = useState<Draft>(() => ({
    ...initialDraft,
    ...(initial?.config ? draftFromConfig(initial.config) : {}),
  }))
  const [selectedTables, setSelectedTables] = useState<Set<string>>(new Set())
  const [search, setSearch] = useState("")
  const [plan, setPlan] = useState<ExecutionPlan | null>(null)
  const [skipped, setSkipped] = useState<Set<string>>(new Set())
  const [confirmDestructive, setConfirmDestructive] = useState(false)
  const [taskId, setTaskId] = useState<string | null>(null)
  const [compareTaskId, setCompareTaskId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [saveProfile, setSaveProfile] = useState(false)
  const [profileName, setProfileName] = useState(initial?.name ?? "")
  const [cron, setCron] = useState<string | null>(initial?.cron ?? null)
  const [cronEnabled, setCronEnabled] = useState(initial?.scheduleEnabled ?? false)
  const [showAdvanced, setShowAdvanced] = useState(() => hasAdvanced(initial?.config ? draftFromConfig(initial.config) : {}))
  const [taskRunning, setTaskRunning] = useState(false)
  const [confirmClose, setConfirmClose] = useState(false)
  const isEditing = !!initial?.profileId

  const patch = (p: Partial<Draft>) => setD((s) => ({ ...s, ...p }))

  // 打开时按场景恢复默认：编辑模式以 Profile 为准并默认保存；新建模式清掉上次的保存选项
  useEffect(() => {
    if (!open) return
    setTaskRunning(false)
    setConfirmClose(false)
    setShowAdvanced(hasAdvanced(initial?.config ? draftFromConfig(initial.config) : {}))
    if (isEditing && initial?.config) {
      // 修复：换一个 Profile 编辑时，表单必须以该 Profile 为准（此前草稿会残留上一次的值）
      setD({ ...initialDraft, ...draftFromConfig(initial.config), compareFirst: false })
      setProfileName(initial.name ?? "")
      setCron(initial.cron ?? null)
      setCronEnabled(initial.scheduleEnabled ?? false)
      setSaveProfile(true)
    } else {
      setSaveProfile(false)
      setProfileName("")
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, initial?.profileId])

  const tabs = useWorkspace((s) => s.tabs)
  const connections = useQuery({ queryKey: ["connections"], queryFn: () => api.listConnections() })
  // 默认带出当前活动连接
  const connById = (id: string) => connections.data?.find((c) => c.id === id)
  const connLabel = (id: string, db: string) => {
    const c = connById(id)
    return `${c ? `${c.name}（${c.dialect}）` : id.slice(0, 8)} · ${db}`
  }

  // 默认带出当前活动连接（仅新建流程、未选过连接时）
  const active = tabs.find((t) => t.connectionId !== "" && t.kind !== "sync")
  useEffect(() => {
    if (!open || initial?.profileId) return
    setD((s) =>
      !s.srcConn && active
        ? { ...s, srcConn: active.connectionId, srcDb: active.database }
        : s,
    )
  }, [open, active, initial?.profileId])
  const srcSchema = useMemo(() => {
    const c = connById(d.srcConn)
    return c ? defaultSchema(c.dialect, d.srcDb) : "public"
  }, [d.srcConn, d.srcDb, connections.data]) // eslint-disable-line react-hooks/exhaustive-deps
  const srcTables = useQuery({
    queryKey: ["tables", d.srcConn, d.srcDb, srcSchema],
    queryFn: () => api.listTables(d.srcConn, d.srcDb, srcSchema),
    enabled: !!d.srcConn && !!d.srcDb && step >= 1,
  })

  function buildRequest(tables: TableSelection[]): SyncPlanRequest {
    return {
      sourceConnectionId: d.srcConn,
      sourceDatabase: d.srcDb,
      targetConnectionId: d.tgtConn,
      targetDatabase: d.tgtDb,
      targetSchema: d.tgtSchema || null,
      mode: d.mode,
      dataMethod: d.dataMethod,
      conflictMode: d.conflictMode,
      dropExtraTables: d.dropExtraTables,
      deleteExtraRows: d.deleteExtraRows,
      alignAutoIncrement: d.alignAutoIncrement,
      batchSize: Number(d.batchSize) || 1000,
      chunkRows: Number(d.chunkRows) || 2000,
      deferIndexes: d.deferIndexes,
      syncExtensions: d.syncExtensions,
      verifyMode: d.verifyMode,
      maxParallelTables: Number(d.maxParallelTables) || 2,
      tables,
    }
  }

  const currentSelections = (): TableSelection[] =>
    [...selectedTables].map((t) => ({ database: d.srcDb, schema: srcSchema, table: t }))

  async function runCompare() {
    if (selectedTables.size === 0) return toast.error("请至少选择一张表")
    setBusy(true)
    try {
      const res = await api.startCompare({
        sourceConnectionId: d.srcConn,
        sourceDatabase: d.srcDb,
        targetConnectionId: d.tgtConn,
        targetDatabase: d.tgtDb,
        targetSchema: d.tgtSchema || null,
        tables: currentSelections(),
        chunkRows: Number(d.chunkRows) || 2000,
        maxDiffKeys: 2000,
      })
      setCompareTaskId(res.id)
      go(2)
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  async function buildPlan() {
    if (selectedTables.size === 0) return toast.error("请至少选择一张表")
    setBusy(true)
    try {
      const res = await api.buildSyncPlan(buildRequest(currentSelections()))
      setPlan(res)
      setSkipped(new Set(res.tables.flatMap((t) => t.structureActions.filter((a) => a.isDestructive).map((a) => a.id))))
      setConfirmDestructive(false)
      go(2)
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  async function generatePlanFromCompare() {
    if (!compareTaskId) return
    setBusy(true)
    try {
      const res = await api.compareToPlan(compareTaskId)
      setPlan(res)
      setSkipped(new Set(res.tables.flatMap((t) => t.structureActions.filter((a) => a.isDestructive).map((a) => a.id))))
      setConfirmDestructive(false)
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  async function submit() {
    if (!plan) return
    if (saveProfile && !profileName.trim()) return toast.error("请填写 Profile 名称")
    setBusy(true)
    try {
      const res = await api.submitSyncTask({ plan, skipActionIds: [...skipped], confirmDestructive })
      setTaskId(res.id)
      go(3)
      if (saveProfile && profileName.trim()) {
        try {
          const body = {
            name: profileName.trim(),
            config: plan.options,
            cron,
            scheduleEnabled: cronEnabled,
          }
          if (initial?.profileId) await api.updateSyncProfile(initial.profileId, body)
          else await api.createSyncProfile(body)
        } catch (e) {
          toast.error(`保存 Profile 失败: ${(e as Error).message}`)
        }
      }
      onSaved?.()
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  function reset() {
    go(0)
    setPlan(null)
    setTaskId(null)
    setCompareTaskId(null)
    setSelectedTables(new Set())
    setConfirmDestructive(false)
    setSearch("")
    setTaskRunning(false)
    setConfirmClose(false)
  }

  /** 关闭请求统一入口：任务运行中先拦截确认，其余直接关闭并复位 */
  function requestClose() {
    if (step === 3 && taskRunning) {
      setConfirmClose(true)
      return
    }
    onOpenChange(false)
    reset()
  }

  const tablesFiltered = (srcTables.data ?? []).filter((t) => t.name.toLowerCase().includes(search.toLowerCase()))
  const sameDb = d.srcConn === d.tgtConn && d.srcDb === d.tgtDb && d.srcConn !== ""

  // 计划中的破坏性动作：未跳过的部分需要显式确认后才允许提交（与后端“未确认即跳过”的语义对齐）
  const planDestructive = plan ? plan.tables.flatMap((t) => t.structureActions.filter((a) => a.isDestructive)) : []
  const pendingDestructive = planDestructive.filter((a) => !skipped.has(a.id))
  const destructiveBlocked = pendingDestructive.length > 0 && !confirmDestructive
  const step1Destructive = d.conflictMode === "Truncate" || d.dropExtraTables
  const estRowsTotal = plan?.tables.reduce((s, t) => s + (t.data?.estimatedRows ?? 0), 0) ?? 0
  const hasEstRows = plan?.tables.some((t) => t.data?.estimatedRows != null) ?? false
  const canNext0 = !!d.srcConn && !!d.srcDb && !!d.tgtConn && !!d.tgtDb && !sameDb

  return (
    <>
      <Dialog open={open} onOpenChange={(v) => { if (!v) requestClose() }}>
        {/* sm: 前缀必须带：基类是 sm:max-w-lg，无前缀的 max-w-3xl 在 CSS 中排在其前会被压回 32rem */}
        <DialogContent className="flex max-h-[92vh] flex-col overflow-hidden sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>{isEditing ? "编辑同步配置" : "新建同步"}</DialogTitle>
            <DialogDescription>
              {isEditing
                ? "调整配置后可保存到当前同步配置，并立即执行一次。"
                : "配置源与目标，选择同步范围，审阅变更后执行。"}
            </DialogDescription>
          </DialogHeader>

          {/* 步骤条：已完成的步骤可点击回跳 */}
          <div className="flex items-center gap-1 text-xs">
            {STEP_TITLES.map((t, i) => (
              <div key={t} className="flex items-center gap-1">
                <button
                  type="button"
                  disabled={i >= step}
                  onClick={() => go(i as Step)}
                  title={i < step ? `返回「${t}」` : undefined}
                  className={`relative rounded-full px-2 py-0.5 outline-none transition-colors duration-200 focus-visible:ring-2 focus-visible:ring-ring ${
                    i === step
                      ? "text-primary-foreground"
                      : i < step
                        ? "bg-accent text-accent-foreground hover:bg-accent/70"
                        : "text-muted-foreground"
                  } disabled:cursor-default`}
                >
                  {/* layoutId 胶囊：active 背景在步骤间平滑滑动 */}
                  {i === step && (
                    <motion.span
                      layoutId="sync-step-pill"
                      className="absolute inset-0 rounded-full bg-primary"
                      transition={springSoft}
                    />
                  )}
                  <span className="relative z-10 flex items-center gap-1">
                    {i < step && <Check className="size-3" />}
                    {i + 1}. {t}
                  </span>
                </button>
                {i < STEP_TITLES.length - 1 && <ArrowRight className="size-3 text-muted-foreground" />}
              </div>
            ))}
          </div>

          {/* 原生滚动：Radix ScrollArea 的 height:100% 在仅有 max-height 的 flex 容器内无法解析，多表长内容会溢出 */}
          <div className="min-h-0 flex-1 overflow-y-auto pr-2">
            {/* 步骤切换只做进场动画：AnimatePresence mode="wait" 依赖退场动画完成才挂载新步骤，
                在复杂步骤子树（Collapse/Select/长列表）上退场有概率永不完成，整个向导冻死在旧步骤
                （实测：点「生成同步计划」后底部按钮全部失效）。改为 key 换载 + 进场滑动，旧步骤立即卸载。 */}
            <motion.div
              key={step}
              initial={{ opacity: 0, x: stepDir >= 0 ? 36 : -36 }}
              animate={{ opacity: 1, x: 0 }}
              transition={stepTransition}
              className="space-y-4 pb-1"
            >
              {/* Step 0：源与目标 */}
              {step === 0 && (
                <>
                  <div className="grid grid-cols-2 gap-3">
                    <ConnDbPicker
                      title="源数据库"
                      connections={connections.data}
                      connId={d.srcConn}
                      db={d.srcDb}
                      onConn={(v) => patch({ srcConn: v, srcDb: "" })}
                      onDb={(v) => patch({ srcDb: v })}
                    />
                    <ConnDbPicker
                      title="目标数据库"
                      connections={connections.data}
                      connId={d.tgtConn}
                      db={d.tgtDb}
                      onConn={(v) => patch({ tgtConn: v, tgtDb: "" })}
                      onDb={(v) => patch({ tgtDb: v })}
                    />
                  </div>
                  {connById(d.tgtConn)?.dialect === "PostgreSQL" && (
                    <div className="flex items-center gap-3 rounded-lg border p-3">
                      <Label className="text-xs whitespace-nowrap">目标 Schema</Label>
                      <Input
                        className="h-8 w-44"
                        placeholder="public"
                        value={d.tgtSchema}
                        onChange={(e) => patch({ tgtSchema: e.target.value })}
                      />
                      <span className="text-[11px] text-muted-foreground">跨方言同步到 PostgreSQL 时指定目标 schema（默认 public）</span>
                    </div>
                  )}
                  {sameDb && (
                    <Alert variant="destructive">
                      <CircleAlert />
                      <AlertTitle>源库与目标库相同</AlertTitle>
                      <AlertDescription className="text-xs">请为目标选择另一个数据库，否则数据会被原地覆盖。</AlertDescription>
                    </Alert>
                  )}
                </>
              )}

              {/* Step 1：范围与选项（先选表，再定同步方式） */}
              {step === 1 && (
                <>
                  <TablePicker
                    tables={tablesFiltered}
                    total={(srcTables.data ?? []).length}
                    loading={srcTables.isLoading}
                    error={srcTables.error?.message}
                    selected={selectedTables}
                    search={search}
                    onSearch={setSearch}
                    onToggle={(t) =>
                      setSelectedTables((s) => {
                        const n = new Set(s)
                        if (n.has(t)) n.delete(t)
                        else n.add(t)
                        return n
                      })
                    }
                    onSelectAll={() => setSelectedTables(new Set(srcTables.data?.filter((t) => t.kind === "table").map((t) => t.name) ?? []))}
                    onClear={() => setSelectedTables(new Set())}
                    headerExtra={
                      <OptSwitch label="删除目标端多余表" checked={d.dropExtraTables} onChange={(v) => patch({ dropExtraTables: v })} />
                    }
                  />

                  <div className="rounded-lg border p-3">
                    <div className="mb-2 text-sm font-medium">同步内容</div>
                    <RadioGroup
                      value={d.mode}
                      onValueChange={(v) => patch({ mode: v as SyncMode })}
                      className="grid grid-cols-3 gap-2"
                    >
                      <ModeOption value="StructureOnly" checked={d.mode === "StructureOnly"} title="仅结构" desc="只建表/改表，不复制数据" />
                      <ModeOption value="StructureAndData" checked={d.mode === "StructureAndData"} title="结构 + 数据" desc="同步结构并复制数据" />
                      <ModeOption value="DataOnly" checked={d.mode === "DataOnly"} title="仅数据" desc="保持目标表结构，只复制数据" />
                    </RadioGroup>
                    <div className="mt-2.5 flex items-center gap-2">
                      <OptSwitch label="先对比再同步" checked={d.compareFirst} onChange={(v) => patch({ compareFirst: v })} />
                      <span className="text-[11px] text-muted-foreground">生成计划前先对比两端差异，审阅时先看对比结果</span>
                    </div>
                  </div>

                  {d.mode !== "StructureOnly" && (
                    <div className="rounded-lg border p-3">
                      <div className="mb-2 text-sm font-medium">数据选项</div>
                      <div className="flex flex-wrap items-center gap-3">
                        <Label className="text-xs">数据方式</Label>
                        <Select value={d.dataMethod} onValueChange={(v) => patch({ dataMethod: v as DataSyncMethod })}>
                          <SelectTrigger className="w-44" size="sm"><SelectValue /></SelectTrigger>
                          <SelectContent>
                            <SelectItem value="FullCopy">全量复制</SelectItem>
                            <SelectItem value="RowDiff">行级 Diff（分块）</SelectItem>
                          </SelectContent>
                        </Select>
                        <Label className="text-xs">冲突处理</Label>
                        <Select value={d.conflictMode} onValueChange={(v) => patch({ conflictMode: v as ConflictMode })}>
                          <SelectTrigger className="w-52" size="sm"><SelectValue /></SelectTrigger>
                          <SelectContent>
                            <SelectItem value="Error">冲突时报错</SelectItem>
                            <SelectItem value="Truncate">复制前清空目标表</SelectItem>
                            <SelectItem value="SkipExisting">跳过已存在（需主键）</SelectItem>
                          </SelectContent>
                        </Select>
                      </div>
                      <div className="mt-3 flex flex-wrap items-center gap-3">
                        {d.dataMethod === "RowDiff" && (
                          <OptSwitch label="删除目标多余行" checked={d.deleteExtraRows} onChange={(v) => patch({ deleteExtraRows: v })} />
                        )}
                        <OptSwitch label="对齐自增/序列" checked={d.alignAutoIncrement} onChange={(v) => patch({ alignAutoIncrement: v })} />
                        <Label className="text-xs">完成后校验</Label>
                        <Select value={d.verifyMode} onValueChange={(v) => patch({ verifyMode: v as VerifyMode })}>
                          <SelectTrigger className="w-36" size="sm"><SelectValue /></SelectTrigger>
                          <SelectContent>
                            <SelectItem value="Off">关闭</SelectItem>
                            <SelectItem value="Sample">行数 + 抽样比对</SelectItem>
                          </SelectContent>
                        </Select>
                      </div>
                    </div>
                  )}

                  {/* 高级选项：性能参数与扩展插件（默认折叠，偏离默认值时自动展开） */}
                  <div className="rounded-lg border p-3">
                    <button
                      type="button"
                      onClick={() => setShowAdvanced((v) => !v)}
                      className="flex items-center gap-1 text-xs text-muted-foreground transition-colors hover:text-foreground"
                    >
                      <ChevronDown className={`size-3.5 transition-transform duration-200 ${showAdvanced ? "" : "-rotate-90"}`} />
                      高级选项
                      {!showAdvanced && <span className="font-normal">（批次 / 分块 / 并行 / 索引后建 / 扩展插件）</span>}
                    </button>
                    <Collapse open={showAdvanced}>
                      <div className="mt-3 flex flex-wrap items-center gap-3">
                        <OptSwitch label="大表：索引后建" checked={d.deferIndexes} onChange={(v) => patch({ deferIndexes: v })} />
                        <OptSwitch label="同步源库扩展插件" checked={d.syncExtensions} onChange={(v) => patch({ syncExtensions: v })} />
                        <Label className="text-xs">批次</Label>
                        <Input className="h-8 w-20" inputMode="numeric" value={d.batchSize} onChange={(e) => patch({ batchSize: e.target.value.replace(/\D/g, "") })} />
                        <Label className="text-xs">分块</Label>
                        <Input className="h-8 w-20" inputMode="numeric" value={d.chunkRows} onChange={(e) => patch({ chunkRows: e.target.value.replace(/\D/g, "") })} />
                        <Label className="text-xs">并行表数</Label>
                        <Input className="h-8 w-16" inputMode="numeric" value={d.maxParallelTables} onChange={(e) => patch({ maxParallelTables: e.target.value.replace(/\D/g, "") })} />
                      </div>
                    </Collapse>
                  </div>

                  {step1Destructive && (
                    <Alert variant="warning">
                      <TriangleAlert />
                      <AlertDescription className="text-xs">
                        当前配置包含破坏性操作：
                        {d.conflictMode === "Truncate" && "复制前清空目标表"}
                        {d.conflictMode === "Truncate" && d.dropExtraTables && "、"}
                        {d.dropExtraTables && "删除目标端多余表"}
                        。生成计划后会在审阅步骤要求二次确认。
                      </AlertDescription>
                    </Alert>
                  )}
                </>
              )}

              {/* Step 2：审阅（对比结果 或 计划预览） */}
              {step === 2 && (
                <>
                  {d.compareFirst && !plan && compareTaskId && (
                    <CompareResultView taskId={compareTaskId} />
                  )}
                  {plan && (
                    <>
                      {/* 计划摘要：源 → 目标 / 表数 / 预计行数 / 破坏性动作数 */}
                      <div className="rounded-lg border p-3">
                        <div className="flex flex-wrap items-center gap-2 text-sm font-medium">
                          <GitCompareArrows className="size-4 text-muted-foreground" />
                          <span className="font-mono text-xs">
                            {connLabel(d.srcConn, d.srcDb)} → {connLabel(d.tgtConn, d.tgtDb)}
                          </span>
                          <Badge variant="secondary">{plan.tables.length} 张表</Badge>
                          {hasEstRows && <Badge variant="outline" className="text-[10px]">预计 ≈{estRowsTotal} 行</Badge>}
                          {planDestructive.length > 0 && (
                            <Badge variant="destructive" className="text-[10px]">破坏性 {planDestructive.length}</Badge>
                          )}
                          {plan.options.conflictMode === "Truncate" && (
                            <Badge variant="outline" className="text-[10px] text-amber-600 dark:text-amber-400">先清空目标表</Badge>
                          )}
                        </div>
                        <div className="mt-1 text-[11px] text-muted-foreground">
                          快照 {plan.id.slice(0, 8)} · 执行时使用此快照，不重新规划 · {plan.options.mode === "StructureOnly" ? "仅结构" : plan.options.mode === "DataOnly" ? "仅数据" : "结构 + 数据"}
                        </div>
                      </div>

                      {pendingDestructive.length > 0 && (
                        <Alert variant="warning">
                          <TriangleAlert />
                          <AlertTitle>{pendingDestructive.length} 个破坏性动作待确认</AlertTitle>
                          <AlertDescription>
                            <div className="text-xs">
                              包含 DROP / 清空等不可逆操作。未确认的破坏性动作执行时将被自动跳过；也可以在下方列表中取消勾选以显式跳过。
                            </div>
                            <label className="mt-1.5 flex items-center gap-2 text-xs font-medium text-amber-800 dark:text-amber-300">
                              <Switch checked={confirmDestructive} onCheckedChange={setConfirmDestructive} />
                              我已了解风险，确认执行破坏性动作
                            </label>
                          </AlertDescription>
                        </Alert>
                      )}

                      <PlanReview plan={plan} skipped={skipped} onToggle={(id, on) =>
                        setSkipped((s) => {
                          const n = new Set(s)
                          if (on) n.delete(id)
                          else n.add(id)
                          return n
                        })
                      } />
                      <div className="rounded-lg border p-3">
                        <OptSwitch
                          label={isEditing ? "保存修改到当前同步配置" : "保存为同步配置（Profile）"}
                          checked={saveProfile}
                          onChange={setSaveProfile}
                        />
                        <Collapse open={saveProfile}>
                          <div className="mt-2 space-y-3">
                            <Input
                              placeholder="Profile 名称"
                              value={profileName}
                              onChange={(e) => setProfileName(e.target.value)}
                            />
                            <CronField cron={cron} enabled={cronEnabled} onChange={(c, e) => { setCron(c); setCronEnabled(e) }} />
                          </div>
                        </Collapse>
                      </div>
                    </>
                  )}
                </>
              )}

              {/* Step 3：执行 */}
              {step === 3 && taskId && <TaskRunView taskId={taskId} onRunningChange={setTaskRunning} />}
            </motion.div>
          </div>

          {/* 固定底部操作栏：始终可见，不随内容滚动 */}
          <div className="flex shrink-0 items-center gap-3 border-t pt-3">
            <div className="min-w-0 flex-1 truncate text-xs text-muted-foreground">
              {step === 1 && <span>已选 {selectedTables.size} 张表{d.compareFirst && " · 将先对比差异"}</span>}
              {step === 2 && plan && destructiveBlocked && (
                <span className="text-amber-600 dark:text-amber-400">还有 {pendingDestructive.length} 个破坏性动作未确认</span>
              )}
              {step === 3 && <span>{taskRunning ? "任务在后台持续执行，关闭弹窗不会中断。" : "任务已结束，可关闭弹窗。"}</span>}
            </div>
            <div className="flex shrink-0 items-center gap-2">
              {step === 0 && (
                <>
                  <Button variant="ghost" onClick={requestClose}>取消</Button>
                  <Button disabled={!canNext0} onClick={() => go(1)}>下一步</Button>
                </>
              )}
              {step === 1 && (
                <>
                  <Button variant="outline" onClick={() => go(0)}>上一步</Button>
                  {d.compareFirst ? (
                    <Button onClick={runCompare} disabled={busy || selectedTables.size === 0}>
                      {busy ? <Loader2 className="animate-spin" /> : <GitCompareArrows />} 开始对比
                    </Button>
                  ) : (
                    <Button onClick={buildPlan} disabled={busy || selectedTables.size === 0}>
                      {busy ? <Loader2 className="animate-spin" /> : <Play />} 生成同步计划
                    </Button>
                  )}
                </>
              )}
              {step === 2 && d.compareFirst && !plan && compareTaskId && (
                <>
                  <Button variant="outline" onClick={() => go(1)}>上一步</Button>
                  <Button onClick={generatePlanFromCompare} disabled={busy}>
                    {busy ? <Loader2 className="animate-spin" /> : <ArrowRight />} 由对比结果生成同步计划
                  </Button>
                </>
              )}
              {step === 2 && plan && (
                <>
                  <Button variant="outline" onClick={() => go(1)}>上一步</Button>
                  <Button onClick={submit} disabled={busy || destructiveBlocked}>
                    {busy ? <Loader2 className="animate-spin" /> : <Play />}
                    {isEditing && saveProfile ? "保存并执行" : "提交执行"}
                  </Button>
                </>
              )}
              {step === 3 && <Button variant="outline" onClick={requestClose}>关闭</Button>}
            </div>
          </div>
        </DialogContent>
      </Dialog>

      {/* 任务运行中关闭弹窗：防止误以为关闭 = 取消 */}
      <AlertDialog open={confirmClose} onOpenChange={setConfirmClose}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>任务仍在运行，关闭弹窗？</AlertDialogTitle>
            <AlertDialogDescription>
              关闭弹窗不会取消或中断任务，可稍后在「同步中心 → 任务历史」查看进度与结果。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>继续等待</AlertDialogCancel>
            <AlertDialogAction onClick={() => { onOpenChange(false); reset() }}>后台运行并关闭</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}

// ------------------------------------------------------------------

function ConnDbPicker({
  title,
  connections,
  connId,
  db,
  onConn,
  onDb,
}: {
  title: string
  connections: { id: string; name: string; dialect: string }[] | undefined
  connId: string
  db: string
  onConn: (v: string) => void
  onDb: (v: string) => void
}) {
  const dbs = useQuery({
    queryKey: ["databases", connId],
    queryFn: () => api.listDatabases(connId),
    enabled: !!connId,
  })
  return (
    <div className="rounded-lg border p-3">
      <div className="mb-2 text-sm font-medium">{title}</div>
      <div className="space-y-2">
        <Select value={connId || undefined} onValueChange={onConn}>
          <SelectTrigger className="w-full"><SelectValue placeholder="选择连接" /></SelectTrigger>
          <SelectContent>
            {connections?.map((c) => (
              <SelectItem key={c.id} value={c.id}>{c.name}（{c.dialect}）</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={db || undefined} onValueChange={onDb}>
          <SelectTrigger className="w-full"><SelectValue placeholder={dbs.isLoading ? "加载中…" : "选择数据库"} /></SelectTrigger>
          <SelectContent>
            {dbs.data?.map((x) => (
              <SelectItem key={x.name} value={x.name}>{x.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
    </div>
  )
}

function ModeOption({ value, checked, title, desc }: { value: string; checked: boolean; title: string; desc: string }) {
  return (
    <label
      htmlFor={`mode-${value}`}
      className={`flex cursor-pointer items-start gap-2 rounded-md border p-2.5 font-normal transition-colors duration-200 ${
        checked ? "border-primary" : ""
      }`}
    >
      <RadioGroupItem value={value} id={`mode-${value}`} />
      <span>
        <span className="block text-sm">{title}</span>
        <span className="text-xs text-muted-foreground">{desc}</span>
      </span>
    </label>
  )
}

function TablePicker({
  tables,
  total,
  loading,
  error,
  selected,
  search,
  onSearch,
  onToggle,
  onSelectAll,
  onClear,
  headerExtra,
}: {
  /** 当前搜索过滤后的表 */
  tables: { name: string; kind: string }[]
  /** 库中全部表/视图数量 */
  total: number
  loading: boolean
  error?: string
  selected: Set<string>
  search: string
  onSearch: (v: string) => void
  onToggle: (t: string) => void
  onSelectAll: () => void
  onClear: () => void
  /** 标题行右侧扩展位（如"删除目标端多余表"这类范围级开关） */
  headerExtra?: ReactNode
}) {
  return (
    <div className="rounded-lg border p-3">
      <div className="mb-2 flex flex-wrap items-center gap-2 text-sm font-medium">
        选择表
        <Badge variant="secondary">{selected.size}</Badge>
        {!loading && !error && (
          <span className="text-xs font-normal text-muted-foreground">共 {total} 个对象</span>
        )}
        {headerExtra}
        <div className="relative ml-auto">
          <Search className="absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input className="h-8 w-44 pl-7" placeholder="搜索表…" value={search} onChange={(e) => onSearch(e.target.value)} />
        </div>
        <Button variant="ghost" size="sm" onClick={onSelectAll}>全选表</Button>
        <Button variant="ghost" size="sm" onClick={onClear}>清空</Button>
      </div>
      {loading ? (
        <div className="grid max-h-72 grid-cols-3 gap-1 overflow-auto rounded-md border p-2">
          {Array.from({ length: 9 }).map((_, i) => <Skeleton key={i} className="h-6" />)}
        </div>
      ) : error ? (
        <div className="text-sm text-destructive">{error}</div>
      ) : (
        <div className="grid max-h-72 grid-cols-2 gap-1 overflow-auto rounded-md border p-2 md:grid-cols-3">
          {tables.map((t) => (
            <label key={t.name} className={`flex items-center gap-2 rounded px-2 py-1 text-sm hover:bg-accent/50 ${t.kind !== "table" ? "opacity-60" : ""}`}>
              <Checkbox
                checked={selected.has(t.name)}
                onCheckedChange={() => onToggle(t.name)}
              />
              <span className="truncate">{t.name}</span>
              {t.kind === "view" && <span className="text-[10px] text-muted-foreground">视图</span>}
            </label>
          ))}
          {tables.length === 0 && (
            <div className="col-span-full py-6 text-center text-xs text-muted-foreground">
              {total === 0 ? "该数据库没有表" : "无匹配的表"}
            </div>
          )}
        </div>
      )}
      {!loading && !error && (
        <div className="mt-1.5 text-[11px] text-muted-foreground">灰显为视图：默认不同步，需要时单独勾选</div>
      )}
    </div>
  )
}

/** 计划审阅：每表一行的紧凑列表（默认折叠，展开看动作/SQL/警告），错误与缺依赖始终可见。 */
export function PlanReview({
  plan,
  skipped,
  onToggle,
}: {
  plan: ExecutionPlan
  skipped: Set<string>
  onToggle: (id: string, on: boolean) => void
}) {
  const [openTable, setOpenTable] = useState<string | null>(null)
  const [sqlOpen, setSqlOpen] = useState<string | null>(null)
  // 缺失扩展若已在计划中生成 InstallExtension 动作（置于首个依赖它的表下，跨表去重），展示为"将自动安装"而非错误
  const installSqls = new Set(
    plan.tables
      .flatMap((t) => [...t.structureActions, ...t.postCopyActions])
      .filter((a) => a.kind === "InstallExtension")
      .map((a) => a.sql),
  )
  const autoInstallable = (x: ExtensionDependency) => x.installSql !== "" && installSqls.has(x.installSql)
  return (
    <div className="overflow-hidden rounded-lg border">
      {plan.tables.map((tp, idx) => {
        const actions = [...tp.structureActions, ...tp.postCopyActions]
        const destructive = actions.filter((a) => a.isDestructive).length
        const warningCount =
          tp.conversionIssues.filter((i) => i.severity === "warning").length +
          actions.reduce((s, a) => s + a.warnings.length, 0)
        // 错误级问题不进折叠区：多表浏览时也必须一眼看到阻塞项
        const errors = [
          ...tp.conversionIssues.filter((i) => i.severity === "error").map((i) => i.message),
          ...tp.extensions
            .filter((x) => !x.present && !autoInstallable(x))
            .map((x) => `缺少依赖：${x.name}${x.note ? `（${x.note}）` : ""}`),
        ]
        const open = openTable === tp.target.table
        return (
          <div key={tp.target.table} className={idx > 0 ? "border-t" : ""}>
            <button
              type="button"
              onClick={() => setOpenTable(open ? null : tp.target.table)}
              className={`flex w-full items-center gap-2 px-2.5 py-2 text-left text-sm transition-colors duration-150 hover:bg-accent/40 ${
                open ? "bg-accent/30" : ""
              }`}
            >
              <ChevronDown className={`size-3.5 shrink-0 text-muted-foreground transition-transform duration-200 ${open ? "" : "-rotate-90"}`} />
              <span className="min-w-0 truncate font-medium">{tp.target.table}</span>
              {tp.targetTableExists ? <Badge variant="secondary" className="shrink-0 text-[10px]">已存在</Badge> : <Badge className="shrink-0 text-[10px]">新建</Badge>}
              {tp.data && (
                <Badge variant="outline" className="shrink-0 text-[10px]">
                  {tp.data.effectiveMethod === "RowDiff" ? "Diff" : "全量"} ≈{tp.data.estimatedRows ?? "?"} 行
                </Badge>
              )}
              {destructive > 0 && <Badge variant="destructive" className="shrink-0 text-[10px]">破坏性 {destructive}</Badge>}
              {warningCount > 0 && (
                <Badge variant="outline" className="shrink-0 text-[10px] text-amber-600 dark:text-amber-400">警告 {warningCount}</Badge>
              )}
              <span className="ml-auto shrink-0 pl-2 text-[11px] text-muted-foreground">{actions.length} 项变更</span>
            </button>
            {errors.map((e, k) => (
              <div key={k} className="flex items-start gap-1.5 px-2.5 pb-1.5 text-[11px] text-destructive">
                <CircleAlert className="mt-0.5 size-3 shrink-0" />
                <span className="min-w-0">{e}</span>
              </div>
            ))}
            <Collapse open={open}>
              <div className="space-y-1 px-2.5 pb-2.5">
                {tp.conversionIssues.filter((i) => i.severity === "warning").map((i, k) => (
                  <div key={k} className="flex items-start gap-1.5 text-[11px] text-amber-600 dark:text-amber-400">
                    <TriangleAlert className="mt-0.5 size-3 shrink-0" />
                    <span className="min-w-0">{i.message}</span>
                  </div>
                ))}
                {tp.conversionIssues.filter((i) => i.severity === "hint").map((i, k) => (
                  <div key={k} className="rounded border border-dashed px-2 py-1 text-[11px] text-muted-foreground">
                    ⓘ {i.message}
                  </div>
                ))}
                {tp.extensions.filter((x) => !x.present).map((x, k) => (
                  <div key={k} className="rounded border border-dashed px-2 py-1 text-[11px]">
                    {autoInstallable(x) ? (
                      <span className="text-muted-foreground">将自动安装扩展：{x.name}{x.note ? `（${x.note}）` : ""}</span>
                    ) : (
                      <span className="text-destructive">缺少依赖：{x.name}{x.note ? `（${x.note}）` : ""}（目标端不可安装，需人工处理）</span>
                    )}
                    {x.installSql && (
                      <pre className="mt-1 overflow-auto rounded bg-muted p-1.5 font-mono text-[10px] leading-4">{x.installSql}</pre>
                    )}
                  </div>
                ))}
                {actions.map((a) => {
                  const on = !skipped.has(a.id)
                  return (
                    <div key={a.id} className="rounded">
                      <label className="flex items-start gap-2 rounded px-1 py-1 text-xs hover:bg-accent/40">
                        <Checkbox
                          className="mt-0.5"
                          checked={on}
                          onCheckedChange={(v) => onToggle(a.id, !!v)}
                        />
                        <span className="min-w-0">
                          <Badge variant={a.isDestructive ? "destructive" : "secondary"} className="mr-1 text-[10px]">
                            {a.kind}
                          </Badge>
                          {a.summary}
                          {a.warnings.map((w, k) => (
                            <span key={k} className="ml-1 text-amber-600 dark:text-amber-400">⚠{w}</span>
                          ))}
                          <button
                            type="button"
                            className="ml-2 text-[10px] text-muted-foreground underline"
                            onClick={(e) => {
                              e.preventDefault()
                              setSqlOpen(sqlOpen === a.id ? null : a.id)
                            }}
                          >
                            {sqlOpen === a.id ? "收起 SQL" : "SQL"}
                          </button>
                        </span>
                      </label>
                      <Collapse open={sqlOpen === a.id}>
                        <pre className="mx-6 my-1 overflow-auto rounded bg-muted p-2 font-mono text-[11px] leading-5">{a.sql}</pre>
                      </Collapse>
                    </div>
                  )
                })}
                {actions.length === 0 && (
                  <div className="px-2 py-1 text-xs text-muted-foreground">结构一致，无需变更</div>
                )}
              </div>
            </Collapse>
          </div>
        )
      })}
    </div>
  )
}

function OptSwitch({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex items-center gap-1.5 text-xs">
      <Switch checked={checked} onCheckedChange={onChange} />
      {label}
    </label>
  )
}

export type { Draft }
