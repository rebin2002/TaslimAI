using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Taslim.Api.AcademicEvidence;
using Taslim.Api.AcademicIntegration.Persistence;
using Taslim.Api.AcademicWorkflow;
using Taslim.Api.Persistence;

namespace Taslim.Api.AcademicIntegration;

/// <summary>
/// Composition seam for the academic integration lane.
///
/// This is the commit the Phase B lane documented it was waiting for: it calls
/// <c>AddAcademicEvidence()</c> and <c>AddAcademicEvidencePersistence()</c>, registers
/// the durable-state store over the shared context, builds the Phase C orchestrator per
/// scope over the <em>registered</em> stage handlers, and wires the projection bridge.
/// An explicit API opt-in registers the deterministic Plan stage. Discovery, screening, extraction,
/// synthesis, drafting and verification remain unregistered and are reported as the exact
/// missing stage; the Plan handler does not call an external service or create a charge.
/// </summary>
public static class AcademicIntegrationServiceCollectionExtensions
{
    /// <summary>Register the integration lane. Safe to call more than once.</summary>
    public static IServiceCollection AddAcademicProjects(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The Phase B extensions resolve a bare DbContext against the shared context.
        services.TryAddScoped<DbContext>(provider => provider.GetRequiredService<TaslimDbContext>());

        services.AddAcademicEvidence();
        services.AddAcademicEvidencePersistence();
        if (string.Equals(configuration[AcademicWorkflowOptions.PlanEnabledKey], "true", StringComparison.OrdinalIgnoreCase))
            services.AddAcademicStageHandler<AcademicPlanStageHandler>();

        services.Configure<AcademicWorkflowEngineOptions>(configuration.GetSection(AcademicWorkflowOptions.SectionName));

        // One in-flight evidence projection per project inside this process; the
        // integration service fences its combined-tree writes behind this gate.
        services.TryAddSingleton<AcademicProjectionGate>();

        services.AddScoped<EfAcademicWorkflowStateStore>();
        services.TryAddScoped<IAcademicWorkflowStateStore>(provider =>
            provider.GetRequiredService<EfAcademicWorkflowStateStore>());
        services.AddScoped<AcademicEvidenceProjector>();
        services.AddScoped<AcademicProjectsService>();
        services.AddScoped(provider => new AcademicWorkflowOrchestrator(
            provider.GetRequiredService<IAcademicWorkflowStateStore>(),
            provider.GetServices<IAcademicStageHandler>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<AcademicWorkflowEngineOptions>>().Value));

        return services;
    }

    /// <summary>
    /// Register one implementation for a stage. The Plan slice is local and deterministic;
    /// the remaining stage handlers are added only with independently reviewed capabilities.
    /// </summary>
    public static IServiceCollection AddAcademicStageHandler<THandler>(this IServiceCollection services)
        where THandler : class, IAcademicStageHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAcademicStageHandler, THandler>());
        return services;
    }
}

/// <summary>Config section name bound into <see cref="AcademicWorkflowEngineOptions"/>.</summary>
public static class AcademicWorkflowOptions
{
    public const string SectionName = "AcademicWorkflow";
    public const string PlanEnabledKey = SectionName + ":PlanEnabled";
}
