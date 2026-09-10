using System.Security.Cryptography;
using System.Text;

namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Derives the one correlation id that every leg of a single workflow attempt shares — the validate-run
/// pre-flight, the token-status check, the interactive EHR sign-in round trip, and the run itself.
/// <para>Deliberately DERIVED rather than handed to the caller and echoed back: the two identifiers it is built
/// from (the workflow id and the caller's opaque session id) are already present on every leg, including the ones
/// a third-party app cannot attach a header to — the browser redirect to the EHR and the EHR's redirect back into
/// <c>/oauth/callback</c>, where the session id travels inside the encrypted launch context. That keeps the
/// third-party integration contract at "call validate-run first" instead of "thread a correlation id through
/// every call and persist it across an OAuth round trip".</para>
/// <para>An explicit <c>X-Correlation-Id</c> header still wins wherever one is supplied (see
/// <c>HttpContextCurrentUserService</c>) — this is the fallback that makes correlation work for callers who send
/// nothing, not a replacement for callers who do.</para>
/// </summary>
public static class WorkflowCorrelationId
{
    /// <summary>Marks a derived id so it is distinguishable at a glance from Kestrel's <c>TraceIdentifier</c>
    /// (<c>0HN…:00000001</c>) and from a caller-supplied header value, in both SQL and Seq.</summary>
    public const string Prefix = "wf-";

    /// <summary>
    /// The stable correlation id for <paramref name="workflowId"/> attempted under <paramref name="sessionId"/>.
    /// Returns null when there is no session id to key on — callers must then keep whatever correlation id they
    /// already have (the ambient request's) rather than inventing one, since a workflow-id-only value would
    /// collide across every concurrent user of the same workflow.
    /// <para>The session id is hashed rather than embedded: it is the key the interactive token cache itself is
    /// partitioned by, and a correlation id is written to governance tables that are read far more widely than
    /// the token cache is.</para>
    /// </summary>
    public static string? Derive(Guid workflowId, string? sessionId)
    {
        if (workflowId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var material = $"{workflowId:N}|{sessionId.Trim()}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        // 12 hex chars (48 bits) of the digest, alongside the workflow id's own first 8 — enough to make a
        // collision between two live sessions of the same workflow implausible, while keeping the value short
        // enough to read out of a log line or paste into Correlation Search by hand.
        return string.Create(
            Prefix.Length + 8 + 1 + 12,
            (workflowId, digest),
            static (span, state) =>
            {
                Prefix.CopyTo(span);
                var cursor = Prefix.Length;
                state.workflowId.ToString("N").AsSpan(0, 8).CopyTo(span[cursor..]);
                cursor += 8;
                span[cursor++] = '-';
                for (var i = 0; i < 6; i++)
                {
                    var b = state.digest[i];
                    span[cursor++] = HexDigit(b >> 4);
                    span[cursor++] = HexDigit(b & 0xF);
                }
            });
    }

    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
}
