using System.Text;
using FluentAssertions;
using Taxation.StatementParser.Console.Web;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class MultipartFormParserTests
{
    private const string Boundary = "----TestBoundary123";

    [Fact]
    public void Parse_ShouldExtractTextFieldAndFile()
    {
        byte[] fileBytes = [0x25, 0x50, 0x44, 0x46]; // "%PDF"
        byte[] body = BuildMultipart(
            ("columns", "Date, Description, Balance"),
            fileBytes);

        MultipartForm form = MultipartFormParser.Parse(body, $"multipart/form-data; boundary={Boundary}");

        form.Fields.Should().ContainKey("columns");
        form.Fields["columns"].Should().Be("Date, Description, Balance");
        form.FileName.Should().Be("statement.pdf");
        form.FileContent.Should().Equal(fileBytes);
    }

    [Fact]
    public void Parse_WithNoBoundary_ShouldReturnEmptyForm()
    {
        MultipartForm form = MultipartFormParser.Parse([1, 2, 3], "multipart/form-data");

        form.Fields.Should().BeEmpty();
        form.FileContent.Should().BeNull();
    }

    [Fact]
    public void Parse_WithQuotedBoundary_ShouldStillParse()
    {
        byte[] body = BuildMultipart(("columns", "A, B"), [0x01]);

        MultipartForm form = MultipartFormParser.Parse(body, $"multipart/form-data; boundary=\"{Boundary}\"");

        form.Fields["columns"].Should().Be("A, B");
        form.FileName.Should().Be("statement.pdf");
    }

    private static byte[] BuildMultipart((string Name, string Value) field, byte[] fileBytes)
    {
        using var ms = new MemoryStream();
        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        WriteAscii($"--{Boundary}\r\n");
        WriteAscii($"Content-Disposition: form-data; name=\"{field.Name}\"\r\n\r\n");
        WriteAscii($"{field.Value}\r\n");

        WriteAscii($"--{Boundary}\r\n");
        WriteAscii("Content-Disposition: form-data; name=\"file\"; filename=\"statement.pdf\"\r\n");
        WriteAscii("Content-Type: application/pdf\r\n\r\n");
        ms.Write(fileBytes);
        WriteAscii("\r\n");

        WriteAscii($"--{Boundary}--\r\n");
        return ms.ToArray();
    }
}
