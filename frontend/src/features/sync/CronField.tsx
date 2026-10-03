import { useMemo, useState } from "react"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"

const PRESETS = [
  { value: "none", label: "不调度", cron: null },
  { value: "hourly", label: "每小时", cron: "0 * * * *" },
  { value: "daily-9", label: "每天 09:00", cron: "0 9 * * *" },
  { value: "daily-22", label: "每天 22:00", cron: "0 22 * * *" },
  { value: "weekly-mon", label: "每周一 09:00", cron: "0 9 * * 1" },
  { value: "custom", label: "自定义…", cron: "" },
]

function presetFor(cron: string | null): string {
  if (!cron) return "none"
  return PRESETS.find((p) => p.cron === cron)?.value ?? "custom"
}

/** cron 调度编辑：预置模板 + 自定义 5 段表达式（分 时 日 月 周） */
export function CronField({
  cron,
  enabled,
  onChange,
}: {
  cron: string | null
  enabled: boolean
  onChange: (cron: string | null, enabled: boolean) => void
}) {
  const [preset, setPreset] = useState(presetFor(cron))
  const custom = preset === "custom"

  const desc = useMemo(() => {
    if (!enabled || !cron) return null
    return `表达式：${cron}（服务器本地时区；保存后列表显示下次运行时间）`
  }, [cron, enabled])

  return (
    <div className="space-y-2">
      <div className="flex items-center gap-3">
        <Switch
          checked={enabled}
          onCheckedChange={(v) => onChange(cron ?? (v ? "0 9 * * *" : null), v)}
        />
        <Label className="text-xs">定时调度</Label>
        <Select
          value={preset}
          disabled={!enabled}
          onValueChange={(v) => {
            setPreset(v)
            const found = PRESETS.find((p) => p.value === v)
            if (found && found.value !== "custom") onChange(found.cron, enabled)
          }}
        >
          <SelectTrigger className="h-8 w-40" size="sm">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {PRESETS.map((p) => (
              <SelectItem key={p.value} value={p.value}>
                {p.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {custom && (
          <Input
            className="h-8 w-44 animate-in fade-in slide-in-from-top-1 font-mono text-xs duration-200"
            placeholder="0 9 * * *"
            value={cron ?? ""}
            onChange={(e) => onChange(e.target.value, enabled)}
          />
        )}
      </div>
      {desc && <p className="text-[11px] text-muted-foreground">{desc}</p>}
    </div>
  )
}
