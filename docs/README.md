# Ark 文档中心

Ark 是一个单机单用户的 Web 版多数据库管理与同步工具（布局参考 Navicat），支持 **PostgreSQL / MySQL / SQLite** 的连接管理、可视化操作，以及**同构 + 异构**的库级/表级同步（仅结构 或 结构+数据），含插件函数自动翻译与目标库扩展依赖检查。

## 文档导航

| 文档 | 内容 |
|---|---|
| [快速开始](./getting-started.md) | 环境要求、启动方式（开发 / Docker）、演示数据准备 |
| [图文教程：连接管理](./tutorial/01-connection-management.md) | 新建/测试/编辑/删除连接，只读模式，密码存储 |
| [图文教程：对象浏览](./tutorial/02-object-browser.md) | 连接树的层级、右键菜单、懒加载行为 |
| [图文教程：数据网格](./tutorial/03-data-grid.md) | 筛选/排序/分页、行内编辑变更集、新增/删除行、无主键只读 |
| [图文教程：表结构设计器](./tutorial/04-table-designer.md) | 可视化列编辑、DDL 预览、索引/外键查看、建表/改表 |
| [图文教程：SQL 控制台](./tutorial/05-query-console.md) | 快捷键、多结果集、历史与收藏、结果导出、AI 助手 |
| [图文教程：数据同步](./tutorial/06-data-sync.md) | 4 步同步向导、先对比再同步、行级 Diff、定时调度、断点续传 |
| [图文教程：导入导出](./tutorial/07-import-export.md) | CSV / JSON / SQL dump 导出，CSV / JSON 导入与自动建表 |
| [图文教程：快捷键与设置](./tutorial/08-shortcuts-and-settings.md) | 全部快捷键、命令面板、AI 助手配置 |
| [系统架构](./architecture.md) | 前后端分层设计、关键机制与技术决策 |
| [API 参考](./api-reference.md) | 全部 REST 端点、统一响应包络、错误码表 |
| [FAQ 与已知边界](./faq.md) | 常见问题、v1 功能边界、故障排查 |

## 教程截图说明

教程中的截图位于 [`tutorial/images/`](./tutorial/images/)，全部来自真实运行界面（浅色主题，1440×900 @2x）。截图使用的演示环境：

- 源库：SQLite 文件 `data/demo/shop.db`（一个含 customers / products / orders / order_items / audit_log 五张表与一个视图的商城示例库）
- 目标库：SQLite 文件 `data/demo/shop_target.db`（同步目标，空库起步）

如需复现：启动系统后参考 [快速开始](./getting-started.md#演示数据) 一节。

## 30 秒了解 Ark 能做什么

1. **管连接**：PostgreSQL / MySQL / SQLite 连接统一管理，密码加密存储，支持只读模式
2. **看数据**：树形浏览库 → schema → 表/视图，双击即开数据网格，筛选/排序/行内编辑
3. **改结构**：可视化表设计器，DDL 先预览再执行，跨方言类型映射与降级警告
4. **写 SQL**：CodeMirror 控制台，⌘↩ 执行，多结果集、执行计划、历史收藏、AI 生成/解释/纠错
5. **同步库**：向导式结构+数据同步（全量复制 / 行级 Diff），破坏性动作显式确认，支持定时调度与断点续传
6. **导数据**：CSV / JSON / SQL dump 流式导出；CSV / JSON 导入，目标表不存在时自动推断建表
