import { useState } from "react"
import { useMutation } from "@tanstack/react-query"
import { Download, Loader2 } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { api } from "@/lib/api"

/** 库 / Schema 级批量导出：范围内全部普通表。SQL 为单文件 dump，CSV/JSON 为 ZIP 打包。 */
export function ExportScopeDialog({
  open,
  onOpenChange,
  connectionId,
  database,
  schema,
  scope,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  connectionId: string
  database: string
  /** null = 数据库级（后端展开为库内全部非系统 schema / 默认 schema） */
  schema: string | null
  scope: "database" | "schema"
}) {
  const [format, setFormat] = useState("sql")
  const [limit, setLimit] = useState("")

  const exportMut = useMutation({
    mutationFn: () => api.exportScope(connectionId, database, schema, format, limit ? Number(limit) : undefined),
    onSuccess: () => toast.success(`已导出 ${schema ? `${database}.${schema}` : database}`),
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{scope === "database" ? "导出数据库" : "导出 Schema"}</DialogTitle>
          <DialogDescription>{schema ? `${database}.${schema}` : database}</DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="flex items-center gap-3">
            <Label className="w-16 text-xs">格式</Label>
            <Select value={format} onValueChange={setFormat}>
              <SelectTrigger className="w-52" size="sm">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="sql">SQL dump（单文件：建表 + 数据）</SelectItem>
                <SelectItem value="csv">CSV（ZIP 打包，每表一个文件）</SelectItem>
                <SelectItem value="json">JSON（ZIP 打包，每表一个文件）</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="flex items-center gap-3">
            <Label className="w-16 text-xs">行数上限</Label>
            <Input
              className="h-8 w-40"
              placeholder="留空 = 全部（每表）"
              value={limit}
              onChange={(e) => setLimit(e.target.value.replace(/\D/g, ""))}
            />
          </div>
          <p className="text-xs text-muted-foreground">
            导出范围内全部普通表（不含视图）。SQL 按外键依赖排序（被引用的表先建），可直接在目标库回放。
          </p>
          <Button className="w-full" onClick={() => exportMut.mutate()} disabled={exportMut.isPending}>
            {exportMut.isPending ? <Loader2 className="animate-spin" /> : <Download />} 导出并下载
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
