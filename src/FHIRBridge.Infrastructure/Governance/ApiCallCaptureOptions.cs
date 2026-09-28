using System.Net.Http;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// <see cref="HttpRequestOptions"/> keys a caller can set on an outbound <see cref="HttpRequestMessage"/> to have
/// <see cref="ApiRequestLoggingHandler"/> persist that one call's headers/body onto its <c>ApiRequestLogs</c> row —
/// see <see cref="Domain.Entities.Governance.ApiRequestLog"/>'s own doc comment for why this is temporary,
/// troubleshooting-only capture, never turned on by default. A caller sets <see cref="RequestBody"/>/
/// <see cref="RequestHeaders"/>/<see cref="MaskedUrl"/> BEFORE calling <c>SendAsync</c> (it already has the
/// pre-serialized body and can mask its own credential-bearing headers/URL far more precisely than this generic
/// handler could); the handler fills <see cref="ResponseHeaders"/>/<see cref="ResponseBody"/> back onto the SAME
/// request's <see cref="HttpRequestMessage.Options"/> after the call completes — reusing the identical
/// <see cref="HttpRequestMessage"/> reference the caller still holds — so the caller can read its own response
/// detail back out without a second (and, for a non-seekable response stream, potentially empty) content read.
/// </summary>
public static class ApiCallCaptureOptions
{
    public static readonly HttpRequestOptionsKey<bool> Capture = new("FHIRBridge.ApiCallCapture");
    public static readonly HttpRequestOptionsKey<string> MaskedUrl = new("FHIRBridge.ApiCallCapture.MaskedUrl");
    public static readonly HttpRequestOptionsKey<string> RequestHeaders = new("FHIRBridge.ApiCallCapture.RequestHeaders");
    public static readonly HttpRequestOptionsKey<string> RequestBody = new("FHIRBridge.ApiCallCapture.RequestBody");
    public static readonly HttpRequestOptionsKey<string> ResponseHeaders = new("FHIRBridge.ApiCallCapture.ResponseHeaders");
    public static readonly HttpRequestOptionsKey<string> ResponseBody = new("FHIRBridge.ApiCallCapture.ResponseBody");
}
