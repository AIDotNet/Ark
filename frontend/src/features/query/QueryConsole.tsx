import { useCallback, useEffect, useRef, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { useTheme } from "next-themes"
import {
  AlertTriangle,
  Braces,
  Check,
  ChevronDown,
  Database,
  Download,
  FileJson,
  FileText,
  Layers,
  Loader2,
  Network,
  Play,
  Square,
  Star,
  WandSparkles,
} from "lucide-react"
import { toast } from "sonner"
import { basicSetup, EditorView } from "codemirror"
import { sql } from "@codemirror/lang-sql"
import { Compartment, Prec, StateEffect, StateField } from "@codemirror/state"
import { Decoration, keymap } from "@codemirror/view"
import type { DecorationSet } from "@codemirror/view"
import { format as formatSql, type SqlLanguage } from "sql-formatter"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Kbd, KbdGroup } from "@/components/ui/kbd"
import { Resizable, ResizableHandle, ResizablePanel } from "@/components/ui/resizable"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Separator } from "@/components/ui/separator"
import { api, ArkApiError } from "@/lib/api"
import { cmTheme } from "@/features/query/editor-theme"
import { ResultsGrid } from "@/features/query/ResultsGrid"
import { AiMenu } from "@/features/query/AiMenu"
import { QueryHistoryPanel, SaveQueryDialog } from "@/features/query/QueryHistoryPanel"
import { statementAt } from "@/lib/sql-statements"
import { useQueryHistory } from "@/stores/query-history"
import { useQuerySessions } from "@/stores/query-session"
import { useWorkspace } from "@/stores/workspace"
import type { Dialect, QueryResultSet } from "@/types/api"
import { cn } from "cn"

const MAX_STORED_SQL = 20000

const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.userAgent)
const MOD_KEY = isMac ? "⌘" : "Ctrl"

// ------------------------- 语句高亮（DataGrip 式当前语句背景） -------------------------

const setCurrentStmt = StateEffect.define<{ from: number; to: number } | null>()

const statementField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(deco, tr) {
    deco = deco.map(tr.changes)
    for (const e of tr.effects) {
      deco =
        e.value && e.value.from < e.value.to
          ? Decoration.set([Decoration.mark({ class: "cm-current-stmt" }).range(e.value.from, e.value.to)])
          : Decoration.none
    }
    return deco
  },
  provide: (f) => EditorView.decorations.from(f),
})

// ------------------------- 导出辅助 -------------------------

function csvEscape(v: unknown): string {
  const s = v === null || v === undefined ? "" : typeof v === "object" ? JSON.stringify(v) : String(v)
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
}

function sqlLiteral(v: unknown): string {
  if (v === null || v === undefined) return "NULL"
  if (typeof v === "number") return Number.isFinite(v) ? String(v) : "NULL"
  if (typeof v === "boolean") return v ? "TRUE" : "FALSE"
  if (typeof v === "bigint") return String(v)
  const s = typeof v === "object" ? JSON.stringify(v) : String(v)
  return `'${s.replace(/'/g, "''")}'`
}

function downloadText(content: string, filename: string, mime = "text/plain;charset=utf-8") {
  const blob = new Blob([content], { type: mime })
  const url = URL.createObjectURL(blob)
  const a = document.createElement("a")
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}

function exportResultSet(rs: QueryResultSet, kind: "csv" | "json" | "md" | "insert") {
  const names = rs.columns.map((c) => c.name)
  if (kind === "csv") {
    const lines = [names.map(csvEscape).join(",")]
    for (const row of rs.rows) lines.push(row.map(csvEscape).join(","))
    downloadText("\uFEFF" + lines.join("\r\n"), "result.csv", "text/csv;charset=utf-8")
  } else if (kind === "json") {
    const data = rs.rows.map((row) => Object.fromEntries(names.map((n, i) => [n, row[i]])))
    downloadText(JSON.stringify(data, null, 2), "result.json", "application/json;charset=utf-8")
  } else if (kind === "md") {
    const lines = [
      `| ${names.join(" | ")} |`,
      `| ${names.map(() => "---").join(" | ")} |`,
      ...rs.rows.map(
        (row) => `| ${row.map((v) => (v === null || v === undefined ? "NULL" : String(v))).join(" | ")} |`,
      ),
    ]
    downloadText(lines.join("\n"), "result.md", "text/markdown;charset=utf-8")
  } else {
    const stmts = rs.rows.map((row) => {
      const cols = names.map((n) => `"${n}"`).join(", ")
      const vals = row.map(sqlLiteral).join(", ")
      return `INSERT INTO table (${cols}) VALUES (${vals});`
    })
    downloadText(stmts.join("\n"), "result_insert.sql")
  }
}

// ------------------------- 查询控制台 -------------------------

export function QueryConsole({
  tabKey,
  connectionId,
  connectionName,
  initialDatabase,
  initialSchema,
}: {
  tabKey: string
  connectionId: string
  connectionName?: string
  initialDatabase: string
  initialSchema: string
}) {
  const hostRef = useRef<HTMLDivElement>(null)
  const viewRef = useRef<EditorView | null>(null)
  const langCompartment = useRef(new Compartment())
  const themeCompartment = useRef(new Compartment())
  const abortRef = useRef<AbortController | null>(null)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const lastStmtRef = useRef("")

  const session = useQuerySessions((s) => s.sessions[tabKey])
  const [database, setDatabase] = useState(session?.database ?? initialDatabase)
  const [schema, setSchema] = useState(session?.schema ?? initialSchema)
  const [maxRows, setMaxRows] = useState("1000")
  const [timeoutSeconds, setTimeoutSeconds] = useState("30")
  const [running, setRunning] = useState(false)
  const [elapsed, setElapsed] = useState<number | null>(null)
  const [result, setResult] = useState<{
    resultSets: QueryResultSet[]
    messages: string[]
    elapsedMs: number
  } | null>(null)
  const [error, setError] = useState<{ message: string; code?: number; cancelled?: boolean } | null>(null)
  const [plan, setPlan] = useState<string | null>(null)
  const [activeRs, setActiveRs] = useState(0)
  const [resultTab, setResultTab] = useState("")
  const [saveOpen, setSaveOpen] = useState(false)
  // 每次成功执行自增，作为结果区 key 驱动入场动画
  const [runSeq, setRunSeq] = useState(0)
  const { resolvedTheme } = useTheme()

  const { add: addHistory } = useQueryHistory()
  const patchTab = useWorkspace((s) => s.patchTab)

  const dbs = useQuery({ queryKey: ["databases", connectionId], queryFn: () => api.listDatabases(connectionId) })
  const schemas = useQuery({
    queryKey: ["schemas", connectionId, database],
    queryFn: () => api.listSchemas(connectionId, database),
  })
  const connections = useQuery({ queryKey: ["connections"], queryFn: api.listConnections })
  const dialect: Dialect = connections.data?.find((c) => c.id === connectionId)?.dialect ?? "MySQL"
  const dialectKey: SqlLanguage =
    dialect === "PostgreSQL" ? "postgresql" : dialect === "MySQL" ? "mysql" : "sqlite"
  const multiSchema = (schemas.data?.length ?? 0) > 1

  // 补全元数据（表 + 列，切库/schema 时刷新，长缓存）
  const completion = useQuery({
    queryKey: ["completion", connectionId, database, schema],
    queryFn: () => api.getCompletionSchema(connectionId, database, schema),
    staleTime: 5 * 60_000,
  })

  const tabQueryNo = tabKey.split(":").at(-1) ?? ""

  function persistSession() {
    const view = viewRef.current
    if (!view) return
    useQuerySessions.getState().save(tabKey, {
      sql: view.state.doc.toString(),
      database,
      schema,
    })
  }

  /** 切库后若当前 schema 不在新库的 schema 列表中，回落到第一个 */
  useEffect(() => {
    if (!schemas.data || schemas.data.length === 0) return
    if (!schemas.data.some((s) => s.name === schema)) setSchema(schemas.data[0]!.name)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [schemas.data])

  function changeDatabase(db: string) {
    setDatabase(db)
    patchTab(tabKey, { database: db, title: `查询${tabQueryNo} · ${db}` })
  }

  function changeSchema(sc: string) {
    setSchema(sc)
    patchTab(tabKey, { schema: sc })
  }

  /** 取当前应执行的 SQL：有选区→选区；否则光标所在语句；空文档→整篇（触发空提示） */
  function currentStatement(): string {
    const view = viewRef.current
    if (!view) return ""
    const doc = view.state.doc.toString()
    const sel = view.state.selection.main
    if (!sel.empty) return doc.slice(sel.from, sel.to)
    const stmt = statementAt(doc, sel.head)
    return stmt ? doc.slice(stmt.from, stmt.to) : doc
  }

  function insertIntoEditor(text: string, replaceStatement = false) {
    const view = viewRef.current
    if (!view) return
    if (replaceStatement) {
      const doc = view.state.doc.toString()
      const sel = view.state.selection.main
      const stmt = statementAt(doc, sel.head) ?? { from: 0, to: doc.length }
      view.dispatch({
        changes: { from: stmt.from, to: stmt.to, insert: text },
        selection: { anchor: stmt.from + text.length },
        scrollIntoView: true,
      })
    } else {
      const sel = view.state.selection.main
      view.dispatch({
        changes: { from: sel.from, to: sel.to, insert: text },
        selection: { anchor: sel.from + text.length },
        scrollIntoView: true,
      })
    }
    view.focus()
  }

  /** 历史面板回填（run=false）/ 双击执行（run=true） */
  function pickSql(sqlText: string, run: boolean) {
    const view = viewRef.current
    if (!view) return
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: sqlText },
      selection: { anchor: sqlText.length },
      scrollIntoView: true,
    })
    view.focus()
    if (run) void execute(sqlText)
  }

  async function execute(sqlText: string) {
    const text = sqlText.trim()
    if (!text) return
    abortRef.current?.abort()
    const ctrl = new AbortController()
    abortRef.current = ctrl
    setError(null)
    setPlan(null)
    setElapsed(0)
    setRunning(true)
    const startedAt = performance.now()
    if (timerRef.current) clearInterval(timerRef.current)
    timerRef.current = setInterval(() => setElapsed(performance.now() - startedAt), 100)
    try {
      const res = await api.executeSql(
        connectionId,
        {
          database,
          schema,
          sql: text,
          maxRows: Number(maxRows) || 1000,
          timeoutSeconds: Number(timeoutSeconds) || 30,
        },
        ctrl.signal,
      )
      setResult(res)
      setActiveRs(0)
      setRunSeq((s) => s + 1)
      const rowCount = res.resultSets.reduce((s, rs) => s + rs.rows.length, 0)
      setResultTab(res.resultSets.length > 0 ? "rs-0" : "messages")
      addHistory({
        connectionId,
        database,
        sql: text.slice(0, MAX_STORED_SQL),
        ok: true,
        durationMs: res.elapsedMs,
        rowCount,
      })
    } catch (e) {
      if (ctrl.signal.aborted) {
        setError({ message: "查询已取消", cancelled: true })
      } else {
        const err = e as ArkApiError
        setError({ message: err.message, code: err.code })
        toast.error(err.message)
        addHistory({ connectionId, database, sql: text.slice(0, MAX_STORED_SQL), ok: false })
      }
    } finally {
      if (timerRef.current) clearInterval(timerRef.current)
      timerRef.current = null
      setRunning(false)
      abortRef.current = null
      if (!ctrl.signal.aborted) setElapsed(null)
    }
  }

  function run(mode: "auto" | "script" = "auto") {
    const view = viewRef.current
    if (!view) return
    const text = mode === "script" ? view.state.doc.toString() : currentStatement()
    void execute(text)
  }

  async function explain() {
    const text = currentStatement().trim()
    if (!text) return
    setError(null)
    try {
      const res = await api.explainSql(connectionId, {
        database,
        schema,
        sql: text,
        timeoutSeconds: Number(timeoutSeconds) || 30,
      })
      setPlan(res.planText)
      setResultTab("plan")
    } catch (e) {
      const err = e as ArkApiError
      setError({ message: err.message, code: err.code })
    }
  }

  async function formatCurrent() {
    const view = viewRef.current
    if (!view) return
    const doc = view.state.doc.toString()
    const sel = view.state.selection.main
    try {
      if (sel.empty) {
        if (!doc.trim()) return
        const formatted = await formatSql(doc, { language: dialectKey, tabWidth: 2 })
        view.dispatch({ changes: { from: 0, to: doc.length, insert: formatted } })
      } else {
        const formatted = await formatSql(doc.slice(sel.from, sel.to), { language: dialectKey, tabWidth: 2 })
        view.dispatch({ changes: { from: sel.from, to: sel.to, insert: formatted } })
      }
      view.focus()
    } catch (e) {
      toast.error(`格式化失败：${(e as Error).message}`)
    }
  }

  const cancel = useCallback(() => abortRef.current?.abort(), [])

  // ------------------------- 编辑器初始化 -------------------------

  useEffect(() => {
    if (!hostRef.current || viewRef.current) return
    const view = new EditorView({
      parent: hostRef.current,
      extensions: [
        basicSetup,
        statementField,
        langCompartment.current.of(sql({ schema: {} })),
        themeCompartment.current.of(cmTheme(resolvedTheme === "dark")),
        EditorView.lineWrapping,
        Prec.highest(
          keymap.of([
            {
              key: "Mod-Enter",
              run: () => {
                run()
                return true
              },
            },
            {
              key: "Mod-Shift-Enter",
              run: () => {
                run("script")
                return true
              },
            },
            {
              key: "Shift-Alt-f",
              run: () => {
                void formatCurrent()
                return true
              },
            },
          ]),
        ),
        EditorView.updateListener.of((u) => {
          if (u.docChanged) {
            if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
            saveTimerRef.current = setTimeout(persistSession, 400)
          }
          if (u.selectionSet || u.docChanged) {
            const { head } = u.state.selection.main
            const stmt = statementAt(u.state.doc.toString(), head)
            const key = stmt ? `${stmt.from}-${stmt.to}` : ""
            if (key !== lastStmtRef.current) {
              lastStmtRef.current = key
              const v = u.view
              setTimeout(() => v.dispatch({ effects: setCurrentStmt.of(stmt) }), 0)
            }
          }
        }),
      ],
      doc: session?.sql ?? "",
    })
    viewRef.current = view
    return () => {
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
      view.destroy()
      viewRef.current = null
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 主题切换
  useEffect(() => {
    viewRef.current?.dispatch({
      effects: themeCompartment.current.reconfigure(cmTheme(resolvedTheme === "dark")),
    })
  }, [resolvedTheme])

  // 补全 schema 更新
  useEffect(() => {
    const view = viewRef.current
    if (!view) return
    const schemaObj = Object.fromEntries(
      (completion.data?.tables ?? []).map((t) => [t.name, t.columns.map((c) => c.name)]),
    )
    view.dispatch({ effects: langCompartment.current.reconfigure(sql({ schema: schemaObj })) })
  }, [completion.data])

  // 库/schema 变化 → 持久化会话
  useEffect(() => {
    persistSession()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [database, schema])

  const totalRows = result?.resultSets.reduce((s, rs) => s + rs.rows.length, 0) ?? 0
  const currentRs = result?.resultSets[activeRs] ?? null
  const hasDoc = (viewRef.current?.state.doc.length ?? 0) > 0
  const truncated = result?.messages.some((m) => m.includes("截断")) ?? false

  const status = running
    ? `运行中… ${((elapsed ?? 0) / 1000).toFixed(1)} s`
    : result
      ? `${result.elapsedMs.toFixed(1)} ms · ${result.resultSets.length} 个结果集 · ${totalRows} 行`
      : ""

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* 工具栏 */}
      <div className="flex flex-wrap items-center gap-1.5 border-b bg-muted/30 px-2 py-1.5">
        {/* 执行（拆分按钮：主钮=选中/光标语句，下拉=整个脚本） */}
        <div className="flex items-center">
          <Button size="sm" className="gap-1.5 rounded-r-none pr-2" onClick={() => run()} disabled={running}>
            {running ? <Loader2 className="animate-spin" /> : <Play />} 执行
            <KbdGroup className="ml-0.5">
              <Kbd>{MOD_KEY}</Kbd>
              <Kbd>↩</Kbd>
            </KbdGroup>
          </Button>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                size="sm"
                className="rounded-l-none border-l border-primary-foreground/25 px-1.5"
                disabled={running}
              >
                <ChevronDown className="size-3.5" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start">
              <DropdownMenuItem onClick={() => run()}>
                <Play /> 执行选中 / 光标语句
              </DropdownMenuItem>
              <DropdownMenuItem onClick={() => run("script")}>
                <Play /> 执行整个脚本
                <span className="ml-auto pl-4 text-[10px] text-muted-foreground">{MOD_KEY}+⇧+↩</span>
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
        {running && (
          <Button size="sm" variant="outline" className="animate-in fade-in zoom-in-95 duration-150" onClick={cancel}>
            <Square /> 取消
          </Button>
        )}

        <Separator orientation="vertical" className="mx-1 h-5" />

        <Button size="sm" variant="ghost" onClick={() => void formatCurrent()} title="格式化（⇧Alt+F）">
          <WandSparkles /> 格式化
        </Button>
        <Button size="sm" variant="ghost" onClick={() => void explain()} title="生成执行计划">
          <Network /> 计划
        </Button>

        <AiMenu
          connectionId={connectionId}
          database={database}
          schema={schema}
          getSql={currentStatement}
          lastError={error && !error.cancelled ? error.message : null}
          onInsert={(sqlText) => insertIntoEditor(sqlText)}
          onFixApply={(sqlText) => insertIntoEditor(sqlText, true)}
        />

        <Separator orientation="vertical" className="mx-1 h-5" />

        {/* 库 / schema / 行上限 / 超时 */}
        <Select value={database} onValueChange={changeDatabase}>
          <SelectTrigger className="w-44" size="sm">
            <Database className="size-3.5 shrink-0 text-muted-foreground" />
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {dbs.data?.map((d) => (
              <SelectItem key={d.name} value={d.name}>
                {d.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {multiSchema && (
          <Select value={schema} onValueChange={changeSchema}>
            <SelectTrigger className="w-36" size="sm">
              <Layers className="size-3.5 shrink-0 text-muted-foreground" />
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {schemas.data?.map((s) => (
                <SelectItem key={s.name} value={s.name}>
                  {s.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        <Select value={maxRows} onValueChange={setMaxRows}>
          <SelectTrigger className="w-24" size="sm" title="结果行上限">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {["100", "500", "1000", "5000", "10000"].map((n) => (
              <SelectItem key={n} value={n}>
                {n} 行
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={timeoutSeconds} onValueChange={setTimeoutSeconds}>
          <SelectTrigger className="w-20" size="sm" title="执行超时">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {["10", "30", "60", "300"].map((n) => (
              <SelectItem key={n} value={n}>
                {n} 秒
              </SelectItem>
            ))}
          </SelectContent>
        </Select>

        <Separator orientation="vertical" className="mx-1 h-5" />

        <Button size="sm" variant="ghost" onClick={() => setSaveOpen(true)} disabled={!hasDoc}>
          <Star /> 保存
        </Button>
        <QueryHistoryPanel connectionId={connectionId} onPick={pickSql} />

        {/* 右侧运行状态 */}
        <div className="ml-auto flex items-center gap-1.5 pr-1 text-xs text-muted-foreground">
          {running ? (
            <>
              <span className="animate-pulse-dot size-2 shrink-0 rounded-full bg-primary" />
              <Loader2 className="size-3.5 animate-spin text-primary" />
            </>
          ) : null}
          {status && <span>{status}</span>}
          {connectionName && !status && <span>{connectionName}</span>}
        </div>
      </div>

      {/* 运行中：工具栏下方不定进度扫条 */}
      {running && (
        <div className="relative h-0.5 shrink-0 overflow-hidden bg-primary/15" role="progressbar" aria-label="查询执行中">
          <div className="animate-indeterminate h-full w-1/3 rounded-full bg-primary" />
        </div>
      )}

      {/* 编辑器 / 结果：可拖拽分割 */}
      <Resizable direction="vertical" className="min-h-0 flex-1">
        <ResizablePanel defaultSize={38} minSize={10}>
          <div ref={hostRef} className="h-full overflow-hidden border-b [&_.cm-editor]:h-full" />
        </ResizablePanel>
        <ResizableHandle />
        <ResizablePanel defaultSize={62} minSize={20}>
          <div className="flex h-full min-h-0 flex-col">
            {error && (
              <Alert
                variant={error.cancelled ? "default" : "destructive"}
                className="animate-slide-down gap-2 rounded-none border-x-0 border-t-0 px-3 py-2"
              >
                <AlertTriangle className="size-4" />
                <AlertTitle className="text-xs">
                  {error.cancelled ? "已取消" : `执行失败${error.code ? `（错误码 ${error.code}）` : ""}`}
                </AlertTitle>
                <AlertDescription className="max-h-28 overflow-y-auto whitespace-pre-wrap font-mono text-xs">
                  {error.message}
                </AlertDescription>
              </Alert>
            )}

            {result ? (
              <div
                key={runSeq}
                className="flex min-h-0 flex-1 animate-in flex-col fade-in slide-in-from-bottom-2 duration-300"
              >
                {/* 结果区标签条 */}
                <div className="flex shrink-0 items-center gap-0.5 border-b px-2">
                  {result.resultSets.map((rs, i) => (
                    <ResultTabButton
                      key={i}
                      active={resultTab === `rs-${i}`}
                      onClick={() => {
                        setActiveRs(i)
                        setResultTab(`rs-${i}`)
                      }}
                    >
                      结果 {i + 1}
                      <span className="text-[10px] text-muted-foreground">· {rs.rows.length}</span>
                    </ResultTabButton>
                  ))}
                  {result.messages.length > 0 && (
                    <ResultTabButton active={resultTab === "messages"} onClick={() => setResultTab("messages")}>
                      消息
                      <span className="text-[10px] text-muted-foreground">· {result.messages.length}</span>
                    </ResultTabButton>
                  )}
                  {plan !== null && (
                    <ResultTabButton active={resultTab === "plan"} onClick={() => setResultTab("plan")}>
                      计划
                    </ResultTabButton>
                  )}

                  <div className="ml-auto flex items-center gap-1 pr-1">
                    {currentRs && currentRs.columns.length > 0 && (
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" size="xs">
                            <Download /> 导出
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          <DropdownMenuItem onClick={() => exportResultSet(currentRs, "csv")}>
                            <Download /> CSV
                          </DropdownMenuItem>
                          <DropdownMenuItem onClick={() => exportResultSet(currentRs, "json")}>
                            <FileJson /> JSON
                          </DropdownMenuItem>
                          <DropdownMenuItem onClick={() => exportResultSet(currentRs, "md")}>
                            <FileText /> Markdown
                          </DropdownMenuItem>
                          <DropdownMenuSeparator />
                          <DropdownMenuItem onClick={() => exportResultSet(currentRs, "insert")}>
                            <Braces /> INSERT 语句
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    )}
                  </div>
                </div>

                {/* 内容 */}
                {result.resultSets.map((rs, i) => (
                  <div
                    key={i}
                    className={cn("min-h-0 flex-1 flex-col", resultTab === `rs-${i}` ? "flex" : "hidden")}
                  >
                    <ResultsGrid rs={rs} />
                  </div>
                ))}
                {resultTab === "messages" && (
                  <div className="min-h-0 flex-1 overflow-y-auto p-3">
                    {result.messages.map((m, i) => (
                      <div key={i} className="border-b border-border/40 py-1 text-xs text-muted-foreground">
                        {m}
                      </div>
                    ))}
                  </div>
                )}
                {resultTab === "plan" && plan !== null && (
                  <div className="min-h-0 flex-1 overflow-auto p-3">
                    <Button
                      variant="ghost"
                      size="xs"
                      className="mb-1"
                      onClick={() => {
                        void navigator.clipboard.writeText(plan)
                        toast.success("已复制计划")
                      }}
                    >
                      复制
                    </Button>
                    <pre className="whitespace-pre font-mono text-xs leading-relaxed">{plan || "（空计划）"}</pre>
                  </div>
                )}

                {/* 状态条 */}
                <div className="flex h-7 shrink-0 items-center gap-3 border-t px-3 text-[11px] text-muted-foreground">
                  <span>
                    共 {totalRows} 行{truncated && "（已达上限截断）"}
                  </span>
                  <span>{result.elapsedMs.toFixed(1)} ms</span>
                  <span className="ml-auto">
                    <Check className="mr-0.5 inline size-3 animate-pop-in text-emerald-500" />
                    完成
                  </span>
                </div>
              </div>
            ) : (
              <Empty className="border-none">
                <EmptyHeader>
                  <EmptyMedia variant="icon">
                    <Play />
                  </EmptyMedia>
                  <EmptyTitle>编写 SQL 后按 {MOD_KEY}+↩ 执行</EmptyTitle>
                  <EmptyDescription>
                    选中 / 光标处语句优先执行，{MOD_KEY}+⇧+↩ 执行整个脚本；表名、列名自动补全
                  </EmptyDescription>
                </EmptyHeader>
              </Empty>
            )}
          </div>
        </ResizablePanel>
      </Resizable>

      <SaveQueryDialog
        open={saveOpen}
        onOpenChange={setSaveOpen}
        sql={viewRef.current?.state.doc.toString() ?? ""}
        defaultName={`${database} 查询`}
        connectionId={connectionId}
        connectionName={connectionName}
        database={database}
        schema={schema}
      />
    </div>
  )
}

function ResultTabButton({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      className={cn(
        "-mb-px flex shrink-0 items-center gap-1 border-b-2 px-2.5 py-1.5 text-xs",
        active
          ? "border-b-primary bg-accent/50 font-medium"
          : "border-transparent text-muted-foreground hover:bg-accent/30",
      )}
      onClick={onClick}
    >
      {children}
    </button>
  )
}
