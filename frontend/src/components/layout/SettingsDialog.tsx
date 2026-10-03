import { useEffect, useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Loader2 } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Switch } from "@/components/ui/switch"
import { api } from "@/lib/api"

/** 应用设置（当前：AI 助手，OpenAI 兼容接口） */
export function SettingsDialog({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const queryClient = useQueryClient()
  const settings = useQuery({
    queryKey: ["ai-settings"],
    queryFn: api.getAiSettings,
    enabled: open,
  })

  const [enabled, setEnabled] = useState(false)
  const [baseUrl, setBaseUrl] = useState("")
  const [model, setModel] = useState("")
  const [apiKey, setApiKey] = useState("")

  useEffect(() => {
    if (settings.data) {
      setEnabled(settings.data.enabled)
      setBaseUrl(settings.data.baseUrl)
      setModel(settings.data.model)
      setApiKey("")
    }
  }, [settings.data])

  const save = useMutation({
    mutationFn: () =>
      api.saveAiSettings({
        enabled,
        baseUrl: baseUrl.trim(),
        model: model.trim(),
        apiKey: apiKey === "" ? null : apiKey, // 空串 = 保持不变
      }),
    onSuccess: (d) => {
      toast.success("设置已保存")
      queryClient.setQueryData(["ai-settings"], d)
      onOpenChange(false)
    },
    onError: (e) => toast.error((e as Error).message),
  })

  const test = useMutation({
    mutationFn: () =>
      // 先保存再测试，保证测的是当前表单里的配置
      api
        .saveAiSettings({ enabled: true, baseUrl: baseUrl.trim(), model: model.trim(), apiKey: apiKey === "" ? null : apiKey })
        .then(() => api.aiTest()),
    onSuccess: (r) => (r.ok ? toast.success(`连接正常：${r.message}`) : toast.error(`连接失败：${r.message}`)),
    onError: (e) => toast.error((e as Error).message),
  })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="text-sm">设置 · AI 助手</DialogTitle>
          <DialogDescription>
            兼容 OpenAI Chat Completions 接口（OpenAI / Azure / Ollama / 各类代理均可）
          </DialogDescription>
        </DialogHeader>

        {settings.isLoading ? (
          <div className="flex items-center gap-2 p-4 text-sm text-muted-foreground">
            <Loader2 className="animate-spin" /> 加载中…
          </div>
        ) : (
          <div className="space-y-4">
            <div className="flex items-center justify-between rounded-md border p-3">
              <div>
                <Label className="text-sm">启用 AI 助手</Label>
                <p className="text-xs text-muted-foreground">提供 NL2SQL、SQL 解释与错误修复</p>
              </div>
              <Switch checked={enabled} onCheckedChange={setEnabled} />
            </div>

            <div className="space-y-1.5">
              <Label className="text-xs">Base URL</Label>
              <Input
                placeholder="https://api.openai.com/v1"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label className="text-xs">模型</Label>
              <Input
                placeholder="gpt-4o-mini"
                value={model}
                onChange={(e) => setModel(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label className="text-xs">API Key</Label>
              <Input
                type="password"
                placeholder={settings.data?.hasApiKey ? "已保存（留空保持不变）" : "sk-…"}
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
              />
              <p className="text-[10px] text-muted-foreground">
                API Key 经服务器加密存储，保存后不再回显
              </p>
            </div>
          </div>
        )}

        <DialogFooter>
          <Button variant="outline" size="sm" disabled={!enabled || test.isPending} onClick={() => test.mutate()}>
            {test.isPending ? <Loader2 className="animate-spin" /> : null} 测试连接
          </Button>
          <Button size="sm" disabled={save.isPending} onClick={() => save.mutate()}>
            {save.isPending ? <Loader2 className="animate-spin" /> : null} 保存
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
