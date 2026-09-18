using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;

namespace Taslim.Api.Persistence;

public sealed class TaslimDbContext(DbContextOptions<TaslimDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.DisplayName).HasMaxLength(120).IsRequired();
            entity.Property(user => user.PreferredLanguage).HasMaxLength(5).IsRequired();
            entity.Property(user => user.CreatedAt).IsRequired();
            entity.Property(user => user.UpdatedAt).IsRequired();
        });

        builder.Entity<Workspace>(entity =>
        {
            entity.HasKey(workspace => workspace.Id);
            entity.Property(workspace => workspace.Name).HasMaxLength(140).IsRequired();
            entity.Property(workspace => workspace.Slug).HasMaxLength(160).IsRequired();
            entity.Property(workspace => workspace.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(workspace => workspace.Slug).IsUnique();
        });

        builder.Entity<WorkspaceMember>(entity =>
        {
            entity.HasKey(member => member.Id);
            entity.Property(member => member.Role).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(member => new { member.WorkspaceId, member.UserId }).IsUnique();
            entity.HasIndex(member => member.UserId);
            entity.HasOne(member => member.Workspace)
                .WithMany(workspace => workspace.Members)
                .HasForeignKey(member => member.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(member => member.User)
                .WithMany(user => user.WorkspaceMemberships)
                .HasForeignKey(member => member.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Project>(entity =>
        {
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Name).HasMaxLength(160).IsRequired();
            entity.Property(project => project.Description).HasMaxLength(2000);
            entity.Property(project => project.Type).HasMaxLength(50).IsRequired();
            entity.Property(project => project.Status).HasMaxLength(30).IsRequired();
            entity.HasIndex(project => project.WorkspaceId);
            entity.HasIndex(project => new { project.WorkspaceId, project.Status });
            entity.HasOne(project => project.Workspace)
                .WithMany(workspace => workspace.Projects)
                .HasForeignKey(project => project.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
