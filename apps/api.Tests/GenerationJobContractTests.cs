using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationJobContractTests
{
    [Fact]
    public void Public_job_dto_replaces_persisted_internal_error_text_with_safe_message()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            JobType = GenerationJobTypes.SocialGenerate,
            Status = GenerationJobStatus.Failed,
            ErrorCode = GenerationJobErrorCodes.SocialProviderUnavailable,
            ErrorMessage = "provider=https://internal.example; secret-provider-detail",
            CreatedAt = DateTime.UtcNow,
        };

        var dto = GenerationJobContractMapper.ToDto(job);

        Assert.Equal("Social content generation is temporarily unavailable. Please try again later.", dto.ErrorMessage);
        Assert.DoesNotContain("internal.example", dto.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-provider-detail", dto.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GenerationJobErrorCodes.SocialProviderUnavailable, dto.ErrorCode);
    }

    [Fact]
    public void Public_job_dto_uses_generic_safe_message_for_unknown_error_codes()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            JobType = GenerationJobTypes.SystemTest,
            Status = GenerationJobStatus.Failed,
            ErrorCode = "UNEXPECTED_INTERNAL_FAILURE",
            ErrorMessage = "SQL connection string and stack trace",
            CreatedAt = DateTime.UtcNow,
        };

        var dto = GenerationJobContractMapper.ToDto(job);

        Assert.Equal("The job could not be completed.", dto.ErrorMessage);
        Assert.DoesNotContain("SQL", dto.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
