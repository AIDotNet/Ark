import { EditorView } from "codemirror"
import { HighlightStyle, syntaxHighlighting } from "@codemirror/language"
import { tags as t } from "@lezer/highlight"
import type { Extension } from "@codemirror/state"

/** 深浅两套 CodeMirror 主题：背景透明融入面板，配色与 Tailwind 语义色一致 */

const lightHighlight = HighlightStyle.define([
  { tag: t.keyword, color: "#7c3aed" },
  { tag: [t.string, t.special(t.string)], color: "#15803d" },
  { tag: [t.number, t.bool, t.null], color: "#c2410c" },
  { tag: [t.comment, t.lineComment, t.blockComment], color: "#94a3b8", fontStyle: "italic" },
  { tag: [t.typeName, t.className], color: "#0e7490" },
  { tag: [t.operator, t.operatorKeyword], color: "#475569" },
  { tag: [t.punctuation, t.separator], color: "#64748b" },
  { tag: [t.meta, t.processingInstruction], color: "#b45309" },
  { tag: t.invalid, color: "#dc2626" },
])

const darkHighlight = HighlightStyle.define([
  { tag: t.keyword, color: "#c4b5fd" },
  { tag: [t.string, t.special(t.string)], color: "#86efac" },
  { tag: [t.number, t.bool, t.null], color: "#fdba74" },
  { tag: [t.comment, t.lineComment, t.blockComment], color: "#64748b", fontStyle: "italic" },
  { tag: [t.typeName, t.className], color: "#67e8f9" },
  { tag: [t.operator, t.operatorKeyword], color: "#94a3b8" },
  { tag: [t.punctuation, t.separator], color: "#94a3b8" },
  { tag: [t.meta, t.processingInstruction], color: "#fbbf24" },
  { tag: t.invalid, color: "#f87171" },
])

function baseTheme(dark: boolean): Extension {
  return EditorView.theme(
    {
      "&": { fontSize: "13px", height: "100%", backgroundColor: "transparent" },
      ".cm-scroller": {
        overflow: "auto",
        fontFamily: "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace",
        lineHeight: "1.7",
        padding: "4px 0",
      },
      ".cm-content": { caretColor: dark ? "#a5b4fc" : "#4f46e5" },
      ".cm-cursor, .cm-dropCursor": { borderLeftColor: dark ? "#a5b4fc" : "#4f46e5", borderLeftWidth: "2px" },
      "&.cm-focused": { outline: "none" },
      "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, &.cm-focused .cm-content ::selection":
        { backgroundColor: dark ? "#3b82f640" : "#3b82f61f" },
      ".cm-activeLine": { backgroundColor: dark ? "#ffffff08" : "#0f172a08" },
      ".cm-current-stmt": { backgroundColor: dark ? "#8b5cf614" : "#7c3aed0d" },
      ".cm-gutters": {
        backgroundColor: "transparent",
        color: dark ? "#475569" : "#94a3b8",
        border: "none",
        paddingRight: "4px",
      },
      ".cm-activeLineGutter": { backgroundColor: dark ? "#ffffff0d" : "#0f172a0d", color: dark ? "#94a3b8" : "#475569" },
      ".cm-selectionMatch": { backgroundColor: dark ? "#3b82f633" : "#3b82f626" },
      ".cm-tooltip": {
        border: `1px solid ${dark ? "#334155" : "#e2e8f0"}`,
        backgroundColor: dark ? "#1e293b" : "#ffffff",
        color: dark ? "#e2e8f0" : "#0f172a",
      },
      ".cm-tooltip-autocomplete ul li[aria-selected]": { backgroundColor: dark ? "#334155" : "#e0e7ff" },
    },
    { dark },
  )
}

export function cmTheme(dark: boolean): Extension[] {
  return [baseTheme(dark), syntaxHighlighting(dark ? darkHighlight : lightHighlight)]
}
