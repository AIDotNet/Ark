import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Bot, Loader2, Sparkles, Wand2 } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuShortcut,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { Skeleton } from "@/components/ui/skeleton"
import { Textarea } from "@/components/ui/textarea"
import { api } from "@/lib/api"

/**
 * 查询工具栏 AI 下拉：生成 SQL（NL2SQL）/ 解释此查询 / AI 修复错误。
 * 未配置 AI 时菜单整体置灰并给出引导。
 */
export function AiMenu({
  connectionId,
  database,
  schema,
  getSql,
  lastError,
  onInsert,
  onFixApply,
}: {
  connectionId: string
  database: string
  schema: string
  /** 实时取当前光标处/选中语句文本（编辑器内容不在 React state 中，须用 getter） */
  getSql: () => string
  /** 最近一次执行错误（有值时展示「AI 修复」） */
  lastError: string | null
  /** 将生成的 SQL 插入编辑器 */
  onInsert: (sql: string) => void
  /** 用修复后的 SQL 替换出错的语句 */
  onFixApply: (sql: string) => void
}) {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: ["ai-settings"], queryFn: api.getAiSettings })
  const enabled = settings.data?.enabled ?? false

  const [genOpen, setGenOpen] = useState(false)
  const [prompt, setPrompt] = useState("")
  const [generated, setGenerated] = useState<string | null>(null)
  const gen = useMutation({
    mutationFn: () => api.aiGenerateSql({ connectionId, database, schema, prompt: prompt.trim() }),
    onSuccess: (r) => setGenerated(r.text.trim()),
    onError: (e) => toast.error((e as Error).message),
  })

  const [explainOpen, setExplainOpen] = useState(false)
  const explain = useMutation({
    mutationFn: (sql: string) => api.aiExplainSql({ connectionId, database, schema, sql }),
    onError: (e) => toast.error((e as Error).message),
  })

  const [fixOpen, setFixOpen] = useState(false)
  const fix = useMutation({
    mutationFn: (sql: string) => api.aiFixSql({ connectionId, database, schema, sql, error: lastError }),
    onError: (e) => toast.error((e as Error).message),
  })

  function openExplain() {
    const sql = getSql()
    if (!sql.trim()) {
      toast.error("当前没有可解释的 SQL")
      return
    }
    setExplainOpen(true)
    explain.mutate(sql)
  }

  function openFix() {
    const sql = getSql()
    if (!sql.trim()) {
      toast.error("当前没有可修复的 SQL")
      return
    }
    setFixOpen(true)
    fix.mutate(sql)
  }

  /** 从「原因：… 修正后的 SQL：…」回答中提取修正 SQL（末段） */
  function extractFixedSql(text: string): string {
    const idx = text.indexOf("修正后的 SQL")
    if (idx >= 0) {
      const after = text.slice(idx)
      const lineBreak = after.indexOf("\n")
      return (lineBreak >= 0 ? after.slice(lineBreak + 1) : after).trim()
    }
    // 无标记时取最后一个代码块或整段
    const fence = /```(?:sql)?\s*([\s\S]*?)```/.exec(text)
    return (fence ? fence[1] : text).trim()
  }

  function extractReason(text: string): string {
    const idx = text.indexOf("修正后的 SQL")
    return idx >= 0 ? text.slice(0, idx).trim() : ""
  }

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="sm" disabled={settings.isLoading}>
            <Bot /> AI
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-56">
          {!enabled ? (
            <>
              <DropdownMenuSeparator />
              <div className="px-2 py-2 text-xs text-muted-foreground">
                AI 助手未启用 · 点击顶栏 ⚙️ 打开设置配置接口
              </div>
            </>
          ) : (
            <>
              <Popover open={genOpen} onOpenChange={(o) => { setGenOpen(o); if (!o) setGenerated(null) }}>
                <PopoverTrigger asChild>
                  <DropdownMenuItem
                    onSelect={(e) => e.preventDefault()}
                  >
                    <Sparkles /> 生成 SQL…
                  </DropdownMenuItem>
                </PopoverTrigger>
                <PopoverContent align="start" className="w-[420px] p-3">
                  <p className="mb-2 text-xs font-medium">用自然语言描述要查什么：</p>
                  <Textarea
                    autoFocus
                    className="min-h-20 text-xs"
                    placeholder={`例如：统计${database} 中消费金额最高的前 10 个客户`}
                    value={prompt}
                    onChange={(e) => setPrompt(e.target.value)}
                  />
                  {generated !== null && (
                    <pre className="mt-2 max-h-40 animate-in overflow-auto whitespace-pre-wrap rounded-md bg-muted/50 p-2 font-mono text-xs fade-in slide-in-from-top-1 duration-300">
                      {generated || "（空回复）"}
                    </pre>
                  )}
                  <div className="mt-2 flex justify-end gap-2">
                    <Button size="sm" variant="outline" disabled={gen.isPending || !prompt.trim()} onClick={() => gen.mutate()}>
                      {gen.isPending ? <Loader2 className="animate-spin" /> : <Wand2 />} 生成
                    </Button>
                    <Button
                      size="sm"
                      disabled={!generated?.trim()}
                      onClick={() => {
                        if (generated?.trim()) {
                          onInsert(generated)
                          setGenOpen(false)
                          setGenerated(null)
                        }
                      }}
                    >
                      插入编辑器
                    </Button>
                  </div>
                </PopoverContent>
              </Popover>
              <DropdownMenuItem onSelect={() => setTimeout(openExplain, 10)}>
                <Bot /> 解释此查询
              </DropdownMenuItem>
              {lastError && (
                <>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem onSelect={() => setTimeout(openFix, 10)}>
                    <Wand2 /> AI 修复错误…
                    <DropdownMenuShortcut>{lastError.slice(0, 14)}…</DropdownMenuShortcut>
                  </DropdownMenuItem>
                </>
              )}
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      {/* 解释结果 */}
      <Dialog open={explainOpen} onOpenChange={setExplainOpen}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle className="text-sm">SQL 解释</DialogTitle>
            <DialogDescription className="max-h-24 overflow-y-auto font-mono text-xs">
              {typeof explain.variables === "string" ? explain.variables : ""}
            </DialogDescription>
          </DialogHeader>
          <div className="max-h-[50vh] overflow-y-auto">
            {explain.isPending ? (
              <div className="space-y-2 p-4">
                <Skeleton className="h-3.5 w-4/5" />
                <Skeleton className="h-3.5 w-full" />
                <Skeleton className="h-3.5 w-3/5" />
              </div>
            ) : (
              <pre className="whitespace-pre-wrap rounded-md bg-muted/50 p-3 text-xs leading-relaxed">
                {explain.data?.text}
              </pre>
            )}
          </div>
        </DialogContent>
      </Dialog>

      {/* 修复结果 */}
      <Dialog open={fixOpen} onOpenChange={setFixOpen}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle className="text-sm">AI 修复</DialogTitle>
            <DialogDescription className="font-mono text-xs">{lastError}</DialogDescription>
          </DialogHeader>
          {fix.isPending ? (
            <div className="space-y-2 p-4">
              <Skeleton className="h-3.5 w-full" />
              <Skeleton className="h-3.5 w-11/12" />
              <Skeleton className="h-3.5 w-2/3" />
            </div>
          ) : (
            <>
              {fix.data && extractReason(fix.data.text) && (
                <p className="rounded-md bg-muted/50 p-2 text-xs leading-relaxed">{extractReason(fix.data.text)}</p>
              )}
              <pre className="max-h-48 overflow-auto whitespace-pre-wrap rounded-md bg-muted/50 p-3 font-mono text-xs">
                {fix.data ? extractFixedSql(fix.data.text) : ""}
              </pre>
            </>
          )}
          <div className="flex justify-end gap-2">
            <Button
              size="sm"
              variant="outline"
              disabled={fix.isPending}
              onClick={() => typeof fix.variables === "string" && fix.mutate(fix.variables)}
            >
              重新生成
            </Button>
            <Button
              size="sm"
              disabled={fix.isPending || !fix.data}
              onClick={() => {
                if (fix.data) {
                  onFixApply(extractFixedSql(fix.data.text))
                  setFixOpen(false)
                  toast.success("已替换出错语句")
                  void queryClient.invalidateQueries({ queryKey: ["ai-settings"] })
                }
              }}
            >
              替换出错语句
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}
