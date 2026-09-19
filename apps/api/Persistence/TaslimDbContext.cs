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
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

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

        builder.Entity<Conversation>(entity =>
        {
            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Title).HasMaxLength(160).IsRequired();
            entity.Property(conversation => conversation.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(conversation => conversation.CreatedAt).IsRequired();
            entity.Property(conversation => conversation.UpdatedAt).IsRequired();
            entity.Property(conversation => conversation.LastMessageAt);
            entity.HasIndex(conversation => new { conversation.UserId, conversation.WorkspaceId, conversation.Status, conversation.UpdatedAt });
            entity.HasIndex(conversation => new { conversation.WorkspaceId, conversation.UpdatedAt });
            entity.HasOne(conversation => conversation.Workspace)
                .WithMany(workspace => workspace.Conversations)
                .HasForeignKey(conversation => conversation.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(conversation => conversation.User)
                .WithMany()
                .HasForeignKey(conversation => conversation.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(conversation => conversation.Project)
                .WithMany()
                .HasForeignKey(conversation => conversation.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Role).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(message => message.Content).HasMaxLength(20000).IsRequired();
            entity.Property(message => message.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(message => message.AttachmentManifestJson).HasMaxLength(10000);
            entity.Property(message => message.ProviderKey).HasMaxLength(80);
            entity.Property(message => message.ModelKey).HasMaxLength(160);
            entity.Property(message => message.FinishReason).HasMaxLength(80);
            entity.HasIndex(message => new { message.ConversationId, message.CreatedAt, message.Id });
            entity.HasOne(message => message.Conversation)
                .WithMany(conversation => conversation.Messages)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
