using System.Text;

namespace Taxation.StatementParser.Console.Web;

/// <summary>
/// Minimal <c>multipart/form-data</c> parser sufficient for this app's single-file upload form.
/// It extracts text fields and a single uploaded file without any external dependency.
/// </summary>
internal static class MultipartFormParser
{
    public static MultipartForm Parse(byte[] body, string contentType)
    {
        string boundary = ExtractBoundary(contentType);
        if (string.IsNullOrEmpty(boundary))
        {
            return new MultipartForm();
        }

        byte[] delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var form = new MultipartForm();

        int position = 0;
        while (position < body.Length)
        {
            int partStart = IndexOf(body, delimiter, position);
            if (partStart < 0)
            {
                break;
            }

            int headerStart = partStart + delimiter.Length;

            // End marker "--boundary--".
            if (headerStart + 1 < body.Length && body[headerStart] == '-' && body[headerStart + 1] == '-')
            {
                break;
            }

            // Skip the CRLF after the boundary.
            headerStart = SkipCrlf(body, headerStart);

            int headerEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), headerStart);
            if (headerEnd < 0)
            {
                break;
            }

            string headers = Encoding.UTF8.GetString(body, headerStart, headerEnd - headerStart);
            int contentStart = headerEnd + 4;

            int nextBoundary = IndexOf(body, delimiter, contentStart);
            if (nextBoundary < 0)
            {
                break;
            }

            // Content is terminated by a trailing CRLF before the next boundary.
            int contentEnd = nextBoundary;
            if (contentEnd >= 2 && body[contentEnd - 2] == '\r' && body[contentEnd - 1] == '\n')
            {
                contentEnd -= 2;
            }

            ApplyPart(form, headers, body, contentStart, contentEnd - contentStart);
            position = nextBoundary;
        }

        return form;
    }

    private static void ApplyPart(MultipartForm form, string headers, byte[] body, int offset, int length)
    {
        string? name = ExtractHeaderValue(headers, "name");
        if (name is null)
        {
            return;
        }

        string? fileName = ExtractHeaderValue(headers, "filename");
        if (fileName is not null)
        {
            byte[] content = new byte[Math.Max(0, length)];
            if (length > 0)
            {
                Array.Copy(body, offset, content, 0, length);
            }

            form.FileName = fileName;
            form.FileContent = content;
        }
        else
        {
            form.Fields[name] = Encoding.UTF8.GetString(body, offset, Math.Max(0, length));
        }
    }

    private static string ExtractBoundary(string contentType)
    {
        const string marker = "boundary=";
        int index = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        string boundary = contentType[(index + marker.Length)..].Trim();
        return boundary.Trim('"');
    }

    private static string? ExtractHeaderValue(string headers, string key)
    {
        string token = key + "=\"";
        int index = headers.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        int start = index + token.Length;
        int end = headers.IndexOf('"', start);
        return end < 0 ? null : headers[start..end];
    }

    private static int SkipCrlf(byte[] body, int position)
    {
        if (position + 1 < body.Length && body[position] == '\r' && body[position + 1] == '\n')
        {
            return position + 2;
        }

        return position;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Parsed multipart form: text fields plus an optional single uploaded file.</summary>
internal sealed class MultipartForm
{
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? FileName { get; set; }

    public byte[]? FileContent { get; set; }
}
