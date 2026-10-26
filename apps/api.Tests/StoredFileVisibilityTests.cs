using Taslim.Api.Domain;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class StoredFileVisibilityTests
{
    [Fact]
    public void Project_file_from_another_workspace_is_not_visible_to_a_workspace_member()
    {
        var fileWorkspaceId = Guid.NewGuid();
        var foreignProjectWorkspaceId = Guid.NewGuid();
        var foreignProjectId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var file = new StoredFile
        {
            WorkspaceId = fileWorkspaceId,
            UserId = Guid.NewGuid(),
            ProjectId = foreignProjectId,
            Project = new Project
            {
                Id = foreignProjectId,
                WorkspaceId = foreignProjectWorkspaceId,
            },
        };

        var visible = StoredFileVisibility.ForWorkspaceMember(memberId).Compile()(file);

        Assert.False(visible);
    }

    [Fact]
    public void Project_file_in_the_same_workspace_remains_visible_to_members()
    {
        var workspaceId = Guid.NewGuid();
        var file = new StoredFile
        {
            WorkspaceId = workspaceId,
            UserId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Project = new Project { WorkspaceId = workspaceId },
        };

        var visible = StoredFileVisibility.ForWorkspaceMember(Guid.NewGuid()).Compile()(file);

        Assert.True(visible);
    }
}
