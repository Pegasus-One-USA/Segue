namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// A source's FHIR endpoint is not answering, so no token was requested (or a token request failed while the
/// endpoint was unreachable). Thrown by <c>PreflightFhirAccessTokenProvider</c>.
/// <para>
/// This exists so the outage case has its own type rather than being another string inside a generic exception —
/// the mistake that made "Epic is down" and "your client ID is wrong" indistinguishable in the first place.
/// </para>
/// <para>
/// <see cref="FHIRBridgeException.UserMessage"/> deliberately states what is NOT wrong as well as what is: an
/// operator who sees a token failure will otherwise start re-checking credentials that were never the problem.
/// </para>
/// </summary>
public sealed class SourceUnavailableException : FHIRBridgeException
{
    public SourceUnavailableException(string sourceName, string detail, Exception? innerException = null)
        : base(
            BuildMessage(sourceName, detail),
            $"The source '{sourceName}' is not reachable right now, so this run could not authenticate with it. " +
            "This is a connectivity problem or an outage at the source — the credentials on this connection are " +
            "not the cause. Try again shortly.",
            innerException)
    {
        SourceName = sourceName;
        Detail = detail;
    }

    /// <summary>The source connection's display name, for logs and the run's error list.</summary>
    public string SourceName { get; }

    /// <summary>
    /// The probe's own observation ("connection refused", "HTTP 503"). Transport-level or a status number only —
    /// never an upstream response body — so it is safe in logs and in a client-facing sentence.
    /// </summary>
    public string Detail { get; }

    private static string BuildMessage(string sourceName, string detail) =>
        $"Source '{sourceName}' is not reachable: {detail}";
}
