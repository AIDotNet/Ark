import { useState } from "react"
import { useIsFetching, useQuery, useQueryClient } from "@tanstack/react-query"
import { useTheme } from "next-themes"
import { AnimatePresence, motion } from "motion/react"
import { ArrowLeftRight, Database, Moon, Pencil, RefreshCw, Settings, Sun, Table2, TerminalSquare, X } from "lucide-react"
import { Button } from "@/components/ui/button"
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@/components/ui/context-menu"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { ErrorBoundary } from "@/components/ErrorBoundary"
import { Progress } from "@/components/ui/progress"
import { Resizable, ResizableHandle, ResizablePanel } from "@/components/ui/resizable"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { api } from "@/lib/api"
import { useWorkspace, type TabKind } from "@/stores/workspace"
import { useActiveTask } from "@/stores/task"
import { ConnectionTree } from "@/components/layout/ConnectionTree"
import { CommandPalette } from "@/components/layout/CommandPalette"
import { SettingsDialog } from "@/components/layout/SettingsDialog"
import { TabContent } from "@/features/tabs/TabContent"
import { easeOutQuart } from "@/components/ui/motion"

const TAB_ICONS: Record<TabKind, typeof Table2> = {
  grid: Table2,
  designer: Pencil,
  query: TerminalSquare,
  sync: ArrowLeftRight,
}

export function AppShell() {
  const { tabs, activeKey, setActive, closeTab, openTab } = useWorkspace()
  const { resolvedTheme, setTheme } = useTheme()
  const isDark = resolvedTheme === "dark"
  const queryClient = useQueryClient()
  const active = tabs.find((t) => t.key === activeKey) ?? null
  const [settingsOpen, setSettingsOpen] = useState(false)
  // 任意 connections 请求在途 → 顶栏刷新图标旋转
  const connectionsFetching = useIsFetching({ queryKey: ["connections"] }) > 0

  return (
    <div className="flex h-svh flex-col overflow-hidden">
      {/* 顶栏 */}
      <header className="flex h-12 shrink-0 items-center gap-2 border-b px-3">
        <Database className="size-4" />
        <span className="font-semibold tracking-wide">Ark</span>
        <span className="text-xs text-muted-foreground">多数据库管理与同步</span>
        <Button
          variant="ghost"
          size="sm"
          onClick={() =>
            // 数据同步页不消费连接上下文（TabContent 渲染 <SyncCenter /> 无 props），仅传占位
            openTab({ kind: "sync", title: "数据同步", connectionId: "", database: "", schema: "" })
          }
        >
          <ArrowLeftRight /> 数据同步
        </Button>
        <Button
          variant="ghost"
          size="sm"
          disabled={!active}
          onClick={() =>
            active &&
            openTab({
              kind: "query",
              connectionId: active.connectionId,
              connectionName: active.connectionName,
              database: active.database,
              schema: active.schema,
            })
          }
        >
          <TerminalSquare /> 新建查询
        </Button>
        <div className="ml-auto flex items-center gap-1">
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                onClick={() => queryClient.invalidateQueries({ queryKey: ["connections"] })}
              >
                <RefreshCw className={`size-4 ${connectionsFetching ? "animate-spin" : ""}`} />
              </Button>
            </TooltipTrigger>
            <TooltipContent>刷新连接</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="ghost" size="icon" onClick={() => setTheme(isDark ? "light" : "dark")}>
                {/* 主题图标旋转交叉切换 */}
                <AnimatePresence mode="wait" initial={false}>
                  <motion.span
                    key={isDark ? "sun" : "moon"}
                    className="flex"
                    initial={{ rotate: -90, opacity: 0, scale: 0.6 }}
                    animate={{ rotate: 0, opacity: 1, scale: 1 }}
                    exit={{ rotate: 90, opacity: 0, scale: 0.6 }}
                    transition={{ duration: 0.18, ease: easeOutQuart }}
                  >
                    {isDark ? <Sun className="size-4" /> : <Moon className="size-4" />}
                  </motion.span>
                </AnimatePresence>
              </Button>
            </TooltipTrigger>
            <TooltipContent>{isDark ? "切换到亮色" : "切换到暗色"}</TooltipContent>
          </Tooltip>
          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="ghost" size="icon" onClick={() => setSettingsOpen(true)}>
                <Settings className="size-4" />
              </Button>
            </TooltipTrigger>
            <TooltipContent>设置（AI 助手）</TooltipContent>
          </Tooltip>
        </div>
      </header>

      {/* 主体：左侧连接树 + 右侧工作区，可拖拽分割 */}
      <div className="flex min-h-0 flex-1">
        <Resizable direction="horizontal" className="min-h-0 flex-1">
          <ResizablePanel defaultSize={21} minSize={12} maxSize={45}>
            <ConnectionTree />
          </ResizablePanel>
          <ResizableHandle />
          <ResizablePanel defaultSize={79}>
            <main className="flex h-full min-w-0 flex-col">
              {tabs.length === 0 ? (
                <Empty className="border-none">
                  <EmptyHeader>
                    <EmptyMedia variant="icon">
                      <Database />
                    </EmptyMedia>
                    <EmptyTitle>开始使用 Ark</EmptyTitle>
                    <EmptyDescription>
                      左侧选择连接与表开始浏览，或打开「数据同步」向导；按 ⌘K 打开命令面板
                    </EmptyDescription>
                  </EmptyHeader>
                </Empty>
              ) : (
                <TabsBar tabs={tabs} activeKey={activeKey} setActive={setActive} closeTab={closeTab} />
              )}
            </main>
          </ResizablePanel>
        </Resizable>
      </div>

      {/* 状态栏 */}
      <footer className="flex h-6 shrink-0 items-center gap-3 border-t px-3 text-xs text-muted-foreground">
        {active ? (
          <>
            <span>{active.connectionName ?? active.connectionId}</span>
            <span>库: {active.database}</span>
            <span>schema: {active.schema}</span>
            {active.table && <span>表: {active.table}</span>}
          </>
        ) : (
          <span>就绪</span>
        )}
        <ActiveTaskBadge />
      </footer>

      <CommandPalette />
      <SettingsDialog open={settingsOpen} onOpenChange={setSettingsOpen} />
    </div>
  )
}

/** 状态栏活动同步任务徽标：点击回到同步向导 */
function ActiveTaskBadge() {
  const taskId = useActiveTask((s) => s.taskId)
  const openTab = useWorkspace((s) => s.openTab)
  const task = useQuery({
    queryKey: ["syncTask", taskId],
    queryFn: () => api.getSyncTask(taskId!),
    enabled: !!taskId,
    refetchInterval: (q) =>
      q.state.data && (q.state.data.status === "Queued" || q.state.data.status === "Running") ? 1500 : false,
  })
  const running = task.data && (task.data.status === "Running" || task.data.status === "Queued")
  if (!taskId || !task.data || !running) return null
  return (
    <button
      className="ml-auto flex animate-in fade-in slide-in-from-bottom-1 items-center gap-2 text-foreground hover:underline duration-300"
      title="查看同步任务"
      onClick={() =>
        openTab({ kind: "sync", title: "数据同步", connectionId: "", database: "", schema: "" })
      }
    >
      <span>同步{task.data.status === "Queued" ? "排队中" : "进行中"}</span>
      <Progress value={task.data.percent} className="h-1.5 w-24" />
      <span>{task.data.percent}%</span>
    </button>
  )
}

function TabsBar({
  tabs,
  activeKey,
  setActive,
  closeTab,
}: {
  tabs: ReturnType<typeof useWorkspace.getState>["tabs"]
  activeKey: string | null
  setActive: (key: string) => void
  closeTab: (key: string) => void
}) {
  const { closeOthers, closeRight, closeAll } = useWorkspace()
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 overflow-x-auto border-b">
        {/* AnimatePresence：关闭标签时宽度收起 + 淡出，兄弟标签被 flex 自然推移 */}
        <AnimatePresence initial={false}>
          {tabs.map((t) => {
            const Icon = TAB_ICONS[t.kind]
            const isActive = t.key === activeKey
            return (
              <ContextMenu key={t.key}>
                <ContextMenuTrigger asChild>
                  <motion.button
                    initial={{ opacity: 0, scale: 0.92, y: -4 }}
                    animate={{ opacity: 1, scale: 1, y: 0 }}
                    exit={{ opacity: 0, scale: 0.92, width: 0, paddingLeft: 0, paddingRight: 0 }}
                    transition={{ duration: 0.18, ease: easeOutQuart }}
                    className={`group flex shrink-0 items-center gap-2 overflow-hidden border-r px-3 py-1.5 text-xs whitespace-nowrap ${
                      isActive
                        ? "border-b-2 border-b-primary bg-accent/50 font-medium"
                        : "text-muted-foreground hover:bg-accent/30"
                    }`}
                    onClick={() => setActive(t.key)}
                    onMouseUp={(e) => {
                      if (e.button === 1) closeTab(t.key)
                    }}
                  >
                    <Icon className="size-3.5 shrink-0" />
                    <span className="max-w-44 truncate">{t.title}</span>
                    <span
                      role="button"
                      tabIndex={0}
                      aria-label="关闭标签"
                      className="rounded p-0.5 opacity-40 transition-[opacity,background-color] duration-150 hover:bg-accent hover:opacity-100"
                      onClick={(e) => {
                        e.stopPropagation()
                        closeTab(t.key)
                      }}
                      onKeyDown={(e) => e.key === "Enter" && closeTab(t.key)}
                    >
                      <X className="size-3 transition-transform duration-150 group-hover:rotate-90" />
                    </span>
                  </motion.button>
                </ContextMenuTrigger>
                <ContextMenuContent>
                  <ContextMenuItem onClick={() => closeTab(t.key)}>关闭</ContextMenuItem>
                  <ContextMenuItem onClick={() => closeOthers(t.key)}>关闭其他</ContextMenuItem>
                  <ContextMenuItem onClick={() => closeRight(t.key)}>关闭右侧</ContextMenuItem>
                  <ContextMenuSeparator />
                  <ContextMenuItem onClick={closeAll}>全部关闭</ContextMenuItem>
                </ContextMenuContent>
              </ContextMenu>
            )
          })}
        </AnimatePresence>
      </div>
      {tabs.map((t) => (
        <div
          key={t.key}
          className={`min-h-0 flex-1 ${
            // display 切换会重启动画：激活标签每次切入都有淡入上移，且内容状态保留不卸载
            t.key === activeKey
              ? "flex flex-col animate-in fade-in slide-in-from-bottom-1 duration-200"
              : "hidden"
          }`}
          >
          {/* ErrorBoundary：单个标签页渲染异常不拖垮整个应用 */}
          <ErrorBoundary label={t.title}>
            <TabContent tab={t} />
          </ErrorBoundary>
        </div>
      ))}
    </div>
  )
}
