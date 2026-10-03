import { useEffect, useMemo, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { AnimatePresence, motion } from "motion/react"
import { ArrowRight, CircleAlert, GitCompareArrows, Loader2, Play, Search, TriangleAlert } from "lucide-react"
import { toast } from "sonner"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Skeleton } from "@/components/ui/skeleton"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"
import { api } from "@/lib/api"
import { useWorkspace } from "@/stores/workspace"
import { defaultSchema } from "@/components/layout/ConnectionTree"
import { Collapse, springSoft, stepTransition, stepVariants } from "@/components/ui/motion"
import { CronField } from "@/features/sync/CronField"
import { CompareResultView } from "@/features/sync/CompareViews"
import { TaskRunView } from "@/features/sync/TaskViews"
import type {
  ConflictMode,
  DataSyncMethod,
  ExecutionPlan,
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
  verifyMode: "Sample",
  batchSize: "1000",
  chunkRows: "2000",
  maxParallelTables: "2",
  compareFirst: false,
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
    ...(initial?.config
      ? {
          srcConn: initial.config.sourceConnectionId,
          srcDb: initial.config.sourceDatabase,
          tgtConn: initial.config.targetConnectionId,
          tgtDb: initial.config.targetDatabase,
          tgtSchema: initial.config.targetSchema ?? "",
          mode: initial.config.mode,
          dataMethod: initial.config.dataMethod,
          conflictMode: initial.config.conflictMode,
          deleteExtraRows: initial.config.deleteExtraRows,
          dropExtraTables: initial.config.dropExtraTables,
          alignAutoIncrement: initial.config.alignAutoIncrement,
          deferIndexes: initial.config.deferIndexes,
          verifyMode: initial.config.verifyMode,
          batchSize: String(initial.config.batchSize),
          chunkRows: String(initial.config.chunkRows),
          maxParallelTables: String(initial.config.maxParallelTables),
        }
      : {}),
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

  const patch = (p: Partial<Draft>) => setD((s) => ({ ...s, ...p }))

  const tabs = useWorkspace((s) => s.tabs)
  const connections = useQuery({ queryKey: ["connections"], queryFn: () => api.listConnections() })
  // 默认带出当前活动连接
  const connById = (id: string) => connections.data?.find((c) => c.id === id)

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
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  async function submit() {
    if (!plan) return
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
          onSaved?.()
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
  }

  const tablesFiltered = srcTables.data?.filter((t) => t.name.includes(search)) ?? []
  const sameDb = d.srcConn === d.tgtConn && d.srcDb === d.tgtDb && d.srcConn !== ""

  return (
    <Dialog open={open} onOpenChange={(v) => { onOpenChange(v); if (!v) reset() }}>
      <DialogContent className="max-h-[92vh] max-w-3xl overflow-hidden">
        <DialogHeader>
          <DialogTitle>{initial?.profileId ? "编辑同步配置" : "新建同步"}</DialogTitle>
        </DialogHeader>

        <div className="flex items-center gap-1 text-xs">
          {STEP_TITLES.map((t, i) => (
            <div key={t} className="flex items-center gap-1">
              <span
                className={`relative rounded-full px-2 py-0.5 transition-colors duration-200 ${
                  i === step
                    ? "text-primary-foreground"
                    : i < step
                      ? "bg-accent text-accent-foreground"
                      : "text-muted-foreground"
                }`}
              >
                {/* layoutId 胶囊：active 背景在步骤间平滑滑动 */}
                {i === step && (
                  <motion.span
                    layoutId="sync-step-pill"
                    className="absolute inset-0 rounded-full bg-primary"
                    transition={springSoft}
                  />
                )}
                <span className="relative z-10">
                  {i + 1}. {t}
                </span>
              </span>
              {i < STEP_TITLES.length - 1 && <ArrowRight className="size-3 text-muted-foreground" />}
            </div>
          ))}
        </div>

        <ScrollArea className="max-h-[62vh] pr-2">
          {/* mode="wait"：旧步骤先滑出，新步骤再滑入，方向由 go() 决定 */}
          <AnimatePresence mode="wait" custom={stepDir} initial={false}>
            <motion.div
              key={step}
              custom={stepDir}
              variants={stepVariants}
              initial="enter"
              animate="center"
              exit="exit"
              transition={stepTransition}
              className="space-y-4"
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
                    <CircleAlert /> <AlertTitle>源库与目标库相同</AlertTitle>
                  </Alert>
                )}
                <div className="flex justify-end">
                  <Button
                    disabled={!d.srcConn || !d.srcDb || !d.tgtConn || !d.tgtDb || sameDb}
                    onClick={() => go(1)}
                  >
                    下一步
                  </Button>
                </div>
              </>
            )}

            {/* Step 1：范围与选项 */}
            {step === 1 && (
              <>
                <div className="rounded-lg border p-3">
                  <div className="mb-2 text-sm font-medium">同步模式</div>
                  <RadioGroup
                    value={d.mode}
                    onValueChange={(v) => patch({ mode: v as SyncMode })}
                    className="grid grid-cols-2 gap-2"
                  >
                    <ModeOption value="StructureOnly" checked={d.mode === "StructureOnly"} title="仅结构" desc="类型映射 + 函数翻译" />
                    <ModeOption value="StructureAndData" checked={d.mode === "StructureAndData"} title="结构 + 数据" desc="同步结构并复制数据" />
                  </RadioGroup>

                  {d.mode === "StructureAndData" && (
                    <div className="mt-3 flex animate-in flex-wrap items-center gap-3 fade-in slide-in-from-top-1 duration-200">
                      <Label className="text-xs">数据方式</Label>
                      <Select value={d.dataMethod} onValueChange={(v) => patch({ dataMethod: v as DataSyncMethod })}>
                        <SelectTrigger className="w-44" size="sm"><SelectValue /></SelectTrigger>
                        <SelectContent>
                          <SelectItem value="FullCopy">全量复制</SelectItem>
                          <SelectItem value="RowDiff">行级 Diff（分块）</SelectItem>
                        </SelectContent>
                      </Select>
                      <Select value={d.conflictMode} onValueChange={(v) => patch({ conflictMode: v as ConflictMode })}>
                        <SelectTrigger className="w-52" size="sm"><SelectValue /></SelectTrigger>
                        <SelectContent>
                          <SelectItem value="Error">冲突时报错</SelectItem>
                          <SelectItem value="Truncate">复制前清空目标表</SelectItem>
                          <SelectItem value="SkipExisting">跳过已存在（需主键）</SelectItem>
                        </SelectContent>
                      </Select>
                      {d.dataMethod === "RowDiff" && (
                        <OptSwitch label="删除目标多余行" checked={d.deleteExtraRows} onChange={(v) => patch({ deleteExtraRows: v })} />
                      )}
                      <OptSwitch label="对齐自增/序列" checked={d.alignAutoIncrement} onChange={(v) => patch({ alignAutoIncrement: v })} />
                      <OptSwitch label="大表：索引后建" checked={d.deferIndexes} onChange={(v) => patch({ deferIndexes: v })} />
                    </div>
                  )}
                  <div className="mt-3 flex flex-wrap items-center gap-3">
                    <OptSwitch label="删除目标端多余表" checked={d.dropExtraTables} onChange={(v) => patch({ dropExtraTables: v })} />
                    <Label className="text-xs">校验</Label>
                    <Select value={d.verifyMode} onValueChange={(v) => patch({ verifyMode: v as VerifyMode })}>
                      <SelectTrigger className="w-36" size="sm"><SelectValue /></SelectTrigger>
                      <SelectContent>
                        <SelectItem value="Off">关闭</SelectItem>
                        <SelectItem value="Sample">行数+抽样</SelectItem>
                      </SelectContent>
                    </Select>
                    <Label className="text-xs">批次</Label>
                    <Input className="h-8 w-20" value={d.batchSize} onChange={(e) => patch({ batchSize: e.target.value.replace(/\D/g, "") })} />
                    <Label className="text-xs">分块</Label>
                    <Input className="h-8 w-20" value={d.chunkRows} onChange={(e) => patch({ chunkRows: e.target.value.replace(/\D/g, "") })} />
                    <Label className="text-xs">并行表数</Label>
                    <Input className="h-8 w-16" value={d.maxParallelTables} onChange={(e) => patch({ maxParallelTables: e.target.value.replace(/\D/g, "") })} />
                  </div>
                </div>

                <TablePicker
                  tables={tablesFiltered}
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
                />

                <div className="flex items-center justify-between">
                  <Button variant="outline" onClick={() => go(0)}>上一步</Button>
                  <div className="flex items-center gap-2">
                    <OptSwitch label="先对比再同步" checked={d.compareFirst} onChange={(v) => patch({ compareFirst: v })} />
                    {d.compareFirst ? (
                      <Button onClick={runCompare} disabled={busy || selectedTables.size === 0}>
                        {busy ? <Loader2 className="animate-spin" /> : <GitCompareArrows />} 开始对比
                      </Button>
                    ) : (
                      <Button onClick={buildPlan} disabled={busy || selectedTables.size === 0}>
                        {busy ? <Loader2 className="animate-spin" /> : <Play />} 生成同步计划
                      </Button>
                    )}
                  </div>
                </div>
              </>
            )}

            {/* Step 2：审阅（对比结果 或 计划预览） */}
            {step === 2 && (
              <>
                {d.compareFirst && !plan && compareTaskId && (
                  <>
                    <CompareResultView taskId={compareTaskId} onOpenTable={() => {}} />
                    <div className="flex items-center justify-between">
                      <Button variant="outline" onClick={() => go(1)}>上一步</Button>
                      <Button onClick={generatePlanFromCompare} disabled={busy}>
                        {busy ? <Loader2 className="animate-spin" /> : <ArrowRight />} 由对比结果生成同步计划
                      </Button>
                    </div>
                  </>
                )}
                {plan && (
                  <>
                    <Alert>
                      <AlertTitle>
                        {plan.tables.length} 张表 · 快照 {plan.id.slice(0, 8)}（执行时使用此快照，不重新规划）
                      </AlertTitle>
                    </Alert>
                    <PlanReview plan={plan} skipped={skipped} onToggle={(id, on) =>
                      setSkipped((s) => {
                        const n = new Set(s)
                        if (on) n.delete(id)
                        else n.add(id)
                        return n
                      })
                    } />
                    <div className="rounded-lg border p-3">
                      <OptSwitch label="保存为同步配置（Profile）" checked={saveProfile} onChange={setSaveProfile} />
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
                    <div className="flex items-center justify-between">
                      <Button variant="outline" onClick={() => go(1)}>上一步</Button>
                      <div className="flex items-center gap-3">
                        <OptSwitch label="确认执行破坏性动作（DROP 等）" checked={confirmDestructive} onChange={setConfirmDestructive} />
                        <Button onClick={submit} disabled={busy}>
                          {busy ? <Loader2 className="animate-spin" /> : <Play />} 提交执行
                        </Button>
                      </div>
                    </div>
                  </>
                )}
              </>
            )}

            {/* Step 3：执行 */}
            {step === 3 && taskId && <TaskRunView taskId={taskId} />}
            {step === 2 && d.compareFirst && compareTaskId && !plan && null}
            </motion.div>
          </AnimatePresence>
        </ScrollArea>
      </DialogContent>
    </Dialog>
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
  loading,
  error,
  selected,
  search,
  onSearch,
  onToggle,
  onSelectAll,
  onClear,
}: {
  tables: { name: string; kind: string }[]
  loading: boolean
  error?: string
  selected: Set<string>
  search: string
  onSearch: (v: string) => void
  onToggle: (t: string) => void
  onSelectAll: () => void
  onClear: () => void
}) {
  return (
    <div className="rounded-lg border p-3">
      <div className="mb-2 flex items-center gap-2 text-sm font-medium">
        选择表
        <Badge variant="secondary">{selected.size}</Badge>
        <div className="relative ml-auto">
          <Search className="absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input className="h-8 w-44 pl-7" placeholder="搜索表…" value={search} onChange={(e) => onSearch(e.target.value)} />
        </div>
        <Button variant="ghost" size="sm" onClick={onSelectAll}>全选</Button>
        <Button variant="ghost" size="sm" onClick={onClear}>清空</Button>
      </div>
      {loading ? (
        <div className="grid max-h-56 grid-cols-3 gap-1 overflow-auto rounded-md border p-2">
          {Array.from({ length: 9 }).map((_, i) => <Skeleton key={i} className="h-6" />)}
        </div>
      ) : error ? (
        <div className="text-sm text-destructive">{error}</div>
      ) : (
        <div className="grid max-h-56 grid-cols-2 gap-1 overflow-auto rounded-md border p-2 md:grid-cols-3">
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
        </div>
      )}
    </div>
  )
}

/** 计划审阅：按表分组的动作树（SQL 可展开）。 */
export function PlanReview({
  plan,
  skipped,
  onToggle,
}: {
  plan: ExecutionPlan
  skipped: Set<string>
  onToggle: (id: string, on: boolean) => void
}) {
  const [expanded, setExpanded] = useState<string | null>(null)
  return (
    <div className="space-y-2">
      {plan.tables.map((tp) => (
        <div key={tp.target.table} className="rounded-lg border p-3">
          <div className="mb-1 flex items-center gap-2 text-sm font-medium">
            {tp.target.table}
            {tp.targetTableExists ? <Badge variant="secondary" className="text-[10px]">已存在（Diff）</Badge> : <Badge className="text-[10px]">新建</Badge>}
            {tp.data && (
              <Badge variant="outline" className="text-[10px]">
                {tp.data.effectiveMethod === "RowDiff" ? "Diff" : "全量"} ≈{tp.data.estimatedRows ?? "?"} 行
                {tp.data.conflictMode !== "Error" && ` · ${tp.data.conflictMode === "Truncate" ? "先清空" : "跳过已存在"}`}
                {!tp.data.hasPrimaryKey && " · 无主键"}
              </Badge>
            )}
            {tp.postCopyActions.length > 0 && (
              <Badge variant="outline" className="text-[10px]">索引后建 {tp.postCopyActions.length}</Badge>
            )}
          </div>
          {tp.conversionIssues.filter((i) => i.severity === "error").map((i, k) => (
            <Alert key={k} variant="destructive" className="mb-1">
              <CircleAlert />
              <AlertDescription className="text-xs">{i.message}</AlertDescription>
            </Alert>
          ))}
          {tp.conversionIssues.filter((i) => i.severity === "warning").map((i, k) => (
            <Alert key={k} variant="warning" className="mb-1">
              <TriangleAlert />
              <AlertDescription className="text-xs">{i.message}</AlertDescription>
            </Alert>
          ))}
          {tp.conversionIssues.filter((i) => i.severity === "hint").map((i, k) => (
            <div key={k} className="rounded border border-dashed px-2 py-1 text-[11px] text-muted-foreground">
              ⓘ {i.message}
            </div>
          ))}
          {tp.extensions.filter((x) => !x.present).map((x, k) => (
            <Alert key={k} variant="destructive" className="mb-1">
              <CircleAlert />
              <AlertTitle className="text-xs">缺少依赖：{x.name}{x.note ? `（${x.note}）` : ""}</AlertTitle>
              {x.installSql && <AlertDescription className="font-mono text-xs">{x.installSql}</AlertDescription>}
            </Alert>
          ))}
          <div className="space-y-1">
            {[...tp.structureActions, ...tp.postCopyActions].map((a) => {
              const on = !skipped.has(a.id)
              return (
                <div key={a.id} className="rounded px-1">
                  <label className="flex items-start gap-2 rounded px-1 py-1 text-xs hover:bg-accent/40">
                    <Checkbox
                      className="mt-0.5"
                      checked={on}
                      onCheckedChange={(v) => onToggle(a.id, !!v)}
                    />
                    <span>
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
                        onClick={() => setExpanded(expanded === a.id ? null : a.id)}
                      >
                        {expanded === a.id ? "收起 SQL" : "SQL"}
                      </button>
                    </span>
                  </label>
                  <Collapse open={expanded === a.id}>
                    <pre className="mx-6 my-1 overflow-auto rounded bg-muted p-2 font-mono text-[11px] leading-5">{a.sql}</pre>
                  </Collapse>
                </div>
              )
            })}
            {tp.structureActions.length + tp.postCopyActions.length === 0 && (
              <div className="px-2 py-1 text-xs text-muted-foreground">结构一致，无需变更</div>
            )}
          </div>
        </div>
      ))}
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
