using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentGenerationKillSwitchTests
{
    [Fact]
    public async Task Disabled_handler_stops_before_deserializing_or_reaching_provider_work()
    {
        var handler = new DocumentGenerationJobHandler(
            null!,
            null!,
            null!,
            null!,
            Options.Create(new DocumentGenerationOptions { Enabled = false }));

        var exception = await Assert.ThrowsAsync<DocumentGenerationStageException>(() => handler.ExecuteAsync(
            new GenerationJob { JobType = GenerationJobTypes.DocumentGenerate, InputJson = "not-json" },
            new Progress<int>(),
            CancellationToken.None));

        Assert.Equal(DocumentGenerationStages.Validation, exception.Stage);
        Assert.Equal("DOCUMENT_STUDIO_UNAVAILABLE", exception.Code);
        Assert.Equal("Document generation is not available right now.", exception.Message);
    }
}
