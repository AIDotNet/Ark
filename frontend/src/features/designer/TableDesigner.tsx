import { useEffect, useState } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Loader2, Plus, Save, Trash2, TriangleAlert, Undo2 } from "lucide-react"
import { toast } from "sonner"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { api } from "@/lib/api"
import type { CanonicalTable, CanonicalType } from "@/types/api"

const TYPE_OPTIONS: { id: string; label: string }[] = [
  { id: "Int16", label: "SMALLINT" },
  { id: "Int32", label: "INT" },
  { id: "Int64", label: "BIGINT" },
  { id: "Decimal", label: "DECIMAL" },
  { id: "Single", label: "FLOAT" },
  { id: "Double", label: "DOUBLE" },
  { id: "Boolean", label: "BOOLEAN" },
  { id: "Char", label: "CHAR(n)" },
  { id: "VarChar", label: "VARCHAR(n)" },
  { id: "Text", label: "TEXT" },
  { id: "Date", label: "DATE" },
  { id: "Time", label: "TIME" },
  { id: "DateTime", label: "DATETIME" },
  { id: "DateTimeOffset", label: "TIMESTAMPTZ" },
  { id: "Binary", label: "BLOB" },
  { id: "Guid", label: "UUID" },
  { id: "Json", label: "JSON" },
]

interface DesignerCol {
  name: string
  typeId: string
  length: string
  precision: string
  scale: string
  nullable: boolean
  isPk: boolean
  isAutoIncrement: boolean
  defaultValue: string
  comment: string
  locked?: boolean
}

function emptyCol(): DesignerCol {
  return { name: "", typeId: "Int64", length: "", precision: "", scale: "", nullable: true, isPk: false, isAutoIncrement: false, defaultValue: "", comment: "" }
}

function colToType(c: DesignerCol): CanonicalType {
  const num = (s: string) => (s.trim() === "" ? null : Number(s))
  return {
    id: c.typeId,
    length: ["Char", "VarChar"].includes(c.typeId) ? num(c.length) : null,
    precision: c.typeId === "Decimal" ? num(c.precision) : null,
    scale: c.typeId === "Decimal" ? num(c.scale) : null,
  }
}

export function TableDesigner({
  connectionId,
  database,
  schema,
  table,
}: {
  connectionId: string
  database: string
  schema: string
  table?: string
}) {
  const queryClient = useQueryClient()
  const isNew = !table
  const [name, setName] = useState(table ?? "")
  const [cols, setCols] = useState<DesignerCol[]>([emptyCol()])
  const [previewOpen, setPreviewOpen] = useState(false)
  const [preview, setPreview] = useState<{ statements: string[]; warnings: string[] } | null>(null)
  const [busy, setBusy] = useState(false)

  const detail = useQuery({
    queryKey: ["tableDetail", connectionId, database, schema, table],
    queryFn: () => api.getTableDetail({ connectionId, database, schema, table: table! }),
    enabled: !isNew,
  })

  useEffect(() => {
    if (isNew || !detail.data) return
    const t = detail.data.table
    setName(t.name)
    setCols(
      t.columns.map((c) => ({
        name: c.name,
        typeId: c.type.id,
        length: c.type.length?.toString() ?? "",
        precision: c.type.precision?.toString() ?? "",
        scale: c.type.scale?.toString() ?? "",
        nullable: c.nullable,
        isPk: t.primaryKeyColumns.includes(c.name),
        isAutoIncrement: c.isAutoIncrement,
        defaultValue: c.defaultValueSql ?? "",
        comment: c.comment ?? "",
        locked: c.isGenerated,
      })),
    )
  }, [detail.data, isNew])

  function buildTable(): CanonicalTable | null {
    const trimmed = name.trim()
    if (!trimmed) {
      toast.error("表名必填")
      return null
    }
    const valid = cols.filter((c) => c.name.trim() !== "")
    if (valid.length === 0) {
      toast.error("至少需要一列")
      return null
    }
    const pkCols = valid.filter((c) => c.isPk).map((c) => c.name.trim())
    return {
      name: trimmed,
      columns: valid.map((c) => ({
        name: c.name.trim(),
        type: colToType(c),
        nullable: c.nullable && !c.isPk,
        defaultValueSql: c.defaultValue.trim() === "" ? null : c.defaultValue.trim(),
        defaultKind: c.defaultValue.trim() === "" ? "None" : "Unrecognized",
        isAutoIncrement: c.isAutoIncrement,
        isPrimaryKey: c.isPk,
        comment: c.comment.trim() === "" ? null : c.comment.trim(),
        isGenerated: false,
        generatedExpression: null,
      })),
      primaryKeyColumns: pkCols,
      indexes: [],
      foreignKeys: [],
      comment: null,
      options: {},
      isView: false,
    }
  }

  async function doPreview() {
    const t = buildTable()
    if (!t) return
    setBusy(true)
    try {
      const res = isNew
        ? await api.previewCreateTable(connectionId, database, schema, t)
        : await api.previewAlterTable({ connectionId, database, schema, table: table! }, t)
      setPreview(res)
      setPreviewOpen(true)
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  async function doSave() {
    const t = buildTable()
    if (!t) return
    setBusy(true)
    try {
      const res = isNew
        ? await api.createTable(connectionId, database, schema, t, true)
        : await api.alterTable({ connectionId, database, schema, table: table! }, t, true)
      if (res.errors.length > 0) {
        toast.error(`执行完成但有 ${res.errors.length} 条失败：${res.errors[0]}`)
      } else {
        toast.success(`已执行 ${res.executed} 条 DDL`)
      }
      await queryClient.invalidateQueries({ queryKey: ["tables"] })
      await queryClient.invalidateQueries({ queryKey: ["tableDetail", connectionId, database, schema] })
    } catch (e) {
      toast.error((e as Error).message)
    } finally {
      setBusy(false)
      setPreviewOpen(false)
    }
  }

  const updateCol = (i: number, patch: Partial<DesignerCol>) =>
    setCols((arr) => arr.map((c, j) => (j === i ? { ...c, ...patch } : c)))

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* 工具栏 */}
      <div className="flex items-center gap-2 border-b px-3 py-2">
        <Label className="text-xs">表名</Label>
        <Input className="h-8 w-56" value={name} onChange={(e) => setName(e.target.value)} disabled={!isNew} />
        {isNew ? <Badge variant="secondary">新建表</Badge> : <Badge variant="outline">{schema}.{table}</Badge>}
        <div className="ml-auto flex items-center gap-2">
          {!isNew && (
            <Button variant="ghost" size="sm" onClick={() => detail.refetch()}>
              <Undo2 /> 重置
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={doPreview} disabled={busy}>
            预览 DDL
          </Button>
          <Button size="sm" onClick={doSave} disabled={busy}>
            {busy ? <Loader2 className="animate-spin" /> : <Save />} 保存执行
          </Button>
        </div>
      </div>

      <Tabs defaultValue="columns" className="flex min-h-0 flex-1 flex-col">
        <TabsList className="mx-3 mt-2 w-fit">
          <TabsTrigger value="columns">列</TabsTrigger>
          {!isNew && <TabsTrigger value="indexes">索引 / 外键</TabsTrigger>}
          {!isNew && <TabsTrigger value="ddl">DDL</TabsTrigger>}
        </TabsList>

        <TabsContent value="columns" className="min-h-0 flex-1 animate-in overflow-auto px-3 pb-3 fade-in slide-in-from-bottom-1 data-[state=inactive]:hidden duration-200">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-44">名称</TableHead>
                <TableHead className="w-40">类型</TableHead>
                <TableHead className="w-16">长度</TableHead>
                <TableHead className="w-16">精度</TableHead>
                <TableHead className="w-14">小数</TableHead>
                <TableHead className="w-14">可空</TableHead>
                <TableHead className="w-12">PK</TableHead>
                <TableHead className="w-16">自增</TableHead>
                <TableHead className="w-36">默认值</TableHead>
                <TableHead className="w-40">注释</TableHead>
                <TableHead className="w-10" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {cols.map((c, i) => (
                <TableRow key={i}>
                  <TableCell className="p-1">
                    <Input className="h-7 text-xs" value={c.name} disabled={c.locked}
                      onChange={(e) => updateCol(i, { name: e.target.value })} />
                  </TableCell>
                  <TableCell className="p-1">
                    <Select value={c.typeId} onValueChange={(v) => updateCol(i, { typeId: v })} disabled={c.locked}>
                      <SelectTrigger className="h-7 w-full text-xs" size="sm">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {TYPE_OPTIONS.map((o) => (
                          <SelectItem key={o.id} value={o.id}>{o.label}</SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </TableCell>
                  {(["length", "precision", "scale"] as const).map((k) => (
                    <TableCell key={k} className="p-1">
                      <Input
                        className="h-7 text-xs"
                        value={c[k]}
                        disabled={c.locked || (k === "length" ? !(c.typeId === "Char" || c.typeId === "VarChar") : c.typeId !== "Decimal")}
                        onChange={(e) => updateCol(i, { [k]: e.target.value.replace(/[^\d]/g, "") } as Partial<DesignerCol>)}
                      />
                    </TableCell>
                  ))}
                  <TableCell className="p-1 text-center">
                    <Switch checked={c.nullable} disabled={c.locked || c.isPk}
                      onCheckedChange={(v) => updateCol(i, { nullable: v })} />
                  </TableCell>
                  <TableCell className="p-1 text-center">
                    <Checkbox checked={c.isPk} disabled={c.locked}
                      onCheckedChange={(v) => updateCol(i, { isPk: v === true })} />
                  </TableCell>
                  <TableCell className="p-1 text-center">
                    <Switch checked={c.isAutoIncrement} disabled={c.locked}
                      onCheckedChange={(v) => updateCol(i, { isAutoIncrement: v })} />
                  </TableCell>
                  <TableCell className="p-1">
                    <Input className="h-7 text-xs" value={c.defaultValue} disabled={c.locked}
                      placeholder="如 0 / CURRENT_TIMESTAMP"
                      onChange={(e) => updateCol(i, { defaultValue: e.target.value })} />
                  </TableCell>
                  <TableCell className="p-1">
                    <Input className="h-7 text-xs" value={c.comment} disabled={c.locked}
                      onChange={(e) => updateCol(i, { comment: e.target.value })} />
                  </TableCell>
                  <TableCell className="p-1">
                    {c.locked ? (
                      <Badge variant="secondary" className="text-[10px]">生成列</Badge>
                    ) : (
                      <Button variant="ghost" size="icon" className="size-6"
                        onClick={() => setCols((arr) => arr.filter((_, j) => j !== i))}>
                        <Trash2 className="size-3.5" />
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <Button variant="outline" size="sm" className="mt-2" onClick={() => setCols((arr) => [...arr, emptyCol()])}>
            <Plus /> 添加列
          </Button>
        </TabsContent>

        {!isNew && (
          <TabsContent value="indexes" className="min-h-0 flex-1 animate-in overflow-auto px-3 fade-in slide-in-from-bottom-1 data-[state=inactive]:hidden duration-200">
            <div className="grid grid-cols-2 gap-4 pt-2">
              <div>
                <Label className="text-xs text-muted-foreground">索引</Label>
                <ul className="mt-1 space-y-1 text-xs">
                  {detail.data?.table.indexes.map((ix) => (
                    <li key={ix.name} className="rounded border px-2 py-1">
                      {ix.isUnique && <Badge variant="secondary" className="mr-1 text-[10px]">UNIQUE</Badge>}
                      {ix.name} ({ix.columns.join(", ")})
                    </li>
                  ))}
                  {detail.data?.table.indexes.length === 0 && <li className="text-muted-foreground">无索引</li>}
                </ul>
              </div>
              <div>
                <Label className="text-xs text-muted-foreground">外键</Label>
                <ul className="mt-1 space-y-1 text-xs">
                  {detail.data?.table.foreignKeys.map((fk) => (
                    <li key={fk.name} className="rounded border px-2 py-1">
                      {fk.columns.join(",")} → {fk.referencedTable}({fk.referencedColumns.join(",")})
                      {fk.onDelete && <span className="text-muted-foreground"> ON DELETE {fk.onDelete}</span>}
                    </li>
                  ))}
                  {detail.data?.table.foreignKeys.length === 0 && <li className="text-muted-foreground">无外键</li>}
                </ul>
              </div>
            </div>
          </TabsContent>
        )}

        {!isNew && (
          <TabsContent value="ddl" className="min-h-0 flex-1 animate-in overflow-auto px-3 fade-in slide-in-from-bottom-1 data-[state=inactive]:hidden duration-200">
            <pre className="mt-2 rounded-md bg-muted p-3 text-xs leading-5">
              {detail.data?.createSql ?? "（该方言无原始 DDL，可查看预览生成）"}
            </pre>
          </TabsContent>
        )}
      </Tabs>

      {/* DDL 预览 */}
      <Dialog open={previewOpen} onOpenChange={setPreviewOpen}>
        <DialogContent className="max-w-2xl">
          <DialogHeader>
            <DialogTitle>DDL 预览</DialogTitle>
            <DialogDescription>确认无误后执行。红色警告需人工确认。</DialogDescription>
          </DialogHeader>
          <div className="max-h-80 overflow-auto">
            {preview?.warnings.map((w, i) => (
              <Alert key={i} variant="warning" className="mb-1">
                <TriangleAlert />
                <AlertDescription className="text-xs">{w}</AlertDescription>
              </Alert>
            ))}
            <pre className="rounded-md bg-muted p-3 text-xs leading-5">{preview?.statements.join("\n\n") || "（无变更）"}</pre>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setPreviewOpen(false)}>关闭</Button>
            <Button onClick={doSave} disabled={busy || (preview?.statements.length ?? 0) === 0}>
              执行 {preview?.statements.length ?? 0} 条
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
