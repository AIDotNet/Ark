using System.Globalization;
using System.Text;
using System.Text.Json;
using Ark.Core.Metadata;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

/// <summary>流式导出：CSV / JSON / SQL dump（建表 + 批量 INSERT）。直接写入输出流。</summary>
public static class Exporter
{
    public static async Task ExportAsync(
        Stream output, string format,
        IDbProvider provider, TableRef table, CanonicalTable meta,
        long? limit, CancellationToken ct = default)
    {
        switch (format.ToLowerInvariant())
        {
            case "csv":
                await ExportCsvAsync(output, provider, table, meta, limit, ct);
                break;
            case "json":
                await ExportJsonAsync(output, provider, table, meta, limit, ct);
                break;
            case "sql":
                await ExportSqlAsync(output, provider, table, meta, limit, ct);
                break;
            default:
                throw new ArgumentException($"不支持的导出格式: {format}");
        }
    }

    private static async IAsyncEnumerable<(object?[] Values, List<string> Names)> ReadBatches(
        IDbProvider provider, TableRef table, CanonicalTable meta, long? limit,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var order = meta.PrimaryKeyColumns.Count > 0
            ? " ORDER BY " + string.Join(", ", meta.PrimaryKeyColumns.Select(provider.Ddl.Quote))
            : "";
        var sql = "SELECT * FROM " + provider.Ddl.TableName(table) + order;
        if (limit is > 0) sql += $" LIMIT {limit}";

        await using var conn = await provider.OpenConnectionAsync(table.Database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var names = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));

        while (await reader.ReadAsync(ct))
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values);
            CellValue.NormalizeRow(values);
            yield return (values, names);
        }
    }

    private static async Task ExportCsvAsync(
        Stream output, IDbProvider provider, TableRef table, CanonicalTable meta, long? limit, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        var headerWritten = false;
        await foreach (var (values, names) in ReadBatches(provider, table, meta, limit, ct))
        {
            if (!headerWritten)
            {
                await writer.WriteLineAsync(string.Join(",", names.Select(CsvCell)));
                headerWritten = true;
            }
            await writer.WriteLineAsync(string.Join(",", values.Select(v => CsvCell(CellText(v)))));
        }
        // 空表（0 行）也要输出表头行：列名取自表元数据，与 SELECT * 的列序一致
        if (!headerWritten)
            await writer.WriteLineAsync(string.Join(",", meta.Columns.Select(c => CsvCell(c.Name))));
        await writer.FlushAsync(ct);
    }

    private static async Task ExportJsonAsync(
        Stream output, IDbProvider provider, TableRef table, CanonicalTable meta, long? limit, CancellationToken ct)
    {
        await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });
        writer.WriteStartArray();
        await foreach (var (values, names) in ReadBatches(provider, table, meta, limit, ct))
        {
            writer.WriteStartObject();
            for (var i = 0; i < names.Count; i++)
            {
                var v = values[i];
                switch (v)
                {
                    case null: writer.WriteNull(names[i]); break;
                    case bool b: writer.WriteBoolean(names[i], b); break;
                    case byte or sbyte or short or ushort or int or uint or long or ulong:
                        writer.WriteNumber(names[i], Convert.ToInt64(v, CultureInfo.InvariantCulture));
                        break;
                    case float or double or decimal:
                        writer.WriteNumber(names[i], Convert.ToDecimal(v, CultureInfo.InvariantCulture));
                        break;
                    case DateTime dt: writer.WriteString(names[i], dt); break;
                    case DateTimeOffset dto: writer.WriteString(names[i], dto); break;
                    case Guid g: writer.WriteString(names[i], g); break;
                    case byte[] bytes: writer.WriteString(names[i], Convert.ToBase64String(bytes)); break;
                    default: writer.WriteString(names[i], v.ToString()); break;
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        await writer.FlushAsync(ct);
    }

    private static async Task ExportSqlAsync(
        Stream output, IDbProvider provider, TableRef table, CanonicalTable meta, long? limit, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        var createSql = await provider.GetCreateTableSqlAsync(table.Database, table.Schema, table.Table, ct);
        if (createSql is not null)
        {
            await writer.WriteLineAsync("-- ----------------------------");
            await writer.WriteLineAsync($"-- Table: {table.Table}");
            await writer.WriteLineAsync("-- ----------------------------");
            await writer.WriteLineAsync(createSql.TrimEnd(';') + ";");
            await writer.WriteLineAsync();
        }

        var buffer = new StringBuilder();
        var count = 0;
        await foreach (var (values, names) in ReadBatches(provider, table, meta, limit, ct))
        {
            var cols = string.Join(", ", names.Select(provider.Ddl.Quote));
            var vals = string.Join(", ", values.Select(v => SqlLiteral(v, provider.Dialect)));
            buffer.Append("INSERT INTO ").Append(provider.Ddl.TableName(table))
                  .Append(" (").Append(cols).Append(") VALUES (").Append(vals).Append(");\n");
            if (++count % 200 == 0)
            {
                await writer.WriteAsync(buffer.ToString());
                buffer.Clear();
            }
        }
        await writer.WriteAsync(buffer.ToString());
        await writer.FlushAsync(ct);
    }

    // ------------------------------ 值格式化 ------------------------------

    private static string CellText(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => v.ToString() ?? "",
    };

    private static string CsvCell(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    private static string SqlLiteral(object? v, ArkDialect dialect) => v switch
    {
        null => "NULL",
        bool b => dialect == ArkDialect.SQLite ? (b ? "1" : "0") : (b ? "TRUE" : "FALSE"),
        byte or sbyte or short or ushort or int or uint or long or ulong =>
            Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        float or double or decimal =>
            Convert.ToString(Convert.ToDouble(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) ?? "0",
        byte[] bytes => dialect == ArkDialect.PostgreSQL
            ? $"decode('{Convert.ToHexString(bytes).ToLowerInvariant()}', 'hex')"
            : $"X'{Convert.ToHexString(bytes).ToLowerInvariant()}'",
        DateTime dt => SqlString(dt.ToString("O", CultureInfo.InvariantCulture), dialect),
        DateTimeOffset dto => SqlString(dto.ToString("O", CultureInfo.InvariantCulture), dialect),
        Guid g => SqlString(g.ToString("D"), dialect),
        _ => SqlString(v.ToString() ?? "", dialect),
    };

    /// <summary>
    /// 字符串字面量。转义方言跟随导出源连接（dump 中的 INSERT 即按源方言书写）：
    /// MySQL 默认 sql_mode 下反斜杠是转义字符，不翻倍会导致回放后数据静默损坏；Pg/SQLite 反斜杠为普通字符。
    /// </summary>
    private static string SqlString(string s, ArkDialect dialect)
    {
        var escaped = dialect == ArkDialect.MySQL ? s.Replace("\\", "\\\\") : s;
        return "'" + escaped.Replace("'", "''") + "'";
    }
}
