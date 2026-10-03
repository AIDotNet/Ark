using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Sync;

namespace Ark.Providers.Abstractions;

/// <summary>方言 DDL 生成器：Canonical 模型 → 目标方言语句。</summary>
public interface IDdlGenerator
{
    ArkDialect Dialect { get; }

    string Quote(string identifier);

    /// <summary>带库名/schema 限定的表名。</summary>
    string TableName(TableRef t);

    IReadOnlyList<string> CreateTable(CanonicalTable t, List<string> warnings);

    /// <summary>带 schema/库名限定的建表（同步用）。默认退化为无限定实现。</summary>
    IReadOnlyList<string> CreateTable(TableRef target, CanonicalTable t, List<string> warnings) =>
        CreateTable(t, warnings);

    /// <summary>由结构差异生成 ALTER 语句；不支持的变更以 warning 表达（如 SQLite 改列类型）。</summary>
    IReadOnlyList<string> AlterTable(TableDiff diff, List<string> warnings);

    IReadOnlyList<string> CreateIndex(CanonicalIndex idx, TableRef t, CanonicalTable? table = null);
    IReadOnlyList<string> DropIndex(string indexName, TableRef t);
    IReadOnlyList<string> DropTable(TableRef t);

    /// <summary>对已存在的表追加外键（SQLite 不支持 → 返回空并由调用方给出警告）。</summary>
    IReadOnlyList<string> CreateForeignKey(CanonicalForeignKey fk, TableRef t);
    IReadOnlyList<string> DropForeignKey(string fkName, TableRef t);

    IReadOnlyList<string> TruncateTable(TableRef t);

    /// <summary>同步完成后把自增/序列推进到指定值；方言不支持则返回空。</summary>
    IReadOnlyList<string> ResetAutoIncrement(TableRef t, string pkColumn, long value);
}
