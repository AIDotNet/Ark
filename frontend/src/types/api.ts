// 与后端 Ark.Core DTO 对齐的类型（camelCase）

export type Dialect = "PostgreSQL" | "MySQL" | "SQLite";

export interface ConnectionDto {
  id: string;
  name: string;
  dialect: Dialect;
  host: string | null;
  port: number | null;
  username: string | null;
  hasPassword: boolean;
  database: string | null;
  filePath: string | null;
  readOnly: boolean;
  options: Record<string, string>;
  createdAt: string;
  updatedAt: string;
}

export interface SaveConnection {
  name: string;
  dialect: Dialect;
  host?: string | null;
  port?: number | null;
  username?: string | null;
  password?: string | null;
  database?: string | null;
  filePath?: string | null;
  readOnly: boolean;
  options?: Record<string, string>;
}

export interface TestConnectionResponse {
  success: boolean;
  serverVersion: string | null;
  elapsedMs: number;
  error: string | null;
}

// ---------- Canonical 元数据 ----------

export interface CanonicalType {
  id: string;
  length: number | null;
  precision: number | null;
  scale: number | null;
}

export interface CanonicalColumn {
  name: string;
  type: CanonicalType;
  nullable: boolean;
  defaultValueSql: string | null;
  defaultKind: string;
  isAutoIncrement: boolean;
  isPrimaryKey: boolean;
  comment: string | null;
  isGenerated: boolean;
  generatedExpression: string | null;
}

export interface CanonicalIndex {
  name: string;
  columns: string[];
  isUnique: boolean;
  isPrimaryKey: boolean;
  method: string | null;
  whereClause: string | null;
}

export interface CanonicalForeignKey {
  name: string;
  columns: string[];
  referencedTable: string;
  referencedColumns: string[];
  onDelete: string | null;
  onUpdate: string | null;
}

export interface CanonicalTable {
  name: string;
  columns: CanonicalColumn[];
  primaryKeyColumns: string[];
  indexes: CanonicalIndex[];
  foreignKeys: CanonicalForeignKey[];
  comment: string | null;
  options: Record<string, string | null>;
  isView: boolean;
}

export interface DatabaseInfo { name: string }
export interface SchemaInfo { name: string }
export interface TableSummary { name: string; kind: string; estimatedRows: number | null }
export interface TableDetailResponse { table: CanonicalTable; createSql: string | null }

// ---------- 查询 / 行 ----------

export interface QueryColumn { name: string; dataType: string }
export interface QueryResultSet { columns: QueryColumn[]; rows: (unknown | null)[][]; affectedRows: number }
export interface QueryResponse { resultSets: QueryResultSet[]; messages: string[]; elapsedMs: number }

export interface FilterClause { column: string; op: string; value?: unknown; values?: unknown[] }
export interface OrderClause { column: string; descending: boolean }
export interface RowQueryRequest {
  limit: number;
  offset: number;
  filters?: FilterClause[];
  orderBy?: OrderClause[];
}
export interface RowPage {
  columns: QueryColumn[];
  rows: (unknown | null)[][];
  total: number;
  limit: number;
  offset: number;
  primaryKeyColumns: string[];
}
export interface RowChangeSet {
  inserts: { values: Record<string, unknown> }[];
  updates: { key: Record<string, unknown>; values: Record<string, unknown> }[];
  deletes: { key: Record<string, unknown> }[];
}
export interface ApplyChangesResponse { inserted: number; updated: number; deleted: number }

export interface ExecuteSqlRequest {
  database: string;
  schema?: string | null;
  sql: string;
  maxRows?: number;
  timeoutSeconds?: number;
}

export interface ExplainRequest {
  database: string;
  schema?: string | null;
  sql: string;
  timeoutSeconds?: number;
}

export interface ExplainResponse { planText: string }

// ---------- 补全元数据 ----------

export interface CompletionColumn { name: string; dataType: string }
export interface CompletionTable { name: string; kind: string; columns: CompletionColumn[] }
export interface CompletionSchema { tables: CompletionTable[] }

// ---------- AI 助手 ----------

export interface AiSettingsDto { enabled: boolean; baseUrl: string; model: string; hasApiKey: boolean }
export interface SaveAiSettings { enabled: boolean; baseUrl?: string; model?: string; apiKey?: string | null }
export interface AiSqlRequest { connectionId: string; database: string; schema?: string | null; prompt: string }
export interface AiExplainRequest { connectionId: string; database: string; schema?: string | null; sql: string }
export interface AiFixRequest { connectionId: string; database: string; schema?: string | null; sql: string; error?: string | null }
export interface AiTextResponse { text: string }
export interface AiTestResponse { ok: boolean; message: string }

export interface DdlPreviewResponse { statements: string[]; warnings: string[] }
export interface DdlExecuteResponse { statements: string[]; executed: number; errors: string[] }

// ---------- 同步（v2：快照计划 / 对比 / Profile / 调度） ----------

export type SyncMode = "StructureOnly" | "StructureAndData";
export type DataSyncMethod = "FullCopy" | "RowDiff";
export type ConflictMode = "Error" | "Truncate" | "SkipExisting";
export type VerifyMode = "Off" | "Sample" | "Full";
export type MaskRuleKind = "Null" | "RandomInt" | "RandomLetter" | "RandomDate" | "Fixed" | "SqlExpr";

export interface MaskRule { columnPattern: string; kind: MaskRuleKind; value?: string | null }

export interface TableSelection {
  database: string;
  schema?: string | null;
  table: string;
  where?: string | null;
  columnMap?: Record<string, string> | null;
  methodOverride?: DataSyncMethod | null;
  excludeColumns?: string[] | null;
  maskRules?: MaskRule[] | null;
}

export interface SyncPlanRequest {
  sourceConnectionId: string;
  sourceDatabase: string;
  targetConnectionId: string;
  targetDatabase: string;
  mode: SyncMode;
  dataMethod: DataSyncMethod;
  conflictMode: ConflictMode;
  dropExtraTables: boolean;
  deleteExtraRows: boolean;
  alignAutoIncrement: boolean;
  batchSize: number;
  chunkRows: number;
  deferIndexes: boolean;
  verifyMode: VerifyMode;
  maxParallelTables: number;
  targetSchema?: string | null;
  tables: TableSelection[];
}

export interface DdlAction {
  id: string;
  kind: string;
  summary: string;
  sql: string;
  isDestructive: boolean;
  warnings: string[];
}
export interface ExtensionDependency { name: string; installSql: string; present: boolean; note: string | null }
export interface RenameCandidate { dropColumn: string; addColumn: string; confidence: string }
export interface DataPlan {
  method: DataSyncMethod;
  effectiveMethod: DataSyncMethod;
  hasPrimaryKey: boolean;
  estimatedRows: number | null;
  conflictMode: ConflictMode;
  warnings: string[];
}
export interface ConversionIssue { severity: string; message: string }
export interface CanonicalTableLite {
  name: string;
  isView: boolean;
  primaryKeyColumns?: string[];
}
export interface TableExecution {
  source: TableSelection;
  target: TableSelection;
  targetTableExists: boolean;
  structureActions: DdlAction[];
  postCopyActions: DdlAction[];
  data: DataPlan | null;
  conversionIssues: ConversionIssue[];
  extensions: ExtensionDependency[];
  renameCandidates: RenameCandidate[];
  sourceMeta?: CanonicalTableLite | null;
  targetConverted?: unknown | null;
}
export interface ExecutionPlan {
  id: string;
  createdAt: string;
  options: SyncPlanRequest;
  tables: TableExecution[];
}

export interface SyncExecuteRequest {
  plan: ExecutionPlan;
  skipActionIds: string[];
  confirmDestructive: boolean;
}

export interface VerifyResult {
  sourceRows: number;
  targetRows: number;
  countsMatch: boolean;
  sampleMatch: boolean | null;
  match: boolean;
}

export interface TableSyncReport {
  table: string;
  ddlExecuted: number;
  ddlFailed: number;
  rowsInserted: number;
  rowsUpdated: number;
  rowsDeleted: number;
  elapsedMs: number;
  warnings: string[];
  error: string | null;
  verification?: VerifyResult | null;
}

export type SyncTaskStatus =
  | "Queued" | "Running" | "CancelRequested" | "Cancelled"
  | "Completed" | "Failed" | "PartiallyFailed" | "Interrupted";
export type TaskKind = "Sync" | "Compare";

export interface DiffSampleRow { key: string; changeType: string; sourceValues: string[]; targetValues: string[] }
export interface TableCompareResult {
  table: string;
  chunks: number;
  diffChunks: number;
  inserted: number;
  updated: number;
  deleted: number;
  structureChanges: number;
  samples: DiffSampleRow[];
  samplesTruncated: boolean;
  verification?: VerifyResult | null;
  error: string | null;
}
export interface DiffKeys {
  inserted: string[]; insertedTruncated: boolean;
  updated: string[]; updatedTruncated: boolean;
  deleted: string[]; deletedTruncated: boolean;
}
export interface CompareResult {
  tables: TableCompareResult[];
  diffKeysByTable?: Record<string, DiffKeys> | null;
}

export interface SyncTaskDto {
  id: string;
  kind: TaskKind;
  profileId: string | null;
  status: SyncTaskStatus;
  currentTable: string | null;
  percent: number;
  message: string | null;
  rowsDone: number;
  rowsTotal: number;
  startedAt: string;
  finishedAt: string | null;
  error: string | null;
  log: string[];
  reports: TableSyncReport[];
  compare?: CompareResult | null;
}

export interface CompareRequest {
  sourceConnectionId: string;
  sourceDatabase: string;
  targetConnectionId: string;
  targetDatabase: string;
  tables: TableSelection[];
  chunkRows: number;
  maxDiffKeys: number;
  targetSchema?: string | null;
}

export interface DiffRowsResponse {
  table: string;
  changeType: string;
  total: number;
  truncated: boolean;
  columns: string[];
  pkColumns: string[];
  cursor: number;
  rows: { key: string; source: string[] | null; target: string[] | null }[];
}

// ---------- 同步 Profile ----------

export interface SaveProfileRequest { name: string; config: SyncPlanRequest; cron: string | null; scheduleEnabled: boolean }

export interface SyncProfileDto {
  id: string;
  name: string;
  config: SyncPlanRequest;
  cron: string | null;
  scheduleEnabled: boolean;
  nextRun: string | null;
  lastRunAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ImportResult {
  table: string;
  inserted: number;
  tableCreated: boolean;
  warnings: string[];
}
