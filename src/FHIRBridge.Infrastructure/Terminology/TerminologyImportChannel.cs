using System.Threading.Channels;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Unbounded in-process queue of terminology import jobs (LOINC/SNOMED CT/ICD-10/RxNorm), so the upload
/// or synchronize HTTP endpoint can return immediately instead of the caller — and the portal's global
/// loading overlay — blocking for the entire import's duration. Modeled on
/// <see cref="Messaging.InMemoryMessageChannel{TMessage}"/>, but deliberately kept in-process only (rather
/// than routed through the Worker via MassTransit): the uploaded release file lives on the API host's own
/// disk, and there is no guarantee the Worker process shares that filesystem.
///
/// <see cref="TerminologyImportBackgroundService"/> runs several dequeued jobs concurrently, but there is
/// still exactly one thing READING this channel (that service's own drain loop), which is what
/// <c>SingleReader</c> below asserts — it constrains who may call <c>ReadAsync</c>, not how many jobs may be
/// in flight once read.
/// </summary>
public sealed class TerminologyImportChannel
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel =
        Channel.CreateUnbounded<Func<IServiceProvider, CancellationToken, Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(Func<IServiceProvider, CancellationToken, Task> job) => _channel.Writer.TryWrite(job);

    public ChannelReader<Func<IServiceProvider, CancellationToken, Task>> Reader => _channel.Reader;
}
