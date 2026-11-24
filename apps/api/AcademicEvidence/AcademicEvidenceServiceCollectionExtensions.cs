using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.AcademicEvidence.Appraisal;

namespace Taslim.Api.AcademicEvidence;

/// <summary>
/// Composition seam for the academic evidence lane.
///
/// These extensions are deliberately not called from <c>Program.cs</c> in this PR.
/// Phase A and Phase C both touch the composition root, and the shared persistence
/// context is on the same list, so the lane ships its registration as an extension
/// the integration commit applies in one line. Keeping it here also means the
/// appraisal layer is usable, and testable, before any of that exists.
/// </summary>
public static class AcademicEvidenceServiceCollectionExtensions
{
    /// <summary>
    /// Register the stateless appraisal services. Safe to call more than once.
    /// </summary>
    public static IServiceCollection AddAcademicEvidence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILiteratureAppraiser, LiteratureAppraiser>();
        return services;
    }

    /// <summary>
    /// Register the store.
    ///
    /// This requires the context it resolves to have been built with
    /// <c>ApplyAcademicEvidenceModel</c>; calling it before the integration commit
    /// does that would produce a runtime failure on first query rather than a silent
    /// no-op, which is why it is separate from
    /// <see cref="AddAcademicEvidence"/>.
    /// </summary>
    public static IServiceCollection AddAcademicEvidencePersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<Persistence.IAcademicEvidenceStore, Persistence.EfAcademicEvidenceStore>();
        return services;
    }
}
