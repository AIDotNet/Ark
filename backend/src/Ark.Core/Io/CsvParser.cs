using Ark.Core.Errors;

namespace Ark.Core.Io;

/// <summary>极简 RFC4180 CSV 解析器（引号、转义引号、跨行单元格）。</summary>
public static class CsvParser
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(TextReader reader)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;
        var cellStarted = false;

        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            var c = (char)ch;
            if (inQuotes)
            {
                if (c == '"')
                {
                    var next = reader.Peek();
                    if (next == '"') { cell.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else cell.Append(c);
                cellStarted = true;
                continue;
            }
            switch (c)
            {
                case '"':
                    inQuotes = true;
                    cellStarted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    cellStarted = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = [];
                    cellStarted = false;
                    break;
                default:
                    cell.Append(c);
                    cellStarted = true;
                    break;
            }
        }
        if (cellStarted || cell.Length > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        if (inQuotes) throw ArkException.Validation("CSV 格式错误：引号未闭合");
        return rows;
    }
}
