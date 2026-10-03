/** SQL 语句区间（相对整篇文档的偏移，供 CodeMirror 执行/高亮定位使用） */
export interface StatementRange {
  from: number
  to: number
}

/**
 * 扫描 SQL 文本中的语句边界（分号分隔），返回非空语句区间（升序）。
 * 处理：`--` 行注释、块注释、单/双引号字符串（含转义）、
 * 反引号标识符、PostgreSQL dollar-quoted 字符串（$$ 或 $tag$ 包裹）。
 */
export function scanStatements(text: string): StatementRange[] {
  const out: StatementRange[] = []
  const n = text.length
  let i = 0
  let start = -1
  let end = -1
  const begin = (pos: number) => {
    if (start < 0) start = pos
  }
  const touch = (pos: number) => {
    end = pos
  }
  while (i < n) {
    const c = text[i]!
    // 行注释：吞到行尾
    if (c === "-" && text[i + 1] === "-") {
      while (i < n && text[i] !== "\n") i++
      continue
    }
    // 块注释：吞到闭合
    if (c === "/" && text[i + 1] === "*") {
      i += 2
      while (i < n && !(text[i] === "*" && text[i + 1] === "/")) i++
      i = Math.min(n, i + 2)
      continue
    }
    // PG dollar-quoted 字符串（$$ 或 $tag$）
    const dollar = c === "$" ? /^\$[A-Za-z_0-9]*\$/.exec(text.slice(i, i + 64)) : null
    if (dollar) {
      begin(i)
      const tag = dollar[0]
      const close = text.indexOf(tag, i + tag.length)
      const stop = close === -1 ? n : close + tag.length
      touch(stop - 1)
      i = stop
      continue
    }
    // 字符串 / 引用标识符
    if (c === "'" || c === '"' || c === "`") {
      begin(i)
      i++
      while (i < n) {
        const d = text[i]!
        if (d === "\\" && c !== "`") {
          i += 2
          continue
        }
        if (d === c) {
          if (c === "'" && text[i + 1] === "'") {
            i += 2
            continue
          }
          i++
          break
        }
        i++
      }
      touch(Math.min(i, n) - 1)
      continue
    }
    // 语句分隔符
    if (c === ";") {
      if (start >= 0 && end >= start) out.push({ from: start, to: end + 1 })
      start = -1
      end = -1
      i++
      continue
    }
    if (!/\s/.test(c)) {
      begin(i)
      touch(i)
    }
    i++
  }
  if (start >= 0 && end >= start) out.push({ from: start, to: end + 1 })
  return out
}

/**
 * 光标所在语句：优先取包含 pos 的语句；pos 在语句间空隙时取下一条；没有下一条时取最后一条。
 */
export function statementAt(text: string, pos: number): StatementRange | null {
  const list = scanStatements(text)
  if (list.length === 0) return null
  const containing = list.find((s) => pos >= s.from && pos <= s.to)
  if (containing) return containing
  return list.find((s) => s.from >= pos) ?? list[list.length - 1]!
}
