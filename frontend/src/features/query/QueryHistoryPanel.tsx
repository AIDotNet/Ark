import { useMemo, useState } from "react"
import { Check, History, Play, Search, Star, Trash2, X } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { cn } from "cn"
import { useQueryHistory, type QueryHistoryEntry } from "@/stores/query-history"
import { useSavedQueries } from "@/stores/saved-queries"

function dayLabel(ts: number): string {
  const d = new Date(ts)
  const today = new Date()
  const isSame = (a: Date, b: Date) => a.toDateString() === b.toDateString()
  if (isSame(d, today)) return "今天"
  const yesterday = new Date(today.getTime() - 86_400_000)
  if (isSame(d, yesterday)) return "昨天"
  return `${d.getMonth() + 1}/${d.getDate()}`
}

function firstLine(sql: string): string {
  return sql.split("\n").find((l) => l.trim())?.trim() ?? ""
}

function timeLabel(ts: number): string {
  return new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
}

/** 历史 + 已保存查询面板（工具栏 🕘 按钮） */
export function QueryHistoryPanel({
  connectionId,
  onPick,
}: {
  connectionId: string
  onPick: (sql: string, run: boolean) => void
}) {
  const { entries, toggleFavorite, clear } = useQueryHistory()
  const saved = useSavedQueries((s) => s.queries)
  const removeSaved = useSavedQueries((s) => s.remove)
  const [open, setOpen] = useState(false)
  const [tab, setTab] = useState("history")
  const [keyword, setKeyword] = useState("")

  const connectionHistory = useMemo(
    () =>
      entries
        .filter((e) => e.connectionId === connectionId)
        .filter((e) => !keyword.trim() || e.sql.toLowerCase().includes(keyword.trim().toLowerCase())),
    [entries, connectionId, keyword],
  )

  const groups = useMemo(() => {
    const map = new Map<string, QueryHistoryEntry[]>()
    for (const e of connectionHistory) {
      const label = dayLabel(e.ts)
      const list = map.get(label)
      if (list) list.push(e)
      else map.set(label, [e])
    }
    return [...map.entries()]
  }, [connectionHistory])

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm">
          <History /> 历史
          {connectionHistory.length > 0 && (
            <span className="text-[10px] text-muted-foreground">({connectionHistory.length})</span>
          )}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-[560px] p-0">
        <Tabs value={tab} onValueChange={setTab}>
          <div className="flex items-center gap-2 border-b px-3 py-2">
            <TabsList className="h-7">
              <TabsTrigger value="history" className="px-2.5 text-xs">历史</TabsTrigger>
              <TabsTrigger value="saved" className="px-2.5 text-xs">已保存</TabsTrigger>
            </TabsList>
            {tab === "history" && (
              <div className="relative ml-auto">
                <Search className="absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground" />
                <Input
                  className="h-7 w-52 pl-7 text-xs"
                  placeholder="搜索 SQL…"
                  value={keyword}
                  onChange={(e) => setKeyword(e.target.value)}
                />
              </div>
            )}
          </div>

          <TabsContent value="history" className="mt-0">
            <div className="max-h-[420px] overflow-y-auto">
              {connectionHistory.length === 0 ? (
                <div className="p-6 text-center text-xs text-muted-foreground">暂无查询历史</div>
              ) : (
                groups.map(([label, list]) => (
                  <div key={label}>
                    <div className="sticky top-0 z-10 bg-muted/60 px-3 py-1 text-[10px] font-medium text-muted-foreground backdrop-blur">
                      {label}
                    </div>
                    {list.map((e) => (
                      <div
                        key={e.id}
                        className="group flex cursor-pointer items-center gap-2 border-b border-border/40 px-3 py-1.5 hover:bg-accent/40"
                        onClick={() => onPick(e.sql, false)}
                        onDoubleClick={() => onPick(e.sql, true)}
                        title="单击回填 · 双击执行"
                      >
                        <span
                          className={cn(
                            "size-1.5 shrink-0 rounded-full",
                            e.ok ? "bg-emerald-500" : "bg-destructive",
                          )}
                        />
                        <div className="min-w-0 flex-1">
                          <div className="truncate font-mono text-xs">{firstLine(e.sql)}</div>
                          <div className="flex items-center gap-2 text-[10px] text-muted-foreground">
                            <span>{e.database}</span>
                            <span>{timeLabel(e.ts)}</span>
                            {e.durationMs !== undefined && <span>{e.durationMs.toFixed(0)} ms</span>}
                            {e.rowCount !== undefined && e.rowCount > 0 && <span>{e.rowCount} 行</span>}
                          </div>
                        </div>
                        <button
                          aria-label="收藏"
                          className={cn(
                            "shrink-0 rounded p-1 opacity-0 transition-opacity group-hover:opacity-100",
                            e.favorite && "opacity-100",
                          )}
                          onClick={(ev) => {
                            ev.stopPropagation()
                            toggleFavorite(e.id)
                          }}
                        >
                          <Star className={cn("size-3.5", e.favorite && "fill-amber-400 text-amber-400")} />
                        </button>
                        <Play className="size-3 shrink-0 text-muted-foreground opacity-0 group-hover:opacity-100" />
                      </div>
                    ))}
                  </div>
                ))
              )}
            </div>
            {connectionHistory.length > 0 && (
              <div className="flex items-center justify-between border-t px-3 py-1.5 text-xs text-muted-foreground">
                <span>共 {connectionHistory.length} 条 · 单击回填，双击执行</span>
                <Button variant="ghost" size="xs" onClick={() => clear(connectionId)}>
                  <Trash2 /> 清空该连接
                </Button>
              </div>
            )}
          </TabsContent>

          <TabsContent value="saved" className="mt-0">
            <div className="max-h-[420px] overflow-y-auto">
              {saved.length === 0 ? (
                <div className="p-6 text-center text-xs text-muted-foreground">
                  暂无保存的查询 · 点击工具栏 ⭐ 保存当前 SQL
                </div>
              ) : (
                saved.map((q) => (
                  <div
                    key={q.id}
                    className="group flex cursor-pointer items-center gap-2 border-b border-border/40 px-3 py-1.5 hover:bg-accent/40"
                    onClick={() => onPick(q.sql, false)}
                    onDoubleClick={() => onPick(q.sql, true)}
                    title="单击回填 · 双击执行"
                  >
                    <Check className="size-3.5 shrink-0 text-sky-500" />
                    <div className="min-w-0 flex-1">
                      <div className="truncate text-xs font-medium">{q.name}</div>
                      <div className="truncate font-mono text-[10px] text-muted-foreground">{firstLine(q.sql)}</div>
                    </div>
                    <button
                      aria-label="删除"
                      className="shrink-0 rounded p-1 opacity-0 transition-opacity hover:text-destructive group-hover:opacity-100"
                      onClick={(ev) => {
                        ev.stopPropagation()
                        removeSaved(q.id)
                      }}
                    >
                      <X className="size-3.5" />
                    </button>
                  </div>
                ))
              )}
            </div>
          </TabsContent>
        </Tabs>
      </PopoverContent>
    </Popover>
  )
}

/** 保存当前 SQL 对话框（工具栏 ⭐ 按钮） */
export function SaveQueryDialog({
  open,
  onOpenChange,
  sql,
  defaultName,
  connectionId,
  connectionName,
  database,
  schema,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  sql: string
  defaultName: string
  connectionId: string
  connectionName?: string
  database: string
  schema: string
}) {
  const add = useSavedQueries((s) => s.add)
  const [name, setName] = useState(defaultName)

  function save() {
    if (!sql.trim()) {
      toast.error("当前没有可保存的 SQL")
      return
    }
    add({ name: name.trim() || defaultName, sql, connectionId, connectionName, database, schema })
    toast.success("已保存查询")
    onOpenChange(false)
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="text-sm">保存查询</DialogTitle>
        </DialogHeader>
        <Input
          autoFocus
          placeholder="查询名称"
          value={name}
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && save()}
        />
        <pre className="max-h-40 overflow-auto rounded-md bg-muted/50 p-2 font-mono text-xs">{sql || "（空）"}</pre>
        <DialogFooter>
          <Button variant="outline" size="sm" onClick={() => onOpenChange(false)}>
            取消
          </Button>
          <Button size="sm" onClick={save}>
            保存
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
