import { useRef, useState } from "react"
import { useMutation } from "@tanstack/react-query"
import { Download, Loader2, Upload } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { api, type TableRefArgs } from "@/lib/api"

export function ImportExportDialog({
  open,
  onOpenChange,
  connectionId,
  database,
  schema,
  table,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
} & Omit<TableRefArgs, "connectionId"> & { connectionId: string }) {
  const args: TableRefArgs = { connectionId, database, schema, table }
  const [format, setFormat] = useState("csv")
  const [limit, setLimit] = useState("")
  const [importFormat, setImportFormat] = useState("csv")
  const fileRef = useRef<HTMLInputElement>(null)

  const exportMut = useMutation({
    mutationFn: () => api.exportTable(args, format, limit ? Number(limit) : undefined),
    onSuccess: () => toast.success(`已导出 ${table}.${format}`),
    onError: (e) => toast.error((e as Error).message),
  })

  const importMut = useMutation({
    mutationFn: () => {
      const file = fileRef.current?.files?.[0]
      if (!file) throw new Error("请选择文件")
      return api.importTable(args, file, importFormat)
    },
    onSuccess: (res) => {
      toast.success(
        `导入完成：写入 ${res.inserted} 行${res.tableCreated ? "（已自动建表）" : ""}${res.warnings.length > 0 ? `；警告 ${res.warnings.length} 条` : ""}`,
      )
      if (res.warnings.length > 0) toast.warning(res.warnings.slice(0, 3).join("；"))
    },
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>导入 / 导出</DialogTitle>
          <DialogDescription>
            {schema}.{table}
          </DialogDescription>
        </DialogHeader>

        <Tabs defaultValue="export">
          <TabsList className="w-full">
            <TabsTrigger value="export" className="flex-1">导出</TabsTrigger>
            <TabsTrigger value="import" className="flex-1">导入</TabsTrigger>
          </TabsList>

          <TabsContent value="export" className="space-y-3 pt-2">
            <div className="flex items-center gap-3">
              <Label className="w-16 text-xs">格式</Label>
              <Select value={format} onValueChange={setFormat}>
                <SelectTrigger className="w-40" size="sm">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="csv">CSV</SelectItem>
                  <SelectItem value="json">JSON</SelectItem>
                  <SelectItem value="sql">SQL dump</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <div className="flex items-center gap-3">
              <Label className="w-16 text-xs">行数上限</Label>
              <Input
                className="h-8 w-40"
                placeholder="留空 = 全部"
                value={limit}
                onChange={(e) => setLimit(e.target.value.replace(/\D/g, ""))}
              />
            </div>
            <Button className="w-full" onClick={() => exportMut.mutate()} disabled={exportMut.isPending}>
              {exportMut.isPending ? <Loader2 className="animate-spin" /> : <Download />} 导出并下载
            </Button>
          </TabsContent>

          <TabsContent value="import" className="space-y-3 pt-2">
            <div className="flex items-center gap-3">
              <Label className="w-16 text-xs">文件</Label>
              <Input ref={fileRef} type="file" accept=".csv,.json" className="h-8 text-xs" />
            </div>
            <div className="flex items-center gap-3">
              <Label className="w-16 text-xs">格式</Label>
              <Select value={importFormat} onValueChange={setImportFormat}>
                <SelectTrigger className="w-40" size="sm">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="csv">CSV（首行为表头）</SelectItem>
                  <SelectItem value="json">JSON 数组</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <p className="text-xs text-muted-foreground">
              目标表已存在时按列名映射插入；不存在时按采样值推断类型自动建表。
            </p>
            <Button className="w-full" onClick={() => importMut.mutate()} disabled={importMut.isPending}>
              {importMut.isPending ? <Loader2 className="animate-spin" /> : <Upload />} 开始导入
            </Button>
          </TabsContent>
        </Tabs>
      </DialogContent>
    </Dialog>
  )
}
