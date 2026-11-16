using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Authorization;

public sealed class WorkspaceAccessService(TaslimDbContext db)
{
    public Task<bool> IsMemberAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        db.WorkspaceMembers.AnyAsync(member => member.UserId == userId && member.WorkspaceId == workspaceId, cancellationToken);

    public Task<WorkspaceMember?> GetMembershipAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        db.WorkspaceMembers.AsNoTracking().FirstOrDefaultAsync(
            member => member.UserId == userId && member.WorkspaceId == workspaceId, cancellationToken);
}
