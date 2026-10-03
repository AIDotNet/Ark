import { useEffect, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { useTheme } from "next-themes"
import { ArrowLeftRight, Moon, Pencil, Sun, Table2, TerminalSquare } from "lucide-react"
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
} from "@/components/ui/command"
import { api } from "@/lib/api"
import { defaultSchema } from "@/components/layout/ConnectionTree"
import { useWorkspace, type TabKind, type WorkspaceTab } from "@/stores/workspace"
import type { ConnectionDto } from "@/types/api"

const TAB_ICONS: Record<TabKind, typeof Table2> = {
  grid: Table2,
  designer: Pencil,
  query: TerminalSquare,
  sync: ArrowLeftRight,
}

function firstDbOf(conn: ConnectionDto): string {
  return conn.dialect === "SQLite" ? "main" : (conn.database ?? "")
}

/** ⌘K 命令面板：切换标签页 / 全局操作 / 按连接新建查询 */
export function CommandPalette() {
  const [open, setOpen] = useState(false)
  const { tabs, setActive, openTab } = useWorkspace()
  const { resolvedTheme, setTheme } = useTheme()
  const isDark = resolvedTheme === "dark"
  const connections = useQuery({
    queryKey: ["connections"],
    queryFn: api.listConnections,
    enabled: open,
  })

  useEffect(() => {
    const down = (e: KeyboardEvent) => {
      if ((e.key === "k" || e.key === "K") && (e.metaKey || e.ctrlKey)) {
        e.preventDefault()
        setOpen((o) => !o)
      }
    }
    document.addEventListener("keydown", down)
    return () => document.removeEventListener("keydown", down)
  }, [])

  const run = (fn: () => void) => {
    setOpen(false)
    fn()
  }

  const openQuery = (c: ConnectionDto) => {
    const db = firstDbOf(c)
    openTab({
      kind: "query",
      title: `查询 - ${c.name}`,
      connectionId: c.id,
      connectionName: c.name,
      database: db,
      schema: defaultSchema(c.dialect, db),
    })
  }

  const tabItem = (t: WorkspaceTab) => {
    const Icon = TAB_ICONS[t.kind]
    return (
      <CommandItem key={t.key} value={`tab ${t.title}`} onSelect={() => run(() => setActive(t.key))}>
        <Icon /> {t.title}
      </CommandItem>
    )
  }

  return (
    <CommandDialog open={open} onOpenChange={setOpen}>
      <CommandInput placeholder="搜索命令、标签页、连接…" />
      <CommandList>
        <CommandEmpty>无匹配结果</CommandEmpty>
        {tabs.length > 0 && (
          <>
            <CommandGroup heading="标签页">{tabs.map(tabItem)}</CommandGroup>
            <CommandSeparator />
          </>
        )}
        <CommandGroup heading="操作">
          <CommandItem
            value="sync 数据同步 同步"
            onSelect={() =>
              run(() =>
                openTab({ kind: "sync", title: "数据同步", connectionId: "", database: "", schema: "" })
              )
            }
          >
            <ArrowLeftRight /> 数据同步
          </CommandItem>
          <CommandItem
            value="theme 主题 切换 dark light 暗色 亮色"
            onSelect={() => run(() => setTheme(isDark ? "light" : "dark"))}
          >
            {isDark ? <Sun /> : <Moon />} {isDark ? "切换到亮色主题" : "切换到暗色主题"}
          </CommandItem>
        </CommandGroup>
        {connections.data && connections.data.length > 0 && (
          <>
            <CommandSeparator />
            <CommandGroup heading="新建查询">
              {connections.data.map((c) => (
                <CommandItem
                  key={c.id}
                  value={`query ${c.name} ${c.dialect}`}
                  onSelect={() => run(() => openQuery(c))}
                >
                  <TerminalSquare /> {c.name}（{c.dialect}）
                </CommandItem>
              ))}
            </CommandGroup>
          </>
        )}
      </CommandList>
    </CommandDialog>
  )
}
