import { create } from "zustand";
import { persist } from "zustand/middleware";

/** 单个查询控制台的会话状态（localStorage 持久化，按标签 key 索引） */
export interface QuerySession {
  sql: string;
  database: string;
  schema: string;
  savedAt: number;
}

interface QuerySessionState {
  sessions: Record<string, QuerySession>;
  save: (key: string, s: Omit<QuerySession, "savedAt">) => void;
  drop: (key: string) => void;
}

const MAX_SESSIONS = 50;

/** 查询页内容持久化：刷新/重启后按标签 key 恢复 SQL 与库选择；结果集不持久化 */
export const useQuerySessions = create<QuerySessionState>()(
  persist(
    (set) => ({
      sessions: {},
      save: (key, s) =>
        set((st) => {
          const sessions = { ...st.sessions, [key]: { ...s, savedAt: Date.now() } };
          const keys = Object.keys(sessions);
          if (keys.length > MAX_SESSIONS) {
            keys.sort((a, b) => sessions[a]!.savedAt - sessions[b]!.savedAt);
            for (const k of keys.slice(0, keys.length - MAX_SESSIONS)) delete sessions[k];
          }
          return { sessions };
        }),
      drop: (key) =>
        set((st) => {
          if (!(key in st.sessions)) return st;
          const sessions = { ...st.sessions };
          delete sessions[key];
          return { sessions };
        }),
    }),
    { name: "ark.query-sessions", version: 1 }
  )
);
