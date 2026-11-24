using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Images;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The generation-job queue accepts a status filter and an offset page window. Both are
/// validated at the controller boundary, so a value that cannot name a real queue state has
/// to fail closed instead of being served as a successful-but-empty page.
/// Every fixture here is synthetic: a recording service stand-in replaces the queue store, so
/// no provider, network call, or real job is involved.
/// </summary>
public sealed class GenerationJobQueueStatusFilterTests
{
    private sealed class RecordingJobService : IGenerationJobService
    {
        public GenerationJobFilter? SeenFilter { get; private set; }
        public bool Reached { get; private set; }

        public Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default)
        {
            Reached = true;
            SeenFilter = filter;
            return Task.FromResult<GenerationJobListDto?>(new GenerationJobListDto(
                Array.Empty<GenerationJobDto>(), filter.Page, filter.PageSize, TotalCount: 0, TotalPages: 0));
        }

        public Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null, Guid? retryOfJobId = null) => throw new NotSupportedException();
        public Task<GenerationJob> CreatePendingMovieProductionAsync(Guid userId, CreateGenerationJobRequest request, string idempotencyKey, CancellationToken cancellationToken = default, string? requestId = null) => throw new NotSupportedException();
        public Task<GenerationJob?> PublishPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AbortPendingMovieProductionAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null) => throw new NotSupportedException();
        public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static (GenerationJobsController Controller, RecordingJobService Service) Build(Guid userId)
    {
        var service = new RecordingJobService();
        var controller = new GenerationJobsController(service, Options.Create(new ImageGenerationOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                },
            },
        };
        return (controller, service);
    }

    private static readonly Guid WorkspaceId = Guid.NewGuid();

    [Theory]
    // Enum.TryParse accepts the underlying numeric form, so these all used to be served as a
    // successful page. "99"/"-1"/"07" became undefined members that match no stored row, which
    // made an invalid filter indistinguishable from an empty queue, and "4" silently meant Failed.
    [InlineData("4")]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("07")]
    [InlineData("  3  ")]
    [InlineData("NotAStatus")]
    [InlineData("Runing")]
    public async Task Numeric_and_unknown_status_values_are_rejected_before_the_queue_is_read(string status)
    {
        var (controller, service) = Build(Guid.NewGuid());

        var result = await controller.List(WorkspaceId, page: 1, pageSize: 20, status, projectId: null, jobType: null, CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, error.StatusCode);
        // The queue must not be consulted for a filter that cannot name a real status.
        Assert.False(service.Reached);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("succeeded")]
    [InlineData("FAILED")]
    [InlineData("Pending")]
    [InlineData("queued")]
    [InlineData("Cancelled")]
    [InlineData(" Running ")]
    public async Task Defined_status_names_reach_the_queue_as_the_matching_filter(string status)
    {
        var (controller, service) = Build(Guid.NewGuid());

        var result = await controller.List(WorkspaceId, page: 2, pageSize: 25, status, projectId: null, jobType: null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(service.Reached);
        Assert.Equal(status.Trim().ToLowerInvariant(), service.SeenFilter!.Status!.Value.ToString().ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Absent_status_keeps_the_queue_unfiltered(string? status)
    {
        var (controller, service) = Build(Guid.NewGuid());

        var result = await controller.List(WorkspaceId, page: 1, pageSize: 20, status, projectId: null, jobType: null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(service.SeenFilter!.Status);
    }

    [Fact]
    public async Task Page_window_is_passed_through_for_the_shared_pagination_bounds_to_normalize()
    {
        var (controller, service) = Build(Guid.NewGuid());

        // Out-of-range paging is a normalization concern, not a validation error: the request
        // still reads the queue, and ApiPagination owns clamping it to the safe window.
        var result = await controller.List(WorkspaceId, page: -5, pageSize: 5_000, "Succeeded", projectId: null, jobType: null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(-5, service.SeenFilter!.Page);
        Assert.Equal(5_000, service.SeenFilter.PageSize);
        Assert.Equal(GenerationJobStatus.Succeeded, service.SeenFilter.Status);
    }

    [Fact]
    public async Task Project_and_job_type_narrowing_survive_a_valid_status_filter()
    {
        var (controller, service) = Build(Guid.NewGuid());
        var projectId = Guid.NewGuid();

        var result = await controller.List(WorkspaceId, page: 3, pageSize: 10, "Queued", projectId, "system.test", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(GenerationJobStatus.Queued, service.SeenFilter!.Status);
        Assert.Equal(projectId, service.SeenFilter.ProjectId);
        Assert.Equal("system.test", service.SeenFilter.JobType);
        Assert.Equal(3, service.SeenFilter.Page);
        Assert.Equal(10, service.SeenFilter.PageSize);
    }

    [Fact]
    public async Task An_empty_status_filter_still_reports_the_requested_page_window()
    {
        var (controller, service) = Build(Guid.NewGuid());

        var result = await controller.List(WorkspaceId, page: 7, pageSize: 40, status: null, projectId: null, jobType: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<GenerationJobListDto>(ok.Value);
        Assert.Equal(7, page.Page);
        Assert.Equal(40, page.PageSize);
        Assert.Empty(page.Items);
        Assert.True(service.Reached);
    }
}
