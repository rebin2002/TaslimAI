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
/// Stage handlers themselves are NOT registered here — the discovery, screening,
/// extraction, synthesis, drafting and verification handlers belong to the provider
/// lanes, which plug in through <see cref="AddAcademicStageHandler{THandler}"/>. With no
/// handler registered the run endpoint answers 501 with the exact missing stage, so the
/// product never reports a stage as functional before its lane is connected.
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

        services.Configure<AcademicWorkflowEngineOptions>(configuration.GetSection(AcademicWorkflowOptions.SectionName));

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
    /// The provider-lane seam: a stage implementation registers itself here and the
    /// integration lane starts running that stage. One handler per stage; the composition
    /// root is the only owner of this list, and CI asserts the production registration is
    /// empty until a lane lands (see AcademicProjectsIntegrationTests).
    /// </summary>
    public static IServiceCollection AddAcademicStageHandler<THandler>(this IServiceCollection services)
        where THandler : class, IAcademicStageHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAcademicStageHandler, THandler>();
        return services;
    }
}

/// <summary>Config section name bound into <see cref="AcademicWorkflowEngineOptions"/>.</summary>
public static class AcademicWorkflowOptions
{
    public const string SectionName = "AcademicWorkflow";
}
