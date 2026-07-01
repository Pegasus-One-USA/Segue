using System.Text.Json.Serialization;
using FHIRBridge.Api.Workflows;
using FHIRBridge.Api.Security;
using FHIRBridge.Application;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Infrastructure;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Authentication:SigningKey"] = builder.Configuration["Authentication:SigningKey"]
            ?? "StepBase-FHIRBridge-local-development-signing-key-2026-06-22",
        ["LocalAuth:SeedAdmin:Email"] = builder.Configuration["LocalAuth:SeedAdmin:Email"]
            ?? "admin@fhirbridge.local",
        ["LocalAuth:SeedAdmin:Password"] = builder.Configuration["LocalAuth:SeedAdmin:Password"]
            ?? "FHIRBridgeAdmin123!",
        ["LocalAuth:SeedAdmin:DisplayName"] = builder.Configuration["LocalAuth:SeedAdmin:DisplayName"]
            ?? "FHIRBridge Super Admin",
        ["LocalAuth:SeedAdmin:RequirePasswordChange"] = builder.Configuration["LocalAuth:SeedAdmin:RequirePasswordChange"]
            ?? "false"
    });
}

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, HttpContextCurrentUserService>();
builder.Services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddScoped<IAuthorizationHandler, UnifiedAdminAuthorizationHandler>();
builder.Services
    .AddFHIRBridgeApplication()
    .AddFHIRBridgeInfrastructure(builder.Configuration)
    .AddWorkflowCore()
    .AddWorkflowInfrastructure();

builder.Services.AddFhirBridgeAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.UnifiedAdmin, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new UnifiedAdminRequirement());
    });
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("Portal", policy =>
    {
        var origins = builder.Configuration
            .GetSection("Portal:AllowedOrigins")
            .Get<string[]>() ?? ["http://localhost:4200", "https://localhost:4200"];

        policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

SeedLocalIdentity(app);

app.UseCors("Portal");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapWorkflowEndpoints();

app.Run();

static void SeedLocalIdentity(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var seedService = scope.ServiceProvider.GetService<IIdentitySeedService>();
    if (seedService is null)
    {
        return;
    }

    seedService.SeedAsync(CancellationToken.None).GetAwaiter().GetResult();
}
