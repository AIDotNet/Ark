import { Component, type ErrorInfo, type ReactNode } from "react"
import { AlertTriangle, RotateCcw } from "lucide-react"
import { Button } from "@/components/ui/button"

interface ErrorBoundaryProps {
  children: ReactNode
  /** 出错卡片顶部显示的名称（如标签页标题），便于定位 */
  label?: string
}

interface ErrorBoundaryState {
  error: Error | null
}

/**
 * 最小 ErrorBoundary：捕获子树渲染错误，显示友好错误卡 + 重试按钮，
 * 避免单个标签页异常把整个应用卸载成白屏。
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("[ErrorBoundary]", this.props.label ?? "", error, info.componentStack)
  }

  private reset = () => this.setState({ error: null })

  render() {
    if (this.state.error) {
      return (
        <div className="flex min-h-0 flex-1 items-center justify-center p-6">
          <div className="w-full max-w-md rounded-lg border border-destructive/40 bg-card p-5 shadow-sm">
            <div className="flex items-center gap-2 text-destructive">
              <AlertTriangle className="size-5 shrink-0" />
              <span className="text-sm font-medium">
                {this.props.label ? `「${this.props.label}」页面出错` : "页面出错了"}
              </span>
            </div>
            <p className="mt-2 break-all font-mono text-xs text-muted-foreground">
              {this.state.error.message}
            </p>
            <div className="mt-4 flex gap-2">
              <Button size="sm" onClick={this.reset}>
                <RotateCcw /> 重试
              </Button>
              <Button size="sm" variant="outline" onClick={() => window.location.reload()}>
                刷新页面
              </Button>
            </div>
          </div>
        </div>
      )
    }
    return this.props.children
  }
}
