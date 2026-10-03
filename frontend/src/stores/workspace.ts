import { create } from "zustand";
import { useQuerySessions } from "@/stores/query-session";

export type TabKind = "grid" | "designer" | "query" | "sync";

export interface WorkspaceTab {
  /** 稳定 key：同 key 复用同一标签 */
  key: string;
  kind: TabKind;
  title: string;
  connectionId: string;
  connectionName?: string;
  database: string;
  schema: string;
  table?: string;
  /** 查询控制台序号（同一连接内自增），决定 key 与标题 */
  queryNo?: number;
}

type OpenTabArg = Omit<WorkspaceTab, "key" | "title"> & { key?: string; title?: string };

interface WorkspaceState {
  tabs: WorkspaceTab[];
  activeKey: string | null;
  openTab: (tab: OpenTabArg) => void;
  closeTab: (key: string) => void;
  closeOthers: (key: string) => void;
  closeRight: (key: string) => void;
  closeAll: () => void;
  setActive: (key: string) => void;
  /** 局部更新标签（查询页切换库/schema 时同步标题与状态栏） */
  patchTab: (key: string, patch: Partial<Omit<WorkspaceTab, "key">>) => void;
  /** 表被删除后关闭指向该表的所有标签（数据网格/设计器） */
  closeTableTabs: (connectionId: string, database: string, schema: string, table: string) => void;
}

function tabKey(tab: OpenTabArg): string {
  if (tab.kind === "query") return `query:${tab.connectionId}:${tab.queryNo ?? 1}`;
  if (tab.kind === "sync") return "sync";
  return `${tab.kind}:${tab.connectionId}/${tab.database}/${tab.schema}/${tab.table ?? ""}`;
}

export const useWorkspace = create<WorkspaceState>((set) => ({
  tabs: [],
  activeKey: null,
  openTab: (tab) =>
    set((state) => {
      // 查询控制台：未指定 key 时每次调用都新开一个编号标签（同连接序号自增）
      let queryNo: number | undefined;
      let title: string;
      if (tab.kind === "query" && !tab.key) {
        const nums = state.tabs
          .filter((t) => t.kind === "query" && t.connectionId === tab.connectionId)
          .map((t) => t.queryNo ?? 0);
        queryNo = Math.max(0, ...nums) + 1;
        title = tab.title || `查询${queryNo} · ${tab.database}`;
      } else {
        queryNo = tab.queryNo;
        title = tab.title ?? "";
      }
      const key = tab.key ?? tabKey({ ...tab, queryNo, title });
      const entry: WorkspaceTab = { ...tab, key, queryNo, title };
      const existing = state.tabs.find((t) => t.key === key);
      const next = existing ? state.tabs.map((t) => (t.key === key ? entry : t)) : [...state.tabs, entry];
      return { tabs: next, activeKey: key };
    }),
  closeTab: (key) =>
    set((state) => {
      const closed = state.tabs.find((t) => t.key === key);
      if (closed?.kind === "query") useQuerySessions.getState().drop(key);
      const tabs = state.tabs.filter((t) => t.key !== key);
      let activeKey = state.activeKey;
      if (state.activeKey === key) activeKey = tabs.at(-1)?.key ?? null;
      return { tabs, activeKey };
    }),
  closeOthers: (key) =>
    set((state) => {
      const closed = state.tabs.filter((t) => t.key !== key && t.kind === "query");
      for (const t of closed) useQuerySessions.getState().drop(t.key);
      const tabs = state.tabs.filter((t) => t.key === key);
      return { tabs, activeKey: key };
    }),
  closeRight: (key) =>
    set((state) => {
      const idx = state.tabs.findIndex((t) => t.key === key);
      if (idx === -1) return {};
      const closed = state.tabs.slice(idx + 1).filter((t) => t.kind === "query");
      for (const t of closed) useQuerySessions.getState().drop(t.key);
      const tabs = state.tabs.slice(0, idx + 1);
      return { tabs, activeKey: key };
    }),
  closeAll: () =>
    set((state) => {
      for (const t of state.tabs) if (t.kind === "query") useQuerySessions.getState().drop(t.key);
      return { tabs: [], activeKey: null };
    }),
  setActive: (key) => set({ activeKey: key }),
  patchTab: (key, patch) =>
    set((state) => ({
      tabs: state.tabs.map((t) => (t.key === key ? { ...t, ...patch, key } : t)),
    })),
  closeTableTabs: (connectionId, database, schema, table) =>
    set((state) => {
      const match = (t: WorkspaceTab) =>
        (t.kind === "grid" || t.kind === "designer") &&
        t.connectionId === connectionId &&
        t.database === database &&
        t.schema === schema &&
        t.table === table;
      const removed = state.tabs.filter(match);
      if (removed.length === 0) return {};
      const tabs = state.tabs.filter((t) => !match(t));
      let activeKey = state.activeKey;
      if (activeKey !== null && removed.some((t) => t.key === activeKey))
        activeKey = tabs.at(-1)?.key ?? null;
      return { tabs, activeKey };
    }),
}));
