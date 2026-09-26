using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Notifications;

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
    public DbSet<GenerationProviderAttempt> GenerationProviderAttempts => Set<GenerationProviderAttempt>();
    public DbSet<ProviderCircuit> ProviderCircuits => Set<ProviderCircuit>();
    public DbSet<ProviderExecutionFinalization> ProviderExecutionFinalizations => Set<ProviderExecutionFinalization>();
    public DbSet<ActivityReadState> ActivityReadStates => Set<ActivityReadState>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetRepresentation> AssetRepresentations => Set<AssetRepresentation>();
    public DbSet<ResearchSource> ResearchSources => Set<ResearchSource>();
    public DbSet<ResearchEvidence> ResearchEvidence => Set<ResearchEvidence>();
        public DbSet<Plan> Plans => Set<Plan>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<BillingPeriod> BillingPeriods => Set<BillingPeriod>();
        public DbSet<CreditEntitlement> CreditEntitlements => Set<CreditEntitlement>();
        public DbSet<CreditLedgerEntry> CreditLedgerEntries => Set<CreditLedgerEntry>();
        public DbSet<ProviderCustomerReference> ProviderCustomerReferences => Set<ProviderCustomerReference>();
        public DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();
        public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
        public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
        public DbSet<PaymentRefund> PaymentRefunds => Set<PaymentRefund>();
        public DbSet<SubscriptionLifecycleEvent> SubscriptionLifecycleEvents => Set<SubscriptionLifecycleEvent>();
        public DbSet<PaymentReconciliationRecord> PaymentReconciliationRecords => Set<PaymentReconciliationRecord>();
        public DbSet<MovieProject> MovieProjects => Set<MovieProject>();
    public DbSet<MovieAct> MovieActs => Set<MovieAct>();
    public DbSet<MovieSequence> MovieSequences => Set<MovieSequence>();
    public DbSet<MovieStory> MovieStories => Set<MovieStory>();
    public DbSet<MovieStoryRevision> MovieStoryRevisions => Set<MovieStoryRevision>();
    public DbSet<MovieScreenplayScene> MovieScreenplayScenes => Set<MovieScreenplayScene>();
    public DbSet<MovieScreenplayElement> MovieScreenplayElements => Set<MovieScreenplayElement>();
    public DbSet<MovieContinuityGuide> MovieContinuityGuides => Set<MovieContinuityGuide>();
    public DbSet<MovieGuideRevision> MovieGuideRevisions => Set<MovieGuideRevision>();
    public DbSet<MovieScene> MovieScenes => Set<MovieScene>();
    public DbSet<MovieCharacter> MovieCharacters => Set<MovieCharacter>();
    public DbSet<MovieCharacterState> MovieCharacterStates => Set<MovieCharacterState>();
    public DbSet<MovieCharacterReferenceAsset> MovieCharacterReferenceAssets => Set<MovieCharacterReferenceAsset>();
    public DbSet<MovieCharacterRelationship> MovieCharacterRelationships => Set<MovieCharacterRelationship>();
    public DbSet<MovieCharacterContinuityLock> MovieCharacterContinuityLocks => Set<MovieCharacterContinuityLock>();
    public DbSet<MovieLocation> MovieLocations => Set<MovieLocation>();
    public DbSet<MovieSet> MovieSets => Set<MovieSet>();
    public DbSet<MovieSetVariation> MovieSetVariations => Set<MovieSetVariation>();
    public DbSet<MovieProp> MovieProps => Set<MovieProp>();
    public DbSet<MovieWorldReference> MovieWorldReferences => Set<MovieWorldReference>();
    public DbSet<MovieWorldReferenceLink> MovieWorldReferenceLinks => Set<MovieWorldReferenceLink>();
    public DbSet<MovieWorldUsage> MovieWorldUsages => Set<MovieWorldUsage>();
    public DbSet<MovieContinuityFact> MovieContinuityFacts => Set<MovieContinuityFact>();
    public DbSet<MovieContinuityLock> MovieContinuityLocks => Set<MovieContinuityLock>();
    public DbSet<MovieShot> MovieShots => Set<MovieShot>();
    public DbSet<MovieClip> MovieClips => Set<MovieClip>();
    public DbSet<MovieAssembly> MovieAssemblies => Set<MovieAssembly>();
    public DbSet<MovieTake> MovieTakes => Set<MovieTake>();
    public DbSet<MovieTakeApproval> MovieTakeApprovals => Set<MovieTakeApproval>();
    public DbSet<MovieVideoProviderExecution> MovieVideoProviderExecutions => Set<MovieVideoProviderExecution>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.DisplayName).HasMaxLength(120).IsRequired();
            entity.Property(user => user.PreferredLanguage).HasMaxLength(5).IsRequired();
            entity.Property(user => user.DefaultGenerationLanguage).HasMaxLength(5).IsRequired();
            entity.Property(user => user.TimeZone).HasMaxLength(100).IsRequired();
            entity.Property(user => user.OutputPreference).HasMaxLength(32).IsRequired();
            entity.Property(user => user.IncludeSourceLinks).HasDefaultValue(true).IsRequired();
            entity.Property(user => user.OnboardingIntent).HasMaxLength(50);
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

        builder.Entity<MovieProject>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Mode).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.AspectRatio).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Style).HasMaxLength(100).IsRequired();
            entity.Property(item => item.Language).HasMaxLength(5).IsRequired();
            entity.Property(item => item.AdditionalInstructions).HasMaxLength(8_000);
            entity.Property(item => item.ProductionStatus).HasMaxLength(40).HasDefaultValue(MovieProductionStatuses.Draft).IsRequired();
            entity.Property(item => item.QualityLevel).HasMaxLength(20).HasDefaultValue(MovieQualityLevels.Standard).IsRequired();
            entity.Property(item => item.AutoDirectorEnabled).IsRequired();
            entity.HasIndex(item => new { item.WorkspaceId, item.UpdatedAt });
            entity.HasIndex(item => item.ProjectId);
            entity.HasOne(item => item.Workspace).WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Project).WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.CreatedByUser).WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MovieAct>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Summary).HasMaxLength(8_000);
            entity.Property(item => item.Status).HasMaxLength(30).HasDefaultValue(MovieHierarchyStatuses.Planned).IsRequired();
            entity.HasIndex(item => new { item.MovieProjectId, item.Sequence }).IsUnique();
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Acts).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieSequence>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Summary).HasMaxLength(8_000);
            entity.Property(item => item.Status).HasMaxLength(30).HasDefaultValue(MovieHierarchyStatuses.Planned).IsRequired();
            entity.HasIndex(item => new { item.MovieActId, item.Sequence }).IsUnique();
            entity.HasOne(item => item.MovieAct).WithMany(item => item.Sequences).HasForeignKey(item => item.MovieActId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MovieStory>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Premise).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.Logline).HasMaxLength(2_000).IsRequired();
            entity.Property(item => item.Synopsis).HasMaxLength(20_000).IsRequired();
            entity.Property(item => item.Treatment).HasMaxLength(40_000).IsRequired();
            entity.Property(item => item.ApprovalState).HasMaxLength(30).IsRequired();
            entity.HasIndex(item => item.MovieProjectId).IsUnique();
            entity.HasIndex(item => new { item.WorkspaceId, item.UpdatedAt });
            entity.HasOne(item => item.MovieProject).WithOne().HasForeignKey<MovieStory>(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieStoryRevision>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Premise).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.Logline).HasMaxLength(2_000).IsRequired();
            entity.Property(item => item.Synopsis).HasMaxLength(20_000).IsRequired();
            entity.Property(item => item.Treatment).HasMaxLength(40_000).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.Authorship).HasMaxLength(30).IsRequired();
            entity.Property(item => item.ChangeSummary).HasMaxLength(2_000);
            entity.Property(item => item.RejectionReason).HasMaxLength(2_000);
            entity.HasIndex(item => new { item.MovieStoryId, item.RevisionNumber }).IsUnique();
            entity.HasIndex(item => new { item.MovieStoryId, item.Status });
            entity.HasOne(item => item.MovieStory).WithMany(item => item.Revisions).HasForeignKey(item => item.MovieStoryId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieScreenplayScene>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.SceneIdentifier).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Slugline).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Synopsis).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieStoryRevisionId, item.Ordinal }).IsUnique();
            entity.HasIndex(item => new { item.MovieStoryRevisionId, item.SceneIdentifier }).IsUnique();
            entity.HasIndex(item => item.MovieSceneId);
            entity.HasOne(item => item.Revision).WithMany(item => item.Scenes).HasForeignKey(item => item.MovieStoryRevisionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieScene).WithMany().HasForeignKey(item => item.MovieSceneId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieScreenplayElement>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ElementType).HasMaxLength(30).IsRequired();
            entity.Property(item => item.Content).HasMaxLength(20_000).IsRequired();
            entity.Property(item => item.CharacterName).HasMaxLength(160);
            entity.Property(item => item.Parenthetical).HasMaxLength(500);
            entity.HasIndex(item => new { item.MovieScreenplaySceneId, item.Ordinal }).IsUnique();
            entity.HasOne(item => item.Scene).WithMany(item => item.Elements).HasForeignKey(item => item.MovieScreenplaySceneId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieContinuityGuide>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.VisualLanguage).HasMaxLength(4_000).IsRequired();
            entity.Property(item => item.CameraLanguage).HasMaxLength(4_000).IsRequired();
            entity.Property(item => item.ColorAndLighting).HasMaxLength(4_000).IsRequired();
            entity.Property(item => item.SoundAndNarration).HasMaxLength(4_000).IsRequired();
            entity.Property(item => item.ContinuityRules).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.ReferenceAssetIdsJson).HasMaxLength(20_000);
            entity.Property(item => item.CurrentRevisionNumber).IsRequired();
            entity.HasIndex(item => item.MovieProjectId).IsUnique();
            entity.HasOne(item => item.MovieProject).WithOne(item => item.Guide).HasForeignKey<MovieContinuityGuide>(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieGuideRevision>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.StoryBibleJson).HasMaxLength(50_000).IsRequired();
            entity.Property(item => item.CharacterBibleReferencesJson).HasMaxLength(50_000).IsRequired();
            entity.Property(item => item.WorldBibleReferencesJson).HasMaxLength(50_000).IsRequired();
            entity.Property(item => item.VisualBibleJson).HasMaxLength(50_000).IsRequired();
            entity.Property(item => item.CinematographyBibleJson).HasMaxLength(50_000).IsRequired();
            entity.Property(item => item.AudioBibleJson).HasMaxLength(50_000).IsRequired();
            entity.Property(item => item.ContinuityBibleJson).HasMaxLength(50_000).IsRequired();
            entity.HasIndex(item => new { item.MovieContinuityGuideId, item.RevisionNumber }).IsUnique();
            entity.HasIndex(item => new { item.MovieContinuityGuideId, item.Status });
            entity.HasOne(item => item.Guide).WithMany(item => item.Revisions).HasForeignKey(item => item.MovieContinuityGuideId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.CreatedByUser).WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.LockedByUser).WithMany().HasForeignKey(item => item.LockedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MovieScene>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Summary).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.ContinuityNotes).HasMaxLength(4_000);
            entity.Property(item => item.Narration).HasMaxLength(8_000);
            entity.Property(item => item.Dialogue).HasMaxLength(8_000);
            entity.Property(item => item.Status).HasMaxLength(30).HasDefaultValue(MovieHierarchyStatuses.Planned).IsRequired();
            entity.HasIndex(item => item.MovieSequenceId);
            entity.HasIndex(item => new { item.MovieSequenceId, item.Sequence }).IsUnique();
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Scenes).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieSequence).WithMany(item => item.Scenes).HasForeignKey(item => item.MovieSequenceId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieCharacter>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Role).HasMaxLength(160);
            entity.Property(item => item.Description).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.Appearance).HasMaxLength(4_000);
            entity.Property(item => item.PhysicalDescription).HasMaxLength(4_000);
            entity.Property(item => item.Wardrobe).HasMaxLength(4_000);
            entity.Property(item => item.VoiceReference).HasMaxLength(4_000);
            entity.Property(item => item.PersonalityAndStoryNotes).HasMaxLength(8_000);
            entity.Property(item => item.VoiceAndPerformance).HasMaxLength(4_000);
            entity.Property(item => item.ContinuityNotes).HasMaxLength(4_000);
            entity.HasIndex(item => item.MovieProjectId);
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Characters).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.ReferenceAsset).WithMany().HasForeignKey(item => item.ReferenceAssetId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieCharacterState>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Key).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Label).HasMaxLength(160);
            entity.Property(item => item.Wardrobe).HasMaxLength(4_000);
            entity.Property(item => item.AgeOrTimeState).HasMaxLength(1_000);
            entity.Property(item => item.Appearance).HasMaxLength(4_000);
            entity.Property(item => item.InjuryOrCondition).HasMaxLength(4_000);
            entity.Property(item => item.LocationOrStoryState).HasMaxLength(4_000);
            entity.Property(item => item.ContinuityNotes).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieCharacterId, item.Key }).IsUnique();
            entity.HasOne(item => item.Character).WithMany(item => item.States).HasForeignKey(item => item.MovieCharacterId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieCharacterReferenceAsset>(entity =>
        {
            entity.HasKey(item => new { item.MovieCharacterId, item.AssetId });
            entity.Property(item => item.Label).HasMaxLength(160);
            entity.Property(item => item.SortOrder).IsRequired();
            entity.HasIndex(item => new { item.MovieCharacterId, item.SortOrder });
            entity.HasIndex(item => item.AssetId);
            entity.HasOne(item => item.Character).WithMany(item => item.ReferenceAssets).HasForeignKey(item => item.MovieCharacterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Asset).WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MovieCharacterRelationship>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RelationshipType).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Notes).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieCharacterId, item.RelatedCharacterId, item.RelationshipType }).IsUnique();
            entity.HasOne(item => item.Character).WithMany(item => item.Relationships).HasForeignKey(item => item.MovieCharacterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.RelatedCharacter).WithMany().HasForeignKey(item => item.RelatedCharacterId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MovieCharacterContinuityLock>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FieldKey).HasMaxLength(120).IsRequired();
            entity.Property(item => item.LockedValue).HasMaxLength(8_000).IsRequired();
            entity.HasIndex(item => new { item.MovieCharacterId, item.MovieCharacterStateId, item.FieldKey }).IsUnique();
            entity.HasIndex(item => item.ApprovedByUserId);
            entity.HasOne(item => item.Character).WithMany(item => item.ContinuityLocks).HasForeignKey(item => item.MovieCharacterId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.CharacterState).WithMany(item => item.ContinuityLocks).HasForeignKey(item => item.MovieCharacterStateId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.ApprovedByUser).WithMany().HasForeignKey(item => item.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MovieLocation>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.VisualContinuityNotes).HasMaxLength(4_000);
            entity.HasIndex(item => item.MovieProjectId);
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Locations).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.ReferenceAsset).WithMany().HasForeignKey(item => item.ReferenceAssetId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieSet>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.EnvironmentType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.VisualDescription).HasMaxLength(4_000);
            entity.Property(item => item.TimeOfDay).HasMaxLength(80);
            entity.Property(item => item.Weather).HasMaxLength(160);
            entity.Property(item => item.ContinuityNotes).HasMaxLength(4_000);
            entity.HasIndex(item => item.MovieProjectId);
            entity.HasIndex(item => item.MovieLocationId);
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Sets).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieLocation).WithMany().HasForeignKey(item => item.MovieLocationId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.ReferenceAsset).WithMany().HasForeignKey(item => item.ReferenceAssetId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieSetVariation>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.VisualDescription).HasMaxLength(4_000);
            entity.Property(item => item.TimeOfDay).HasMaxLength(80);
            entity.Property(item => item.Weather).HasMaxLength(160);
            entity.Property(item => item.Lighting).HasMaxLength(2_000);
            entity.Property(item => item.ContinuityNotes).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieSetId, item.Name }).IsUnique();
            entity.HasIndex(item => new { item.MovieSetId, item.IsDefault });
            entity.HasOne(item => item.MovieSet).WithMany(item => item.Variations).HasForeignKey(item => item.MovieSetId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.ReferenceAsset).WithMany().HasForeignKey(item => item.ReferenceAssetId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieProp>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.Category).HasMaxLength(80);
            entity.Property(item => item.ContinuityNotes).HasMaxLength(4_000);
            entity.HasIndex(item => item.MovieProjectId);
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Props).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.ReferenceAsset).WithMany().HasForeignKey(item => item.ReferenceAssetId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieWorldReference>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Kind).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(4_000);
            entity.Property(item => item.TagsJson).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieProjectId, item.Kind });
            entity.HasOne(item => item.MovieProject).WithMany(item => item.WorldReferences).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Asset).WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieWorldReferenceLink>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EntityType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Role).HasMaxLength(120);
            entity.HasIndex(item => new { item.MovieWorldReferenceId, item.EntityType, item.EntityId }).IsUnique();
            entity.HasIndex(item => new { item.MovieProjectId, item.EntityType, item.EntityId });
            entity.HasOne(item => item.MovieProject).WithMany().HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Reference).WithMany(item => item.Links).HasForeignKey(item => item.MovieWorldReferenceId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieWorldUsage>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EntityType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Role).HasMaxLength(120);
            entity.HasIndex(item => new { item.MovieSceneId, item.MovieShotId, item.EntityType, item.EntityId }).IsUnique();
            entity.HasIndex(item => new { item.MovieProjectId, item.EntityType, item.EntityId });
            entity.HasOne(item => item.MovieProject).WithMany(item => item.WorldUsages).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieScene).WithMany().HasForeignKey(item => item.MovieSceneId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieShot).WithMany().HasForeignKey(item => item.MovieShotId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieContinuityFact>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ScopeType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.FactKey).HasMaxLength(160).IsRequired();
            entity.Property(item => item.FactValue).HasMaxLength(4_000).IsRequired();
            entity.Property(item => item.Notes).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieProjectId, item.ScopeType, item.ScopeId, item.FactKey });
            entity.HasOne(item => item.MovieProject).WithMany(item => item.ContinuityFacts).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieContinuityLock>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.EntityType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.FieldName).HasMaxLength(160).IsRequired();
            entity.Property(item => item.LockedValue).HasMaxLength(4_000).IsRequired();
            entity.Property(item => item.Strength).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Reason).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieProjectId, item.EntityType, item.EntityId, item.FieldName, item.ReleasedAt });
            entity.HasOne(item => item.MovieProject).WithMany(item => item.ContinuityLocks).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.CreatedByUser).WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MovieShot>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Description).HasMaxLength(8_000).IsRequired();
            entity.Property(item => item.CameraAndFraming).HasMaxLength(2_000);
            entity.Property(item => item.CameraMotion).HasMaxLength(2_000);
            entity.Property(item => item.Narration).HasMaxLength(8_000);
            entity.Property(item => item.Dialogue).HasMaxLength(8_000);
            entity.Property(item => item.VisualContinuityNotes).HasMaxLength(4_000);
            entity.Property(item => item.Status).HasMaxLength(30).HasDefaultValue(MovieShotStatuses.Planned).IsRequired();
            entity.HasIndex(item => item.SelectedTakeId);
            entity.HasIndex(item => item.FinalTakeId);
            entity.HasIndex(item => new { item.MovieSceneId, item.Sequence }).IsUnique();
            entity.HasOne(item => item.Scene).WithMany(item => item.Shots).HasForeignKey(item => item.MovieSceneId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.SelectedTake).WithMany().HasForeignKey(item => item.SelectedTakeId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.FinalTake).WithMany().HasForeignKey(item => item.FinalTakeId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieTake>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Label).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(30).HasDefaultValue(MovieTakeStatuses.Draft).IsRequired();
            entity.Property(item => item.QualityLevel).HasMaxLength(20).HasDefaultValue(MovieQualityLevels.Standard).IsRequired();
            entity.Property(item => item.Notes).HasMaxLength(4_000);
            entity.Property(item => item.MetadataJson).HasMaxLength(20_000);
            entity.HasIndex(item => new { item.MovieShotId, item.VersionNumber }).IsUnique();
            entity.HasIndex(item => new { item.MovieShotId, item.Status });
            entity.HasOne(item => item.MovieShot).WithMany(item => item.Takes).HasForeignKey(item => item.MovieShotId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieClip).WithMany().HasForeignKey(item => item.MovieClipId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.GenerationJob).WithMany().HasForeignKey(item => item.GenerationJobId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.Asset).WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieTakeApproval>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Decision).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Comment).HasMaxLength(4_000);
            entity.HasIndex(item => new { item.MovieTakeId, item.CreatedAt });
            entity.HasOne(item => item.MovieTake).WithMany(item => item.Approvals).HasForeignKey(item => item.MovieTakeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MovieClip>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.ProviderKey).HasMaxLength(80);
            entity.Property(item => item.ProviderClipId).HasMaxLength(240);
            entity.Property(item => item.MetadataJson).HasMaxLength(20_000);
            entity.Property(item => item.ContinuitySnapshotJson).HasMaxLength(100_000);
            entity.HasIndex(item => new { item.MovieProjectId, item.Status });
            entity.HasIndex(item => item.GenerationJobId);
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Clips).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieScene).WithMany(item => item.Clips).HasForeignKey(item => item.MovieSceneId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.MovieShot).WithMany(item => item.Clips).HasForeignKey(item => item.MovieShotId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.GenerationJob).WithMany().HasForeignKey(item => item.GenerationJobId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.Asset).WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.StoredFile).WithMany().HasForeignKey(item => item.StoredFileId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<MovieVideoProviderExecution>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ProviderKey).HasMaxLength(80).IsRequired();
            entity.Property(item => item.ProviderJobId).HasMaxLength(240);
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.LastErrorCode).HasMaxLength(100);
            entity.HasIndex(item => item.GenerationJobId).IsUnique();
            entity.HasIndex(item => new { item.Status, item.NextPollAt });
            entity.HasIndex(item => item.MovieClipId);
            entity.HasOne(item => item.GenerationJob).WithMany().HasForeignKey(item => item.GenerationJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.MovieClip).WithMany().HasForeignKey(item => item.MovieClipId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MovieAssembly>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.OutputFormat).HasMaxLength(20).IsRequired();
            entity.Property(item => item.MetadataJson).HasMaxLength(20_000);
            entity.HasIndex(item => new { item.MovieProjectId, item.CreatedAt });
            entity.HasOne(item => item.MovieProject).WithMany(item => item.Assemblies).HasForeignKey(item => item.MovieProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.GenerationJob).WithMany().HasForeignKey(item => item.GenerationJobId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(item => item.Asset).WithMany().HasForeignKey(item => item.AssetId).OnDelete(DeleteBehavior.SetNull);
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
            entity.Property(job => job.EstimatedProviderCostUsd).HasPrecision(18, 8);
            entity.Property(job => job.CostEstimateJson).HasMaxLength(8_000);
            entity.Property(job => job.InputJson).HasMaxLength(100_000).IsRequired();
            entity.Property(job => job.IdempotencyKey).HasMaxLength(80);
            entity.Property(job => job.RequestFingerprint).HasMaxLength(64);
            entity.Property(job => job.RequestId).HasMaxLength(128);
            entity.Property(job => job.ResultJson).HasMaxLength(100_000);
            entity.Property(job => job.ErrorCode).HasMaxLength(100);
            entity.Property(job => job.ErrorMessage).HasMaxLength(1_000);
            entity.Property(job => job.ProgressPercent).IsRequired();
            entity.Property(job => job.CancellationRequested).IsRequired();
            entity.Property(job => job.RetryCount).IsRequired();
            entity.Property(job => job.ConcurrencyToken).IsConcurrencyToken().IsRequired();
            entity.Property(job => job.CreatedAt).IsRequired();
            entity.ToTable("GenerationJobs", table => table.HasCheckConstraint("CK_GenerationJobs_ProgressPercent", "\"ProgressPercent\" BETWEEN 0 AND 100"));
            entity.HasIndex(job => new { job.Status, job.QueuedAt, job.CreatedAt });
            entity.HasIndex(job => new { job.WorkspaceId, job.CreatedAt });
            entity.HasIndex(job => new { job.WorkspaceId, job.Status, job.CreatedAt });
            entity.HasIndex(job => job.ProjectId);
            entity.HasIndex(job => new { job.CreatedByUserId, job.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            entity.HasOne(job => job.Workspace).WithMany().HasForeignKey(job => job.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(job => job.Project).WithMany().HasForeignKey(job => job.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(job => job.CreatedByUser).WithMany().HasForeignKey(job => job.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<GenerationProviderAttempt>(entity =>
        {
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.JobConcurrencyToken).IsRequired();
            entity.Property(attempt => attempt.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.Property(attempt => attempt.Capability).HasMaxLength(120);
            entity.Property(attempt => attempt.Provider).HasMaxLength(80).IsRequired();
            entity.Property(attempt => attempt.Model).HasMaxLength(160);
            entity.Property(attempt => attempt.ProviderExecutionId).HasMaxLength(240);
            entity.Property(attempt => attempt.ResultClassification).HasMaxLength(40).IsRequired();
            entity.Property(attempt => attempt.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(attempt => attempt.EstimatedProviderCostUsd).HasPrecision(18, 8);
            entity.Property(attempt => attempt.ActualProviderCostUsd).HasPrecision(18, 8);
            entity.Property(attempt => attempt.CostEstimateJson).HasMaxLength(8_000);
            entity.Property(attempt => attempt.FailureCode).HasMaxLength(100);
            entity.Property(attempt => attempt.PricingVersion).HasMaxLength(100);
            entity.Property(attempt => attempt.PricingSnapshotJson).HasMaxLength(8_000);
            entity.Property(attempt => attempt.Currency).HasMaxLength(3).IsRequired();
            entity.Property(attempt => attempt.SafeMetadataJson).HasMaxLength(8_000);
            entity.Property(attempt => attempt.FinalizationKey).HasMaxLength(160).IsRequired();
            entity.Property(attempt => attempt.StartedAt).IsRequired();
            entity.HasIndex(attempt => new { attempt.GenerationJobId, attempt.AttemptNumber }).IsUnique();
            entity.HasIndex(attempt => new { attempt.GenerationJobId, attempt.IdempotencyKey });
            entity.HasIndex(attempt => new { attempt.Provider, attempt.Capability, attempt.StartedAt });
            entity.HasIndex(attempt => attempt.FinalizationKey).IsUnique();
            entity.HasOne(attempt => attempt.GenerationJob)
                .WithMany(job => job.ProviderAttempts)
                .HasForeignKey(attempt => attempt.GenerationJobId)
                .OnDelete(DeleteBehavior.Cascade);
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


        builder.Entity<ProviderCircuit>(entity =>
        {
            entity.HasKey(circuit => circuit.Id);
            entity.Property(circuit => circuit.ProviderKey).HasMaxLength(80).IsRequired();
            entity.Property(circuit => circuit.Capability).HasMaxLength(120).IsRequired();
            entity.Property(circuit => circuit.State).HasMaxLength(20).IsRequired();
            entity.Property(circuit => circuit.RowVersion).IsConcurrencyToken().IsRequired();
            entity.Property(circuit => circuit.UpdatedAt).IsRequired();
            entity.HasIndex(circuit => new { circuit.ProviderKey, circuit.Capability }).IsUnique();
            entity.HasIndex(circuit => new { circuit.State, circuit.OpenUntil });
        });

        builder.Entity<ProviderExecutionFinalization>(entity =>
        {
            entity.HasKey(finalization => finalization.Id);
            entity.Property(finalization => finalization.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.Property(finalization => finalization.State).HasMaxLength(20).IsRequired();
            entity.Property(finalization => finalization.ClaimedAt).IsRequired();
            entity.HasIndex(finalization => new { finalization.GenerationJobId, finalization.IdempotencyKey }).IsUnique();
            entity.HasIndex(finalization => new { finalization.State, finalization.ClaimExpiresAt });
            entity.HasOne(finalization => finalization.GenerationJob).WithMany().HasForeignKey(finalization => finalization.GenerationJobId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ActivityReadState>(entity =>
        {
            entity.HasKey(read => read.Id);
            entity.Property(read => read.ReadAt).IsRequired();
            entity.HasIndex(read => new { read.UserId, read.GenerationJobId }).IsUnique();
            entity.HasIndex(read => read.GenerationJobId);
            entity.HasOne(read => read.User).WithMany().HasForeignKey(read => read.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(read => read.GenerationJob).WithMany().HasForeignKey(read => read.GenerationJobId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Notification>(entity =>
        {
            entity.HasKey(notification => notification.Id);
            entity.Property(notification => notification.Type).HasMaxLength(60).IsRequired();
            entity.Property(notification => notification.DeduplicationKey).HasMaxLength(180).IsRequired();
            entity.Property(notification => notification.ResourceTitle).HasMaxLength(160);
            entity.Property(notification => notification.CreatedAt).IsRequired();
            entity.HasIndex(notification => new { notification.UserId, notification.WorkspaceId, notification.ReadAt, notification.CreatedAt });
            entity.HasIndex(notification => new { notification.UserId, notification.DeduplicationKey }).IsUnique();
            entity.HasIndex(notification => notification.GenerationJobId);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(notification => notification.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Workspace>().WithMany().HasForeignKey(notification => notification.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Project>().WithMany().HasForeignKey(notification => notification.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<GenerationJob>().WithMany().HasForeignKey(notification => notification.GenerationJobId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Asset>().WithMany().HasForeignKey(notification => notification.AssetId).OnDelete(DeleteBehavior.SetNull);
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

        builder.Entity<AssetRepresentation>(entity =>
        {
            entity.HasKey(representation => representation.Id);
            entity.Property(representation => representation.RepresentationType).HasMaxLength(30).IsRequired();
            entity.Property(representation => representation.FileName).HasMaxLength(255).IsRequired();
            entity.Property(representation => representation.ContentType).HasMaxLength(160).IsRequired();
            entity.Property(representation => representation.SizeBytes).IsRequired();
            entity.Property(representation => representation.CreatedAt).IsRequired();
            entity.HasIndex(representation => new { representation.AssetId, representation.RepresentationType }).IsUnique();
            entity.HasIndex(representation => representation.StoredFileId).IsUnique();
            entity.HasOne(representation => representation.Asset).WithMany(asset => asset.Representations).HasForeignKey(representation => representation.AssetId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(representation => representation.StoredFile).WithMany(file => file.AssetRepresentations).HasForeignKey(representation => representation.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ResearchSource>(entity =>
        {
            entity.HasKey(source => source.Id);
            entity.Property(source => source.CitationId).HasMaxLength(16).IsRequired();
            entity.Property(source => source.Url).HasMaxLength(2_000);
            entity.Property(source => source.CanonicalUrl).HasMaxLength(2_000);
            entity.Property(source => source.Title).HasMaxLength(300).IsRequired();
            entity.Property(source => source.Domain).HasMaxLength(160).IsRequired();
            entity.Property(source => source.Publisher).HasMaxLength(300);
            entity.Property(source => source.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(source => source.Snippet).HasMaxLength(2_000);
            entity.Property(source => source.ExtractedText).HasMaxLength(20_000);
            entity.Property(source => source.SearchQuery).HasMaxLength(800);
            entity.Property(source => source.MetadataJson).HasMaxLength(8_000);
            entity.HasIndex(source => new { source.GenerationJobId, source.CitationId }).IsUnique();
            entity.HasIndex(source => new { source.WorkspaceId, source.RetrievedAt });
            entity.HasOne(source => source.Workspace).WithMany().HasForeignKey(source => source.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(source => source.GenerationJob).WithMany(job => job.ResearchSources).HasForeignKey(source => source.GenerationJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(source => source.StoredFile).WithMany().HasForeignKey(source => source.StoredFileId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ResearchEvidence>(entity =>
        {
            entity.HasKey(evidence => evidence.Id);
            entity.Property(evidence => evidence.Topic).HasMaxLength(240).IsRequired();
            entity.Property(evidence => evidence.Excerpt).HasMaxLength(4_000).IsRequired();
            entity.Property(evidence => evidence.Context).HasMaxLength(4_000);
            entity.Property(evidence => evidence.CreatedAt).IsRequired();
            entity.HasIndex(evidence => new { evidence.GenerationJobId, evidence.ResearchSourceId });
            entity.HasIndex(evidence => evidence.WorkspaceId);
            entity.HasOne(evidence => evidence.Workspace).WithMany().HasForeignKey(evidence => evidence.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(evidence => evidence.GenerationJob).WithMany().HasForeignKey(evidence => evidence.GenerationJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(evidence => evidence.ResearchSource).WithMany(source => source.Evidence).HasForeignKey(evidence => evidence.ResearchSourceId).OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(transaction => transaction.CostBasis).HasMaxLength(20);
            entity.Property(transaction => transaction.ProviderCostUsd).HasPrecision(18, 8).IsRequired();
            entity.Property(transaction => transaction.ChargedAmount).HasPrecision(18, 8).IsRequired();
            entity.Property(transaction => transaction.EstimatedProviderCostUsd).HasPrecision(18, 8);
            entity.Property(transaction => transaction.ProviderCostKnown).IsRequired();
            entity.Property(transaction => transaction.PricingVersion).HasMaxLength(100);
            entity.Property(transaction => transaction.PricingSnapshotJson).HasMaxLength(8_000);
            entity.Property(transaction => transaction.CostEstimateJson).HasMaxLength(8_000);
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
        builder.Entity<Plan>(entity =>
        {
            entity.HasKey(plan => plan.Id);
            entity.Property(plan => plan.Code).HasMaxLength(40).IsRequired();
            entity.Property(plan => plan.Name).HasMaxLength(80).IsRequired();
            entity.Property(plan => plan.Description).HasMaxLength(500);
            entity.Property(plan => plan.MonthlyPriceUsd).HasPrecision(18, 2).IsRequired();
            entity.Property(plan => plan.Currency).HasMaxLength(3).IsRequired();
            entity.Property(plan => plan.MonthlyCreditAllowance).IsRequired();
            entity.Property(plan => plan.CreatedAt).IsRequired();
            entity.Property(plan => plan.UpdatedAt).IsRequired();
            entity.HasIndex(plan => plan.Code).IsUnique();
            entity.HasData(DefaultPlanCatalog.All.Select(plan => new
            {
                plan.Id, plan.Code, plan.Name, plan.Description, plan.MonthlyPriceUsd,
                plan.MonthlyCreditAllowance, plan.Currency, plan.IsActive, plan.SortOrder,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }));
        });
        builder.Entity<Subscription>(entity =>
        {
            entity.HasKey(subscription => subscription.Id);
            entity.Property(subscription => subscription.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(subscription => subscription.BillingProvider).HasMaxLength(60);
            entity.Property(subscription => subscription.ProviderSubscriptionReference).HasMaxLength(200);
            entity.Property(subscription => subscription.CreatedAt).IsRequired();
            entity.Property(subscription => subscription.UpdatedAt).IsRequired();
            entity.HasIndex(subscription => subscription.WorkspaceId);
            entity.HasIndex(subscription => new { subscription.Status, subscription.NextRenewalAt });
            entity.HasOne(subscription => subscription.Workspace).WithMany().HasForeignKey(subscription => subscription.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(subscription => subscription.Plan).WithMany(plan => plan.Subscriptions).HasForeignKey(subscription => subscription.PlanId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ProviderCustomerReference>(entity =>
        {
            entity.HasKey(reference => reference.Id);
            entity.Property(reference => reference.Provider).HasMaxLength(60).IsRequired();
            entity.Property(reference => reference.ProviderCustomerReferenceValue).HasMaxLength(240).IsRequired();
            entity.Property(reference => reference.CreatedAt).IsRequired();
            entity.Property(reference => reference.UpdatedAt).IsRequired();
            entity.HasIndex(reference => new { reference.WorkspaceId, reference.Provider, reference.ProviderCustomerReferenceValue }).IsUnique();
            entity.HasIndex(reference => new { reference.WorkspaceId, reference.Provider, reference.IsDefault });
            entity.HasOne(reference => reference.Workspace).WithMany().HasForeignKey(reference => reference.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<CheckoutSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Provider).HasMaxLength(60).IsRequired();
            entity.Property(session => session.ProviderSessionReference).HasMaxLength(240);
            entity.Property(session => session.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(session => session.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.Property(session => session.Currency).HasMaxLength(3).IsRequired();
            entity.Property(session => session.Amount).HasPrecision(18, 2).IsRequired();
            entity.Property(session => session.FailureReason).HasMaxLength(500);
            entity.Property(session => session.CreatedAt).IsRequired();
            entity.HasIndex(session => new { session.WorkspaceId, session.IdempotencyKey }).IsUnique();
            entity.HasIndex(session => new { session.Provider, session.ProviderSessionReference }).IsUnique().HasFilter("\"ProviderSessionReference\" IS NOT NULL");
            entity.HasIndex(session => new { session.Status, session.ExpiresAt });
            entity.HasOne(session => session.Workspace).WithMany().HasForeignKey(session => session.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.Plan).WithMany().HasForeignKey(session => session.PlanId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.Subscription).WithMany().HasForeignKey(session => session.SubscriptionId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<PaymentAttempt>(entity =>
        {
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.Provider).HasMaxLength(60).IsRequired();
            entity.Property(attempt => attempt.ProviderPaymentReference).HasMaxLength(240);
            entity.Property(attempt => attempt.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(attempt => attempt.Amount).HasPrecision(18, 2).IsRequired();
            entity.Property(attempt => attempt.Currency).HasMaxLength(3).IsRequired();
            entity.Property(attempt => attempt.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.Property(attempt => attempt.FailureCode).HasMaxLength(100);
            entity.Property(attempt => attempt.FailureReason).HasMaxLength(500);
            entity.Property(attempt => attempt.CreatedAt).IsRequired();
            entity.Property(attempt => attempt.UpdatedAt).IsRequired();
            entity.HasIndex(attempt => new { attempt.WorkspaceId, attempt.IdempotencyKey }).IsUnique();
            entity.HasIndex(attempt => new { attempt.Provider, attempt.ProviderPaymentReference }).IsUnique().HasFilter("\"ProviderPaymentReference\" IS NOT NULL");
            entity.HasIndex(attempt => new { attempt.WorkspaceId, attempt.CreatedAt });
            entity.HasIndex(attempt => attempt.Status);
            entity.HasOne(attempt => attempt.Workspace).WithMany().HasForeignKey(attempt => attempt.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(attempt => attempt.Subscription).WithMany().HasForeignKey(attempt => attempt.SubscriptionId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(attempt => attempt.CheckoutSession).WithMany(session => session.PaymentAttempts).HasForeignKey(attempt => attempt.CheckoutSessionId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(attempt => attempt.CreditLedgerEntry).WithMany().HasForeignKey(attempt => attempt.CreditLedgerEntryId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<PaymentEvent>(entity =>
        {
            entity.HasKey(paymentEvent => paymentEvent.Id);
            entity.Property(paymentEvent => paymentEvent.Provider).HasMaxLength(60).IsRequired();
            entity.Property(paymentEvent => paymentEvent.ProviderEventReference).HasMaxLength(240).IsRequired();
            entity.Property(paymentEvent => paymentEvent.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(paymentEvent => paymentEvent.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(paymentEvent => paymentEvent.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(paymentEvent => paymentEvent.PayloadJson).HasMaxLength(100_000).IsRequired();
            entity.Property(paymentEvent => paymentEvent.Reason).HasMaxLength(500);
            entity.Property(paymentEvent => paymentEvent.FailureReason).HasMaxLength(500);
            entity.Property(paymentEvent => paymentEvent.OccurredAt).IsRequired();
            entity.Property(paymentEvent => paymentEvent.ReceivedAt).IsRequired();
            entity.HasIndex(paymentEvent => new { paymentEvent.Provider, paymentEvent.ProviderEventReference }).IsUnique();
            entity.HasIndex(paymentEvent => new { paymentEvent.Status, paymentEvent.ReceivedAt });
            entity.HasIndex(paymentEvent => paymentEvent.WorkspaceId);
            entity.HasOne(paymentEvent => paymentEvent.Workspace).WithMany().HasForeignKey(paymentEvent => paymentEvent.WorkspaceId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(paymentEvent => paymentEvent.Subscription).WithMany().HasForeignKey(paymentEvent => paymentEvent.SubscriptionId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(paymentEvent => paymentEvent.CheckoutSession).WithMany().HasForeignKey(paymentEvent => paymentEvent.CheckoutSessionId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(paymentEvent => paymentEvent.PaymentAttempt).WithMany().HasForeignKey(paymentEvent => paymentEvent.PaymentAttemptId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<PaymentRefund>(entity =>
        {
            entity.HasKey(refund => refund.Id);
            entity.Property(refund => refund.Provider).HasMaxLength(60).IsRequired();
            entity.Property(refund => refund.ProviderRefundReference).HasMaxLength(240);
            entity.Property(refund => refund.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(refund => refund.Amount).HasPrecision(18, 2).IsRequired();
            entity.Property(refund => refund.Currency).HasMaxLength(3).IsRequired();
            entity.Property(refund => refund.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.Property(refund => refund.Reason).HasMaxLength(500).IsRequired();
            entity.Property(refund => refund.FailureReason).HasMaxLength(500);
            entity.Property(refund => refund.CreatedAt).IsRequired();
            entity.Property(refund => refund.UpdatedAt).IsRequired();
            entity.HasIndex(refund => new { refund.WorkspaceId, refund.IdempotencyKey }).IsUnique();
            entity.HasIndex(refund => new { refund.Provider, refund.ProviderRefundReference }).IsUnique().HasFilter("\"ProviderRefundReference\" IS NOT NULL");
            entity.HasIndex(refund => new { refund.PaymentAttemptId, refund.CreatedAt });
            entity.HasOne(refund => refund.Workspace).WithMany().HasForeignKey(refund => refund.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(refund => refund.PaymentAttempt).WithMany(attempt => attempt.Refunds).HasForeignKey(refund => refund.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SubscriptionLifecycleEvent>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(item => item.Source).HasMaxLength(60).IsRequired();
            entity.Property(item => item.Reason).HasMaxLength(500).IsRequired();
            entity.Property(item => item.ProviderEventReference).HasMaxLength(240);
            entity.Property(item => item.CreatedAt).IsRequired();
            entity.HasIndex(item => new { item.SubscriptionId, item.CreatedAt });
            entity.HasIndex(item => item.ProviderEventReference);
            entity.HasOne(item => item.Workspace).WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Subscription).WithMany().HasForeignKey(item => item.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<PaymentReconciliationRecord>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Provider).HasMaxLength(60).IsRequired();
            entity.Property(item => item.ProviderObjectType).HasMaxLength(60).IsRequired();
            entity.Property(item => item.ProviderObjectReference).HasMaxLength(240).IsRequired();
            entity.Property(item => item.LocalEntityType).HasMaxLength(60);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(item => item.Reason).HasMaxLength(500).IsRequired();
            entity.Property(item => item.CreatedAt).IsRequired();
            entity.Property(item => item.UpdatedAt).IsRequired();
            entity.HasIndex(item => new { item.Provider, item.ProviderObjectType, item.ProviderObjectReference }).IsUnique();
            entity.HasIndex(item => new { item.Status, item.UpdatedAt });
            entity.HasIndex(item => item.WorkspaceId);
            entity.HasOne(item => item.Workspace).WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<BillingPeriod>(entity =>
        {
            entity.HasKey(period => period.Id);
            entity.Property(period => period.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(period => period.IncludedCredits).IsRequired();
            entity.Property(period => period.CreatedAt).IsRequired();
            entity.HasIndex(period => new { period.SubscriptionId, period.StartsAt }).IsUnique();
            entity.HasIndex(period => new { period.Status, period.EndsAt });
            entity.HasOne(period => period.Subscription).WithMany(subscription => subscription.BillingPeriods).HasForeignKey(period => period.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<CreditEntitlement>(entity =>
        {
            entity.HasKey(entitlement => entitlement.Id);
            entity.Property(entitlement => entitlement.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(entitlement => entitlement.GrantedCredits).IsRequired();
            entity.Property(entitlement => entitlement.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.Property(entitlement => entitlement.SourceReference).HasMaxLength(200);
            entity.Property(entitlement => entitlement.CreatedAt).IsRequired();
            entity.HasIndex(entitlement => new { entitlement.WorkspaceId, entitlement.IdempotencyKey }).IsUnique();
            entity.HasIndex(entitlement => new { entitlement.WorkspaceId, entitlement.ExpiresAt });
            entity.HasOne(entitlement => entitlement.Workspace).WithMany().HasForeignKey(entitlement => entitlement.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entitlement => entitlement.BillingPeriod).WithMany(period => period.CreditEntitlements).HasForeignKey(entitlement => entitlement.BillingPeriodId).OnDelete(DeleteBehavior.SetNull);
        });
        builder.Entity<CreditLedgerEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(entry => entry.Amount).IsRequired();
            entity.Property(entry => entry.IdempotencyKey).HasMaxLength(180).IsRequired();
            entity.Property(entry => entry.Reason).HasMaxLength(500).IsRequired();
            entity.Property(entry => entry.CreatedAt).IsRequired();
            entity.HasIndex(entry => new { entry.WorkspaceId, entry.IdempotencyKey }).IsUnique();
            entity.HasIndex(entry => new { entry.WorkspaceId, entry.CreatedAt });
            entity.HasIndex(entry => entry.UsageTransactionId);
            entity.HasIndex(entry => entry.ReversesEntryId);
            entity.HasOne(entry => entry.Workspace).WithMany().HasForeignKey(entry => entry.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.CreditEntitlement).WithMany(entitlement => entitlement.LedgerEntries).HasForeignKey(entry => entry.CreditEntitlementId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entry => entry.UsageTransaction).WithMany().HasForeignKey(entry => entry.UsageTransactionId).OnDelete(DeleteBehavior.SetNull);
            entity.ToTable("CreditLedgerEntries", table => table.HasCheckConstraint("CK_CreditLedgerEntries_NonZeroAmount", "\"Amount\" <> 0"));
        });
    }
}
