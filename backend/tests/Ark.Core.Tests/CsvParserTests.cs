using Ark.Core.Io;
using Xunit;

namespace Ark.Core.Tests;

public class CsvParserTests
{
    [Fact]
    public void ParsesSimpleRows()
    {
        var rows = CsvParser.Parse(new StringReader("a,b,c\n1,2,3\n4,5,6"));
        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "2", "3"], rows[1]);
    }

    [Fact]
    public void ParsesQuotedCellsWithCommaAndNewline()
    {
        var rows = CsvParser.Parse(new StringReader("name,memo\n\"张三\",\"你好,世界\"\n\"李四\",\"两行\n内容\""));
        Assert.Equal(3, rows.Count);
        Assert.Equal("你好,世界", rows[1][1]);
        Assert.Equal("两行\n内容", rows[2][1]);
    }

    [Fact]
    public void ParsesEscapedQuotes()
    {
        var rows = CsvParser.Parse(new StringReader("v\n\"he said \"\"hi\"\"\""));
        Assert.Equal("he said \"hi\"", rows[1][0]);
    }

    [Fact]
    public void TrailingNewlineDoesNotCreateEmptyRow()
    {
        var rows = CsvParser.Parse(new StringReader("a,b\n1,2\n"));
        Assert.Equal(2, rows.Count);
    }
}
