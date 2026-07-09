using FHIRBridge.Application;
using FHIRBridge.Infrastructure;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using FHIRBridge.Worker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// DbSecretStore (app-provisioned secrets, e.g. destination connection strings written by the Api's wizard) encrypts
// at rest via Data Protection — a real runtime dependency of the shared configuration/secret-resolution graph, not
// Worker-specific noise. Same application name as the Api host so both processes share one local key ring on a
// single-instance dev machine (see FHIRBridge.Api/Program.cs for the production KeyRingPath guidance).
builder.Services.AddDataProtection().SetApplicationName("FHIRBridge");

builder.Services
    .AddFHIRBridgeApplication()
    .AddFHIRBridgeInfrastructure(builder.Configuration)
    .AddWorkflowCore()
    .AddWorkflowInfrastructure()
    .AddWorkflowSqlPersistence(builder.Configuration);

builder.Services.Configure<RuntimeWorkerOptions>(builder.Configuration.GetSection("RuntimeWorker"));
builder.Services.AddHostedService<Worker>();

// AddFHIRBridgeApplication/Infrastructure register the full application surface (auth, user management, etc.) that
// only the Api host actually wires end-to-end (IDataProtectionProvider, IAccessTokenIssuer, ...). The Worker never
// resolves those services — it only uses IConfigurationRepository/IConfiguredPipelineService/the workflow store — so
// skip Development's default ValidateOnBuild, which otherwise fails the whole host over unrelated, unused services.
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateOnBuild = false,
    ValidateScopes = builder.Environment.IsDevelopment(),
}));

var host = builder.Build();

// Applies pending EF migrations, mirroring the Api host's bootstrap — no-ops on the in-memory path (no
// ConnectionStrings:FHIRBridgeDb configured, so AddFHIRBridgeInfrastructure never registers FHIRBridgeDbContext).
using (var scope = host.Services.CreateScope())
{
    scope.ServiceProvider.GetService<FHIRBridgeDbContext>()?.Database.Migrate();
}

host.Run();
