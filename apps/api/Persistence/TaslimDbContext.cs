using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;

namespace Taslim.Api.Persistence;

public sealed class TaslimDbContext(DbContextOptions<TaslimDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<UsageTransaction> UsageTransactions => Set<UsageTransaction>();
    public DbSet<PersonalMemory> PersonalMemories => Set<PersonalMemory>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<ChatMessageAttachment> ChatMessageAttachments => Set<ChatMessageAttachment>();
    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();
    public DbSet<GenerationJobOutput> GenerationJobOutputs => Set<GenerationJobOutput>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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
            entity.Property(project => project.Instructions).HasMaxLength(4000);
            entity.Property(project => project.ContextNotes).HasMaxLength(8000);
            entity.Property(project => project.Type).HasMaxLength(50).IsRequired();
            entity.Property(project => project.Status).HasMaxLength(30).IsRequired();
            entity.HasIndex(project => project.WorkspaceId);
            entity.HasIndex(project => new { project.WorkspaceId, project.Status });
            entity.HasOne(project => project.Workspace)
                .WithMany(workspace => workspace.Projects)
                .HasForeignKey(project => project.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PersonalMemory>(entity =>
        {
            entity.HasKey(memory => memory.Id);
            entity.Property(memory => memory.Category).HasMaxLength(30).IsRequired();
            entity.Property(memory => memory.Title).HasMaxLength(160).IsRequired();
            entity.Property(memory => memory.Content).HasMaxLength(4000).IsRequired();
            entity.Property(memory => memory.Source).HasMaxLength(30).IsRequired();
            entity.Property(memory => memory.IsActive).IsRequired();
            entity.Property(memory => memory.CreatedAt).IsRequired();
            entity.Property(memory => memory.UpdatedAt).IsRequired();
            entity.HasIndex(memory => new { memory.WorkspaceId, memory.UserId });
            entity.HasIndex(memory => new { memory.UserId, memory.IsActive });
            entity.HasIndex(memory => new { memory.WorkspaceId, memory.UpdatedAt });
            entity.HasOne(memory => memory.User)
                .WithMany()
                .HasForeignKey(memory => memory.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(memory => memory.Workspace)
                .WithMany()
                .HasForeignKey(memory => memory.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<StoredFile>(entity =>
        {
            entity.HasKey(file => file.Id);
            entity.Property(file => file.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(file => file.StoredFileName).HasMaxLength(255).IsRequired();
            entity.Property(file => file.ContentType).HasMaxLength(160).IsRequired();
            entity.Property(file => file.Extension).HasMaxLength(20).IsRequired();
            entity.Property(file => file.StorageProvider).HasMaxLength(40).IsRequired();
            entity.Property(file => file.StorageKey).HasMaxLength(600).IsRequired();
            entity.Property(file => file.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(file => file.TextExtractionStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(file => file.ExtractedText).HasMaxLength(1_000_000);
            entity.Property(file => file.MetadataJson).HasMaxLength(20_000);
            entity.Property(file => file.CreatedAt).IsRequired();
            entity.HasIndex(file => new { file.WorkspaceId, file.CreatedAt });
            entity.HasIndex(file => new { file.UserId, file.CreatedAt });
            entity.HasIndex(file => file.ProjectId);
            entity.HasIndex(file => file.ConversationId);
            entity.HasIndex(file => file.Status);
            entity.HasIndex(file => file.StorageKey).IsUnique();
            entity.HasOne(file => file.Workspace).WithMany(workspace => workspace.Files).HasForeignKey(file => file.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(file => file.User).WithMany().HasForeignKey(file => file.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(file => file.Project).WithMany(project => project.Files).HasForeignKey(file => file.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(file => file.Conversation).WithMany(conversation => conversation.Files).HasForeignKey(file => file.ConversationId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<GenerationJob>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.JobType).HasMaxLength(100).IsRequired();
            entity.Property(job => job.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(job => job.Title).HasMaxLength(160);
            entity.Property(job => job.Provider).HasMaxLength(80);
            entity.Property(job => job.ProviderModel).HasMaxLength(160);
            entity.Property(job => job.InputJson).HasMaxLength(100_000).IsRequired();
            entity.Property(job => job.ResultJson).HasMaxLength(100_000);
            entity.Property(job => job.ErrorCode).HasMaxLength(100);
            entity.Property(job => job.ErrorMessage).HasMaxLength(1_000);
            entity.Property(job => job.ProgressPercent).IsRequired();
            entity.Property(job => job.CancellationRequested).IsRequired();
            entity.Property(job => job.ConcurrencyToken).IsConcurrencyToken().IsRequired();
            entity.Property(job => job.CreatedAt).IsRequired();
            entity.ToTable("GenerationJobs", table => table.HasCheckConstraint("CK_GenerationJobs_ProgressPercent", "\"ProgressPercent\" BETWEEN 0 AND 100"));
            entity.HasIndex(job => new { job.Status, job.QueuedAt, job.CreatedAt });
            entity.HasIndex(job => new { job.WorkspaceId, job.CreatedAt });
            entity.HasIndex(job => new { job.WorkspaceId, job.Status, job.CreatedAt });
            entity.HasIndex(job => job.ProjectId);
            entity.HasOne(job => job.Workspace).WithMany().HasForeignKey(job => job.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(job => job.Project).WithMany().HasForeignKey(job => job.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(job => job.CreatedByUser).WithMany().HasForeignKey(job => job.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<GenerationJobOutput>(entity =>
        {
            entity.HasKey(output => output.Id);
            entity.Property(output => output.OutputType).HasMaxLength(80).IsRequired();
            entity.Property(output => output.MetadataJson).HasMaxLength(20_000);
            entity.Property(output => output.CreatedAt).IsRequired();
            entity.HasIndex(output => output.GenerationJobId);
            entity.HasIndex(output => output.StoredFileId);
            entity.HasOne(output => output.GenerationJob).WithMany(job => job.Outputs).HasForeignKey(output => output.GenerationJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(output => output.StoredFile).WithMany(file => file.GenerationJobOutputs).HasForeignKey(output => output.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Asset>(entity =>
        {
            entity.HasKey(asset => asset.Id);
            entity.Property(asset => asset.Name).HasMaxLength(255).IsRequired();
            entity.Property(asset => asset.Description).HasMaxLength(2_000);
            entity.Property(asset => asset.AssetType).HasMaxLength(40).IsRequired();
            entity.Property(asset => asset.MimeType).HasMaxLength(160);
            entity.Property(asset => asset.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(asset => asset.MetadataJson).HasMaxLength(20_000);
            entity.Property(asset => asset.CreatedAt).IsRequired();
            entity.Property(asset => asset.UpdatedAt).IsRequired();
            entity.HasIndex(asset => new { asset.WorkspaceId, asset.Status, asset.CreatedAt });
            entity.HasIndex(asset => new { asset.WorkspaceId, asset.AssetType, asset.Status, asset.CreatedAt });
            entity.HasIndex(asset => new { asset.ProjectId, asset.Status, asset.CreatedAt });
            entity.HasIndex(asset => new { asset.WorkspaceId, asset.Name });
            entity.HasIndex(asset => asset.StoredFileId);
            entity.HasIndex(asset => asset.SourceGenerationJobId);
            entity.HasOne(asset => asset.Workspace).WithMany(workspace => workspace.Assets).HasForeignKey(asset => asset.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(asset => asset.Project).WithMany(project => project.Assets).HasForeignKey(asset => asset.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(asset => asset.CreatedByUser).WithMany().HasForeignKey(asset => asset.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(asset => asset.StoredFile).WithMany(file => file.Assets).HasForeignKey(asset => asset.StoredFileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(asset => asset.SourceGenerationJob).WithMany(job => job.Assets).HasForeignKey(asset => asset.SourceGenerationJobId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ChatMessageAttachment>(entity =>
        {
            entity.HasKey(attachment => new { attachment.ChatMessageId, attachment.StoredFileId });
            entity.Property(attachment => attachment.SortOrder).IsRequired();
            entity.HasIndex(attachment => new { attachment.StoredFileId, attachment.ChatMessageId });
            entity.HasOne(attachment => attachment.ChatMessage)
                .WithMany(message => message.Attachments)
                .HasForeignKey(attachment => attachment.ChatMessageId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(attachment => attachment.StoredFile)
                .WithMany(file => file.Attachments)
                .HasForeignKey(attachment => attachment.StoredFileId)
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
            entity.Property(conversation => conversation.NextMessageSequence).IsRequired();
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
            entity.Property(message => message.Sequence).IsRequired();
            entity.Property(message => message.AttachmentManifestJson).HasMaxLength(10000);
            entity.Property(message => message.RequestId).HasMaxLength(80);
            entity.Property(message => message.ProviderKey).HasMaxLength(80);
            entity.Property(message => message.ModelKey).HasMaxLength(160);
            entity.Property(message => message.FinishReason).HasMaxLength(80);
            entity.HasIndex(message => new { message.ConversationId, message.Sequence }).IsUnique();
            entity.HasIndex(message => new { message.ConversationId, message.CreatedAt, message.Id });
            entity.HasIndex(message => new { message.ConversationId, message.RequestId }).IsUnique().HasFilter("\"RequestId\" IS NOT NULL");
            entity.HasOne(message => message.Conversation)
                .WithMany(conversation => conversation.Messages)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UsageTransaction>(entity =>
        {
            entity.HasKey(transaction => transaction.Id);
            entity.Property(transaction => transaction.RequestId).HasMaxLength(80).IsRequired();
            entity.Property(transaction => transaction.Feature).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(transaction => transaction.Provider).HasMaxLength(80).IsRequired();
            entity.Property(transaction => transaction.Model).HasMaxLength(160).IsRequired();
            entity.Property(transaction => transaction.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(transaction => transaction.ChargedUnit).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(transaction => transaction.Currency).HasMaxLength(3).IsRequired();
            entity.Property(transaction => transaction.ProviderCostUsd).HasPrecision(18, 8).IsRequired();
            entity.Property(transaction => transaction.ChargedAmount).HasPrecision(18, 8).IsRequired();
            entity.Property(transaction => transaction.EstimatedProviderCostUsd).HasPrecision(18, 8);
            entity.Property(transaction => transaction.PricingVersion).HasMaxLength(100);
            entity.Property(transaction => transaction.PricingSnapshotJson).HasMaxLength(8_000);
            entity.Property(transaction => transaction.SafeMetadataJson).HasMaxLength(8_000);
            entity.Property(transaction => transaction.FailureCode).HasMaxLength(80);
            entity.Property(transaction => transaction.AnomalyCode).HasMaxLength(100);
            entity.Property(transaction => transaction.CreatedAt).IsRequired();
            entity.HasIndex(transaction => new { transaction.WorkspaceId, transaction.CreatedAt });
            entity.HasIndex(transaction => new { transaction.UserId, transaction.CreatedAt });
            entity.HasIndex(transaction => new { transaction.GenerationJobId, transaction.CreatedAt });
            entity.HasIndex(transaction => transaction.Feature);
            entity.HasIndex(transaction => transaction.Status);
            entity.HasIndex(transaction => transaction.Provider);
            entity.HasIndex(transaction => transaction.IsAnomalous);
            entity.HasIndex(transaction => new { transaction.WorkspaceId, transaction.RequestId, transaction.Feature }).IsUnique();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(transaction => transaction.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(transaction => transaction.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Project>().WithMany().HasForeignKey(transaction => transaction.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Conversation>().WithMany().HasForeignKey(transaction => transaction.ConversationId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(transaction => transaction.GenerationJob).WithMany().HasForeignKey(transaction => transaction.GenerationJobId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
