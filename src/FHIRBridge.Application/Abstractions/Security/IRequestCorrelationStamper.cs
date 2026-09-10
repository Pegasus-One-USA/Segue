namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Re-stamps the in-flight request's correlation id once a workflow id and interactive session id become known
/// from something other than the URL — specifically <c>/oauth/callback</c>, whose only inputs are an OAuth
/// <c>code</c> and an encrypted <c>state</c>, so nothing correlatable is visible until that state is decrypted.
/// <para>Exists as an abstraction purely so <c>InteractiveSourceAuthorizationService</c> (Infrastructure) can do
/// this without referencing the Api layer that owns the HTTP pipeline. Hosts with no HTTP request in flight (the
/// Worker) get a no-op implementation, and a caller-supplied <c>X-Correlation-Id</c> is always left alone — see
/// <see cref="WorkflowCorrelationId"/> for the derivation and why it is not demanded from the caller.</para>
/// </summary>
public interface IRequestCorrelationStamper
{
    /// <summary>Derives an id from the workflow and session — the fallback for callers that never called
    /// validate-run, so their OAuth leg still correlates with the rest of the attempt.</summary>
    void Stamp(Guid workflowId, string? sessionId);

    /// <summary>Uses an already-minted, attempt-scoped id verbatim — the primary path, for an attempt started by
    /// <c>validate-run</c>, whose id travelled here inside the encrypted launch context.</summary>
    void StampExplicit(string correlationId);
}

/// <summary>Used wherever there is no HTTP request to stamp (the Worker host, tests).</summary>
public sealed class NullRequestCorrelationStamper : IRequestCorrelationStamper
{
    public void Stamp(Guid workflowId, string? sessionId)
    {
    }

    public void StampExplicit(string correlationId)
    {
    }
}
