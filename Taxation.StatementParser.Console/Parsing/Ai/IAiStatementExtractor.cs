namespace Taxation.StatementParser.Console.Parsing.Ai;

/// <summary>
/// Abstraction over a fully-offline, on-device AI extractor that turns a bank statement document
/// (scanned image or PDF) into structured transaction rows. Implementations MUST keep all data on
/// the local machine — no statement content may cross the network to any remote host.
/// <para>
/// The abstraction lets the processing pipeline treat AI extraction as an optional, swappable
/// fallback: when the runtime is unavailable the caller keeps the geometric parser result unchanged.
/// </para>
/// </summary>
public interface IAiStatementExtractor
{
    /// <summary>
    /// Returns <c>true</c> when the local AI runtime is installed, reachable and has the configured
    /// model available. Never throws: any failure (service down, model missing, timeout) yields
    /// <c>false</c> so the caller can fall back silently.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts transaction rows from the document at <paramref name="documentPath"/> (PDF or image).
    /// Never throws: on any failure it returns an unsuccessful <see cref="AiExtractionResult"/>.
    /// </summary>
    /// <param name="documentPath">Absolute path to the local PDF or image file.</param>
    /// <param name="password">Optional password for an encrypted PDF.</param>
    Task<AiExtractionResult> ExtractAsync(
        string documentPath,
        string? password = null,
        CancellationToken cancellationToken = default);
}
