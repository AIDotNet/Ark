import type {
  AiFixRequest,
  AiExplainRequest,
  AiSettingsDto,
  AiSqlRequest,
  AiTestResponse,
  AiTextResponse,
  ApplyChangesResponse,
  CanonicalTable,
  CompletionSchema,
  ConnectionDto,
  DatabaseInfo,
  DdlExecuteResponse,
  DdlPreviewResponse,
  ExplainResponse,
  ImportResult,
  QueryResponse,
  RowChangeSet,
  RowPage,
  RowQueryRequest,
  SaveAiSettings,
  SaveConnection,
  SchemaInfo,
  CompareRequest,
  DiffRowsResponse,
  ExecutionPlan,
  SaveProfileRequest,
  SyncExecuteRequest,
  SyncPlanRequest,
  SyncProfileDto,
  SyncTaskDto,
  SyncTaskStatus,
  TaskKind,
  TableDetailResponse,
  TableSummary,
  TestConnectionResponse,
} from "@/types/api";

interface ApiEnvelope<T> {
  code: number;
  message: string;
  data: T | null;
  traceId: string;
  timestamp: string;
}

export class ArkApiError extends Error {
  readonly code: number;
  constructor(code: number, message: string) {
    super(message);
    this.code = code;
    this.name = "ArkApiError";
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(path, init);
  } catch (e) {
    throw new ArkApiError(-1, `无法连接后端服务: ${(e as Error).message}`);
  }
  let body: ApiEnvelope<T>;
  try {
    body = (await res.json()) as ApiEnvelope<T>;
  } catch {
    throw new ArkApiError(-1, `HTTP ${res.status}（响应不是有效 JSON）`);
  }
  if (body.code !== 0) throw new ArkApiError(body.code, body.message);
  return body.data as T;
}

function jsonInit(method: string, body: unknown): RequestInit {
  return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
}

export interface TableRefArgs {
  connectionId: string;
  database: string;
  schema: string;
  table: string;
}

function tablePath({ connectionId, database, schema, table }: TableRefArgs, suffix = "") {
  return `/api/connections/${connectionId}/databases/${encodeURIComponent(database)}/schemas/${encodeURIComponent(schema)}/tables/${encodeURIComponent(table)}${suffix}`;
}

export const api = {
  // ---------- 连接 ----------
  listConnections: () => request<ConnectionDto[]>("/api/connections"),
  getConnection: (id: string) => request<ConnectionDto>(`/api/connections/${id}`),
  createConnection: (b: SaveConnection) => request<ConnectionDto>("/api/connections", jsonInit("POST", b)),
  updateConnection: (id: string, b: SaveConnection) =>
    request<ConnectionDto>(`/api/connections/${id}`, jsonInit("PUT", b)),
  deleteConnection: (id: string) => request<null>(`/api/connections/${id}`, { method: "DELETE" }),
  testSavedConnection: (id: string) =>
    request<TestConnectionResponse>(`/api/connections/${id}/test`, { method: "POST" }),
  testConnection: (b: SaveConnection) => request<TestConnectionResponse>("/api/connections/test", jsonInit("POST", b)),

  // ---------- 元数据 ----------
  listDatabases: (connectionId: string) => request<DatabaseInfo[]>(`/api/connections/${connectionId}/databases`),
  listSchemas: (connectionId: string, database: string) =>
    request<SchemaInfo[]>(`/api/connections/${connectionId}/databases/${encodeURIComponent(database)}/schemas`),
  listTables: (connectionId: string, database: string, schema: string) =>
    request<TableSummary[]>(
      `/api/connections/${connectionId}/databases/${encodeURIComponent(database)}/schemas/${encodeURIComponent(schema)}/tables`,
    ),
  getTableDetail: (args: TableRefArgs) => request<TableDetailResponse>(tablePath(args)),

  // ---------- 设计器 ----------
  createTable: (connectionId: string, database: string, schema: string, table: CanonicalTable, execute: boolean) =>
    request<DdlExecuteResponse>(`/api/connections/${connectionId}/databases/${encodeURIComponent(database)}/schemas/${encodeURIComponent(schema)}/tables`, jsonInit("POST", { table, execute })),
  previewCreateTable: (connectionId: string, database: string, schema: string, table: CanonicalTable) =>
    request<DdlPreviewResponse>(`/api/connections/${connectionId}/databases/${encodeURIComponent(database)}/schemas/${encodeURIComponent(schema)}/tables/preview`, jsonInit("POST", { table, execute: false })),
  alterTable: (args: TableRefArgs, table: CanonicalTable, execute: boolean) =>
    request<DdlExecuteResponse>(tablePath(args), jsonInit("PUT", { table, execute })),
  previewAlterTable: (args: TableRefArgs, table: CanonicalTable) =>
    request<DdlPreviewResponse>(tablePath(args, "/preview"), jsonInit("PUT", { table, execute: false })),
  dropTable: (args: TableRefArgs) =>
    request<DdlExecuteResponse>(tablePath(args), { method: "DELETE" }),

  // ---------- 数据网格 ----------
  queryRows: (args: TableRefArgs, req: RowQueryRequest) =>
    request<RowPage>(tablePath(args, "/rows/query"), jsonInit("POST", req)),
  applyChanges: (args: TableRefArgs, changes: RowChangeSet) =>
    request<ApplyChangesResponse>(tablePath(args, "/rows/changes"), jsonInit("POST", changes)),

  // ---------- SQL ----------
  executeSql: (
    connectionId: string,
    b: { database: string; schema?: string; sql: string; maxRows?: number; timeoutSeconds?: number },
    signal?: AbortSignal,
  ) => request<QueryResponse>(`/api/connections/${connectionId}/query`, { ...jsonInit("POST", b), signal }),
  explainSql: (
    connectionId: string,
    b: { database: string; schema?: string; sql: string; timeoutSeconds?: number },
  ) => request<ExplainResponse>(`/api/connections/${connectionId}/explain`, jsonInit("POST", b)),

  // ---------- 补全元数据 ----------
  getCompletionSchema: (connectionId: string, database: string, schema: string) =>
    request<CompletionSchema>(
      `/api/connections/${connectionId}/databases/${encodeURIComponent(database)}/schemas/${encodeURIComponent(schema)}/completion`,
    ),

  // ---------- AI 与设置 ----------
  getAiSettings: () => request<AiSettingsDto>("/api/settings/ai"),
  saveAiSettings: (b: SaveAiSettings) => request<AiSettingsDto>("/api/settings/ai", jsonInit("PUT", b)),
  aiTest: () => request<AiTestResponse>("/api/ai/test", { method: "POST" }),
  aiGenerateSql: (b: AiSqlRequest) => request<AiTextResponse>("/api/ai/sql", jsonInit("POST", b)),
  aiExplainSql: (b: AiExplainRequest) => request<AiTextResponse>("/api/ai/explain", jsonInit("POST", b)),
  aiFixSql: (b: AiFixRequest) => request<AiTextResponse>("/api/ai/fix", jsonInit("POST", b)),

  // ---------- 同步（v2） ----------
  buildSyncPlan: (b: SyncPlanRequest) => request<ExecutionPlan>("/api/sync/plan", jsonInit("POST", b)),
  submitSyncTask: (b: SyncExecuteRequest) => request<SyncTaskDto>("/api/sync/tasks", jsonInit("POST", b)),
  getSyncTask: (id: string) => request<SyncTaskDto>(`/api/sync/tasks/${id}`),
  listSyncTasks: (f?: { kind?: TaskKind; status?: SyncTaskStatus; profileId?: string; limit?: number; offset?: number }) => {
    const q = new URLSearchParams();
    if (f?.kind) q.set("kind", f.kind);
    if (f?.status) q.set("status", f.status);
    if (f?.profileId) q.set("profileId", f.profileId);
    if (f?.limit) q.set("limit", String(f.limit));
    if (f?.offset) q.set("offset", String(f.offset));
    const qs = q.toString();
    return request<SyncTaskDto[]>(`/api/sync/tasks${qs ? `?${qs}` : ""}`);
  },
  cancelSyncTask: (id: string) => request<{ ok: boolean }>(`/api/sync/tasks/${id}/cancel`, { method: "POST" }),
  resumeSyncTask: (id: string) => request<SyncTaskDto>(`/api/sync/tasks/${id}/resume`, { method: "POST" }),
  retrySyncTask: (id: string) => request<SyncTaskDto>(`/api/sync/tasks/${id}/retry`, { method: "POST" }),
  exportSyncScript: async (id: string) => {
    const res = await fetch(`/api/sync/tasks/${id}/export-script`);
    if (!res.ok) throw new ArkApiError(-1, `HTTP ${res.status}`);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `ark-sync-${id.slice(0, 8)}.sql`;
    a.click();
    URL.revokeObjectURL(url);
  },

  // ---------- 对比 ----------
  startCompare: (b: CompareRequest) => request<SyncTaskDto>("/api/compare/runs", jsonInit("POST", b)),
  getCompareRun: (id: string) => request<SyncTaskDto>(`/api/compare/runs/${id}`),
  fetchDiffRows: (id: string, table: string, changeType: string, cursor: number, limit = 50) =>
    request<DiffRowsResponse>(
      `/api/compare/runs/${id}/tables/${encodeURIComponent(table)}/diffs?changeType=${changeType}&cursor=${cursor}&limit=${limit}`,
    ),
  compareToPlan: (id: string) => request<ExecutionPlan>(`/api/compare/runs/${id}/to-plan`, { method: "POST" }),

  // ---------- 同步 Profile ----------
  listSyncProfiles: () => request<SyncProfileDto[]>("/api/sync/profiles"),
  createSyncProfile: (b: SaveProfileRequest) => request<SyncProfileDto>("/api/sync/profiles", jsonInit("POST", b)),
  updateSyncProfile: (id: string, b: SaveProfileRequest) =>
    request<SyncProfileDto>(`/api/sync/profiles/${id}`, jsonInit("PUT", b)),
  updateSyncProfileSchedule: (id: string, b: { cron: string | null; scheduleEnabled: boolean }) =>
    request<SyncProfileDto>(`/api/sync/profiles/${id}/schedule`, jsonInit("PATCH", b)),
  deleteSyncProfile: (id: string) => request<{ ok: boolean }>(`/api/sync/profiles/${id}`, { method: "DELETE" }),
  runSyncProfile: (id: string) => request<SyncTaskDto>(`/api/sync/profiles/${id}/run`, { method: "POST" }),

  // ---------- 导入导出 ----------
  exportTable: async (args: TableRefArgs, format: string, limit?: number) => {
    const res = await fetch(tablePath(args, "/export"), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ format, limit }),
    });
    if (!res.ok) {
      let message = `HTTP ${res.status}`;
      try {
        const body = (await res.json()) as ApiEnvelope<unknown>;
        if (body.message) message = body.message;
      } catch { /* 忽略 */ }
      throw new ArkApiError(-1, message);
    }
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${args.table}.${format}`;
    a.click();
    URL.revokeObjectURL(url);
  },
  importTable: async (args: TableRefArgs, file: File, format: string) => {
    const form = new FormData();
    form.append("file", file);
    return request<ImportResult>(`${tablePath(args, "/import")}?format=${encodeURIComponent(format)}`, {
      method: "POST",
      body: form,
    });
  },
};
