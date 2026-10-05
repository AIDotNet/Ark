import Link from 'next/link';
import { ArrowRight } from 'lucide-react';

const features = [
  {
    title: '连接管理',
    desc: 'PostgreSQL / MySQL / SQLite 统一管理，密码加密存储，只读模式三层防护',
  },
  {
    title: '数据网格',
    desc: '结构化筛选、排序、分页与行内编辑变更集，无主键表自动只读',
  },
  {
    title: '表结构设计器',
    desc: '可视化列编辑 → DDL 预览 → 执行，跨方言类型映射降级可见',
  },
  {
    title: 'SQL 控制台',
    desc: 'CodeMirror 6，⌘↩ 执行，多结果集、执行计划、历史收藏与 AI 助手',
  },
  {
    title: '结构与数据同步',
    desc: '4 步向导，全量复制 / 行级 Diff，破坏性动作显式确认，断点续传与定时调度',
  },
  {
    title: '导入导出',
    desc: 'CSV / JSON / SQL dump 流式导出；导入自动推断建表',
  },
];

export default function HomePage() {
  return (
    <main className="flex flex-1 flex-col items-center px-6 py-20">
      <section className="max-w-3xl text-center">
        <p className="mb-3 text-sm font-medium text-fd-muted-foreground">
          Ark · 多数据库管理与同步工具
        </p>
        <h1 className="text-4xl font-bold tracking-tight sm:text-5xl">
          像浏览本地文件一样
          <br />
          管理与同步你的数据库
        </h1>
        <p className="mt-5 text-lg text-fd-muted-foreground">
          单机单用户的 Web 版数据库工具，布局参考 Navicat。支持
          PostgreSQL / MySQL / SQLite
          的可视化操作与同构、异构库级同步——插件函数自动翻译，降级永远可见。
        </p>
        <div className="mt-8 flex flex-wrap items-center justify-center gap-3">
          <Link
            href="/docs/getting-started"
            className="inline-flex items-center gap-1 rounded-md bg-fd-primary px-4 py-2 text-sm font-medium text-fd-primary-foreground"
          >
            快速开始
            <ArrowRight className="size-4" />
          </Link>
          <Link
            href="/docs/tutorial/connections"
            className="inline-flex items-center gap-1 rounded-md border px-4 py-2 text-sm font-medium"
          >
            图文教程
          </Link>
          <a
            href="https://github.com/AIDotNet/Ark"
            className="inline-flex items-center gap-1 rounded-md border px-4 py-2 text-sm font-medium"
          >
            GitHub
          </a>
        </div>
      </section>

      <section className="mt-20 grid w-full max-w-5xl grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {features.map((f) => (
          <div
            key={f.title}
            className="rounded-lg border bg-fd-card p-5 transition-colors hover:bg-fd-muted/50"
          >
            <h3 className="font-semibold">{f.title}</h3>
            <p className="mt-2 text-sm text-fd-muted-foreground">{f.desc}</p>
          </div>
        ))}
      </section>

      <section className="mt-16 text-sm text-fd-muted-foreground">
        遇到问题？查看{' '}
        <Link href="/docs/faq" className="font-medium underline">
          FAQ 与已知边界
        </Link>
        ，或了解{' '}
        <Link href="/docs/architecture" className="font-medium underline">
          系统架构设计
        </Link>
        。
      </section>
    </main>
  );
}
