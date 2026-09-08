using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Taxation.StatementParser.Console.Processing;

namespace Taxation.StatementParser.Console.Web;

/// <summary>
/// A minimal, dependency-free local web server (built on <see cref="HttpListener"/>) that serves the
/// single-page UI and exposes:
/// <list type="bullet">
///   <item><c>GET /favicon.svg</c> &mdash; the browser tab icon.</item>
///   <item><c>POST /api/process</c> &mdash; accepts a multipart upload (PDF + columns), runs the
///   parsing pipeline, and streams the generated Excel workbook back as a download.</item>
/// </list>
/// The user selects the file with the browser's own file picker, so no native dialog is involved.
/// The default browser is launched automatically at startup.
/// </summary>
public sealed class StatementWebServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly StatementProcessingService _service = new();

    public int Run()
    {
        string prefix = ResolvePrefix(out string url);

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            System.Console.WriteLine($"Could not start the local web server: {ex.Message}");
            return 1;
        }

        System.Console.WriteLine("========================================================");
        System.Console.WriteLine("  Bank Statement PDF -> Excel Parser (Web UI)");
        System.Console.WriteLine("========================================================");
        System.Console.WriteLine($"UI running at {url}");
        System.Console.WriteLine("Your browser should open automatically. Close this window to stop.");

        OpenBrowser(url);

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException)
            {
                break;
            }

            HandleRequest(context);
        }

        return 0;
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            switch (path)
            {
                case "/" when method == "GET":
                    WriteHtml(context, WebUiPage.Html);
                    break;

                case "/favicon.svg" when method == "GET":
                    WriteFavicon(context);
                    break;

                case "/api/process" when method == "POST":
                    HandleProcess(context);
                    break;

                default:
                    context.Response.StatusCode = 404;
                    WriteJson(context, new { error = "Not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                context.Response.StatusCode = 500;
                WriteJson(context, new { error = ex.Message });
            }
            catch
            {
                // Client likely disconnected; nothing further we can do.
            }
        }
    }

    private void HandleProcess(HttpListenerContext context)
    {
        string contentType = context.Request.ContentType ?? string.Empty;
        if (!contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            WriteJson(context, new { ok = false, error = "Expected a file upload." });
            return;
        }

        byte[] body = ReadAllBytes(context.Request.InputStream);
        MultipartForm form = MultipartFormParser.Parse(body, contentType);

        if (form.FileContent is null || form.FileContent.Length == 0 || string.IsNullOrWhiteSpace(form.FileName))
        {
            WriteJson(context, new { ok = false, error = "Please choose a PDF or scanned image file." });
            return;
        }

        if (!StatementProcessingService.SupportedExtensions.Contains(
                Path.GetExtension(form.FileName), StringComparer.OrdinalIgnoreCase))
        {
            WriteJson(context, new
            {
                ok = false,
                error = "Unsupported file type. Choose a PDF or a scanned image (PNG, JPG, JPEG, TIF, TIFF, BMP).",
            });
            return;
        }

        // Persist the upload to a private temp folder so the pipeline (and its output naming based on
        // the original file name) works unchanged. The folder is cleaned up afterwards.
        string workDir = Path.Combine(Path.GetTempPath(), "StatementParser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string safeName = Path.GetFileName(form.FileName);
        string pdfPath = Path.Combine(workDir, safeName);

        try
        {
            File.WriteAllBytes(pdfPath, form.FileContent);

            // Optional password for encrypted (password protected) PDFs.
            form.Fields.TryGetValue("password", out string? password);

            // Columns are detected automatically from the statement header.
            StatementProcessingResult result = _service.Process(pdfPath, log: null, password: password);

            if (!result.Ok || result.OutputPath is null || !File.Exists(result.OutputPath))
            {
                WriteJson(context, new { ok = false, error = result.Error ?? "Processing failed." });
                return;
            }

            byte[] workbook = File.ReadAllBytes(result.OutputPath);
            string downloadName = Path.GetFileName(result.OutputPath);
            WriteFileDownload(context, workbook, downloadName, result.TransactionCount, result.GroupCount);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static string ResolvePrefix(out string url)
    {
        int port = FindFreePort();
        url = $"http://localhost:{port}/";
        return url;
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            System.Console.WriteLine($"Please open your browser and navigate to {url}");
        }
    }

    private static void WriteHtml(HttpListenerContext context, string html)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
    }

    private static void WriteFavicon(HttpListenerContext context)
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.svg");
        if (!File.Exists(iconPath))
        {
            context.Response.StatusCode = 404;
            context.Response.OutputStream.Close();
            return;
        }

        byte[] buffer = File.ReadAllBytes(iconPath);
        context.Response.ContentType = "image/svg+xml";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
    }

    private static void WriteFileDownload(
        HttpListenerContext context,
        byte[] content,
        string fileName,
        int transactionCount,
        int groupCount)
    {
        context.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileName}\"");
        // Surface useful metadata to the page without a second request.
        context.Response.AddHeader("X-Transaction-Count", transactionCount.ToString());
        context.Response.AddHeader("X-Group-Count", groupCount.ToString());
        context.Response.ContentLength64 = content.Length;
        context.Response.OutputStream.Write(content, 0, content.Length);
        context.Response.OutputStream.Close();
    }

    private static void WriteJson(HttpListenerContext context, object payload)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of a temp folder; safe to ignore.
        }
    }
}
