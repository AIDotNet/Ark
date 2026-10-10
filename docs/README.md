# Ark 文档站

基于 [Fumadocs](https://fumadocs.dev)（Next.js App Router）构建的 Ark 文档网站。

## 本地运行

```bash
npm install
npm run dev        # http://localhost:3000
npm run build && npm start
```

Docker 独立镜像：`docker build -t ark-docs:local .`（详见 [deployment 文档](/docs/deployment)）

## 写文档

- 内容：`content/docs/**/*.mdx`，frontmatter 支持 `title`（必填）/ `description` / `full` / `icon`
- 侧边栏顺序：各目录的 `meta.json`（`pages` 数组）
- 图片：放 `public/images/`，正文用 `/images/xxx.png` 引用
- 内部链接：使用路由路径（如 `/docs/tutorial/data-sync`）
- 组件：`Cards` / `Card` / `Callout` / `Tabs` / `Steps` / `TypeTable` 等可直接在 MDX 中使用
- 搜索：Orama 全文 + 中文分词（`@orama/tokenizers/mandarin`）
- LLM 友好输出：`/llms.txt`、`/llms-full.txt`、`/llms.mdx/docs/<path>/content.md`

## 目录

| 路径 | 说明 |
|---|---|
| `content/docs/` | 全部文档源文件（16 篇） |
| `app/(home)/` | 文档站首页 |
| `app/docs/` | 文档布局与页面 |
| `app/api/search/` | 搜索 API |
| `lib/source.ts` | 内容源装配（loader） |
| `public/images/` | 教程截图（28 张） |
