import { create } from "zustand";
import { persist } from "zustand/middleware";

export interface SavedQuery {
  id: string;
  name: string;
  sql: string;
  /** 保存时的连接/库上下文（仅作回填默认值，不限制使用位置） */
  connectionId?: string;
  connectionName?: string;
  database?: string;
  schema?: string;
  createdAt: number;
}

interface SavedQueriesState {
  queries: SavedQuery[];
  add: (q: Omit<SavedQuery, "id" | "createdAt">) => void;
  remove: (id: string) => void;
}

const MAX_QUERIES = 100;

/** 保存的查询（全局共享，不按连接隔离） */
export const useSavedQueries = create<SavedQueriesState>()(
  persist(
    (set) => ({
      queries: [],
      add: (q) =>
        set((s) => ({
          queries: [
            { ...q, id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, createdAt: Date.now() },
            ...s.queries,
          ].slice(0, MAX_QUERIES),
        })),
      remove: (id) => set((s) => ({ queries: s.queries.filter((q) => q.id !== id) })),
    }),
    { name: "ark.saved-queries", version: 1 }
  )
);
