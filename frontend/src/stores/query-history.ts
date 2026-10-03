import { create } from "zustand";
import { persist } from "zustand/middleware";

export interface QueryHistoryEntry {
  id: string;
  connectionId: string;
  database: string;
  sql: string;
  ok: boolean;
  ts: number;
  /** 执行耗时（毫秒） */
  durationMs?: number;
  /** 返回总行数（写语句为 0/缺省） */
  rowCount?: number;
  /** 收藏标记 */
  favorite?: boolean;
}

interface QueryHistoryState {
  entries: QueryHistoryEntry[];
  add: (e: Omit<QueryHistoryEntry, "id" | "ts">) => void;
  toggleFavorite: (id: string) => void;
  remove: (id: string) => void;
  clear: (connectionId: string) => void;
}

const MAX_ENTRIES = 500;

/** SQL 查询历史（localStorage 持久化，每连接独立；连续重复 SQL 就地更新时间戳） */
export const useQueryHistory = create<QueryHistoryState>()(
  persist(
    (set) => ({
      entries: [],
      add: (e) =>
        set((s) => {
          const latest = s.entries.find((x) => x.connectionId === e.connectionId);
          if (latest && latest.sql === e.sql && latest.database === e.database) {
            return {
              entries: s.entries.map((x) =>
                x.id === latest.id
                  ? { ...x, ts: Date.now(), ok: e.ok, durationMs: e.durationMs, rowCount: e.rowCount }
                  : x,
              ),
            };
          }
          return {
            entries: [
              { ...e, id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, ts: Date.now() },
              ...s.entries,
            ].slice(0, MAX_ENTRIES),
          };
        }),
      toggleFavorite: (id) =>
        set((s) => ({
          entries: s.entries.map((x) => (x.id === id ? { ...x, favorite: !x.favorite } : x)),
        })),
      remove: (id) => set((s) => ({ entries: s.entries.filter((x) => x.id !== id) })),
      clear: (connectionId) =>
        set((s) => ({ entries: s.entries.filter((e) => e.connectionId !== connectionId) })),
    }),
    {
      name: "ark.query-history",
      version: 2,
      migrate: (state) => ({
        entries: ((state as { entries?: QueryHistoryEntry[] }).entries ?? []).map((e) => ({
          ...e,
          durationMs: e.durationMs,
          rowCount: e.rowCount,
          favorite: false,
        })),
      }),
    }
  )
);
