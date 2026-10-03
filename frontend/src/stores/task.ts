import { create } from "zustand";

interface ActiveTaskState {
  /** 最近提交的同步任务（状态栏轮询展示进度） */
  taskId: string | null;
  setTaskId: (id: string | null) => void;
}

export const useActiveTask = create<ActiveTaskState>((set) => ({
  taskId: null,
  setTaskId: (taskId) => set({ taskId }),
}));
