using System.Reflection;
using FHIRBridge.Runtime.Application.Abstractions.Pipeline;
using FHIRBridge.Runtime.Application.Abstractions.Transformations;
using FHIRBridge.Runtime.Application.Behaviors;
using FHIRBridge.Runtime.Application.Services;
using FHIRBridge.Runtime.Application.Transformations;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Runtime.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRuntimeApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(assembly);
        });

        services.AddValidatorsFromAssembly(assembly);

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));

        services.AddScoped<IResourceTransformer, FhirResourceNormalizer>();
        services.AddScoped<IPipelineOrchestrator, PipelineOrchestrator>();

        return services;
    }
}
