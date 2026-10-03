import { useEffect, useState } from "react"
import { useForm } from "react-hook-form"
import { zodResolver } from "@hookform/resolvers/zod"
import { z } from "zod"
import { useQueryClient } from "@tanstack/react-query"
import { CircleCheck, CircleAlert, Loader2 } from "lucide-react"
import { toast } from "sonner"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Separator } from "@/components/ui/separator"
import { Switch } from "@/components/ui/switch"
import { api } from "@/lib/api"
import type { ConnectionDto } from "@/types/api"

const schema = z
  .object({
    name: z.string().min(1, "名称必填").max(100),
    dialect: z.enum(["PostgreSQL", "MySQL", "SQLite"]),
    host: z.string().optional(),
    port: z.string().optional(),
    username: z.string().optional(),
    password: z.string().optional(),
    database: z.string().optional(),
    filePath: z.string().optional(),
    readOnly: z.boolean(),
  })
  .refine((v) => v.dialect !== "SQLite" || (v.filePath ?? "").trim().length > 0, {
    message: "SQLite 连接必须提供数据库文件路径",
    path: ["filePath"],
  })
  .refine((v) => v.dialect === "SQLite" || (v.host ?? "").trim().length > 0, {
    message: "主机地址必填",
    path: ["host"],
  })

export type ConnectionFormValues = z.infer<typeof schema>

export function ConnectionDialog({
  open,
  onOpenChange,
  editing,
}: {
  open: boolean
  onOpenChange: (o: boolean) => void
  editing: ConnectionDto | null
}) {
  const queryClient = useQueryClient()
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<{ ok: boolean; text: string } | null>(null)

  const form = useForm<ConnectionFormValues>({
    resolver: zodResolver(schema),
    defaultValues: { name: "", dialect: "PostgreSQL", host: "", port: "", username: "", password: "", database: "", filePath: "", readOnly: false },
  })
  const dialect = form.watch("dialect")

  useEffect(() => {
    if (!open) return
    setTestResult(null)
    if (editing) {
      form.reset({
        name: editing.name,
        dialect: editing.dialect,
        host: editing.host ?? "",
        port: editing.port?.toString() ?? "",
        username: editing.username ?? "",
        password: "",
        database: editing.database ?? "",
        filePath: editing.filePath ?? "",
        readOnly: editing.readOnly,
      })
    } else {
      form.reset({ name: "", dialect: "PostgreSQL", host: "", port: "", username: "", password: "", database: "", filePath: "", readOnly: false })
    }
  }, [open, editing, form])

  async function onSubmit(values: ConnectionFormValues) {
    const payload = {
      name: values.name,
      dialect: values.dialect,
      host: values.dialect === "SQLite" ? null : values.host || null,
      port: values.dialect === "SQLite" || !values.port ? null : Number(values.port),
      username: values.dialect === "SQLite" ? null : values.username || null,
      password: values.password ? values.password : null,
      database: values.dialect === "SQLite" ? null : values.database || null,
      filePath: values.dialect === "SQLite" ? values.filePath || null : null,
      readOnly: values.readOnly,
    }
    try {
      if (editing) {
        await api.updateConnection(editing.id, payload)
        toast.success("连接已更新")
      } else {
        await api.createConnection(payload)
        toast.success("连接已创建")
      }
      await queryClient.invalidateQueries({ queryKey: ["connections"] })
      onOpenChange(false)
    } catch (e) {
      toast.error((e as Error).message)
    }
  }

  async function onTest() {
    const values = form.getValues()
    setTesting(true)
    setTestResult(null)
    try {
      const payload = {
        name: values.name || "test",
        dialect: values.dialect,
        host: values.host || null,
        port: values.port ? Number(values.port) : null,
        username: values.username || null,
        password: values.password || null,
        database: values.database || null,
        filePath: values.filePath || null,
        readOnly: values.readOnly,
      }
      const r = await api.testConnection(payload as never)
      setTestResult(
        r.success
          ? { ok: true, text: `连接成功：${r.serverVersion}（${r.elapsedMs.toFixed(0)}ms）` }
          : { ok: false, text: `连接失败：${r.error}` },
      )
    } catch (e) {
      setTestResult({ ok: false, text: `连接失败：${(e as Error).message}` })
    } finally {
      setTesting(false)
    }
  }

  const isSqlite = dialect === "SQLite"

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{editing ? "编辑连接" : "新建连接"}</DialogTitle>
          <DialogDescription>连接配置保存在服务端 data/ark.db，密码加密存储。</DialogDescription>
        </DialogHeader>

        <Form {...form}>
          <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <FormField
                control={form.control}
                name="name"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel className="text-xs">名称</FormLabel>
                    <FormControl>
                      <Input placeholder="我的数据库" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="dialect"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel className="text-xs">类型</FormLabel>
                    <Select value={field.value} onValueChange={field.onChange}>
                      <FormControl>
                        <SelectTrigger className="w-full">
                          <SelectValue />
                        </SelectTrigger>
                      </FormControl>
                      <SelectContent>
                        <SelectItem value="PostgreSQL">PostgreSQL</SelectItem>
                        <SelectItem value="MySQL">MySQL</SelectItem>
                        <SelectItem value="SQLite">SQLite</SelectItem>
                      </SelectContent>
                    </Select>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>

            {!isSqlite && (
              <div className="grid grid-cols-4 gap-3">
                <FormField
                  control={form.control}
                  name="host"
                  render={({ field }) => (
                    <FormItem className="col-span-3">
                      <FormLabel className="text-xs">主机</FormLabel>
                      <FormControl>
                        <Input placeholder="localhost" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="port"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel className="text-xs">端口</FormLabel>
                      <FormControl>
                        <Input
                          placeholder={dialect === "MySQL" ? "3306" : "5432"}
                          inputMode="numeric"
                          onChange={(e) => field.onChange(e.target.value.replace(/\D/g, ""))}
                          value={field.value ?? ""}
                        />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
            )}

            {!isSqlite && (
              <div className="grid grid-cols-2 gap-3">
                <FormField
                  control={form.control}
                  name="username"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel className="text-xs">用户名</FormLabel>
                      <FormControl>
                        <Input autoComplete="off" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="password"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel className="text-xs">{editing?.hasPassword ? "密码（留空保持不变）" : "密码"}</FormLabel>
                      <FormControl>
                        <Input type="password" autoComplete="new-password" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
            )}

            {!isSqlite && (
              <FormField
                control={form.control}
                name="database"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel className="text-xs">默认数据库</FormLabel>
                    <FormControl>
                      <Input placeholder="postgres" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
            )}

            {isSqlite && (
              <FormField
                control={form.control}
                name="filePath"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel className="text-xs">数据库文件路径</FormLabel>
                    <FormControl>
                      <Input placeholder="/path/to/data.db" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
            )}

            <FormField
              control={form.control}
              name="readOnly"
              render={({ field }) => (
                <FormItem>
                  <div className="flex items-center justify-between rounded-md border px-3 py-2">
                    <FormLabel className="text-sm font-normal">只读模式（拒绝所有写操作）</FormLabel>
                    <FormControl>
                      <Switch checked={field.value} onCheckedChange={field.onChange} />
                    </FormControl>
                  </div>
                  <FormMessage />
                </FormItem>
              )}
            />

            {testResult && (
              <Alert variant={testResult.ok ? "default" : "destructive"} className="text-xs">
                {testResult.ok ? <CircleCheck /> : <CircleAlert />}
                <AlertDescription className="text-xs">{testResult.text}</AlertDescription>
              </Alert>
            )}

            <Separator />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onTest} disabled={testing}>
                {testing && <Loader2 className="animate-spin" />} 测试连接
              </Button>
              <Button type="submit" disabled={form.formState.isSubmitting}>
                {editing ? "保存" : "创建"}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  )
}
