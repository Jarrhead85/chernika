using Chernika.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Complex> Complexes => Set<Complex>();
    public DbSet<Aggregate> Aggregates => Set<Aggregate>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<AssemblyUnit> AssemblyUnits => Set<AssemblyUnit>();
    public DbSet<GsmMaterial> GsmMaterials => Set<GsmMaterial>();
    public DbSet<EquipmentModel> EquipmentModels => Set<EquipmentModel>();
    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();
    public DbSet<EquipmentInstance> EquipmentInstances => Set<EquipmentInstance>();
    public DbSet<ProductComposition> ProductCompositions => Set<ProductComposition>();
    public DbSet<ProductCompositionPart> ProductCompositionParts => Set<ProductCompositionPart>();
    public DbSet<ProductCompositionAggregate> ProductCompositionAggregates => Set<ProductCompositionAggregate>();
    public DbSet<AggregateComposition> AggregateCompositions => Set<AggregateComposition>();
    public DbSet<AggregateCompositionNode> AggregateCompositionNodes => Set<AggregateCompositionNode>();
    public DbSet<ComplexComposition> ComplexCompositions => Set<ComplexComposition>();
    public DbSet<ComplexCompositionItem> ComplexCompositionItems => Set<ComplexCompositionItem>();
    public DbSet<HKCard> HKCards => Set<HKCard>();
    public DbSet<HKCardComponent> HKCardComponents => Set<HKCardComponent>();
    public DbSet<HKCardItem> HKCardItems => Set<HKCardItem>();
    public DbSet<HKCardItemMaterial> HKCardItemMaterials => Set<HKCardItemMaterial>();
    public DbSet<HKCardStatusLog> HKCardStatusLogs => Set<HKCardStatusLog>();
    public DbSet<CoefficientType> CoefficientTypes => Set<CoefficientType>();
    public DbSet<Coefficient> Coefficients => Set<Coefficient>();
    public DbSet<IndividualCard> IndividualCards => Set<IndividualCard>();
    public DbSet<IndividualCardItem> IndividualCardItems => Set<IndividualCardItem>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<WorkTask> WorkTasks => Set<WorkTask>();
    public DbSet<WorkTaskGroup> WorkTaskGroups => Set<WorkTaskGroup>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RolePermissionTemplate> RolePermissionTemplates => Set<RolePermissionTemplate>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<MilitaryBranch> MilitaryBranches => Set<MilitaryBranch>();
    public DbSet<HKCardMilitaryBranch> HKCardMilitaryBranches => Set<HKCardMilitaryBranch>();
    public DbSet<HKCardAttachment> HKCardAttachments => Set<HKCardAttachment>();
    public DbSet<ReferenceProposal> ReferenceProposals => Set<ReferenceProposal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApplicationUser>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.FullName).HasMaxLength(256);
            e.Property(x => x.Position).HasMaxLength(256);
            e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IdentityRole>(e =>
        {
            e.ToTable("Roles");
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.NormalizedName).HasMaxLength(256);
        });

        modelBuilder.Entity<IdentityUserRole<string>>(e => e.ToTable("UserRoles"));
        modelBuilder.Entity<IdentityUserClaim<string>>(e => e.ToTable("UserClaims"));
        modelBuilder.Entity<IdentityUserLogin<string>>(e => e.ToTable("UserLogins"));
        modelBuilder.Entity<IdentityRoleClaim<string>>(e => e.ToTable("RoleClaims"));
        modelBuilder.Entity<IdentityUserToken<string>>(e => e.ToTable("UserTokens"));

        modelBuilder.Entity<Branch>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Code).HasMaxLength(50);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<Complex>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<Aggregate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasIndex(x => x.Code).IsUnique().HasFilter("\"IsDeleted\" = false");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<Node>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<AssemblyUnit>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<GsmMaterial>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Type).HasMaxLength(128).IsRequired();
            e.Property(x => x.Gost).HasMaxLength(128);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<EquipmentModel>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Index).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Type).HasMaxLength(128);
            e.Property(x => x.Brand).HasMaxLength(128);
            e.Property(x => x.Modification).HasMaxLength(128);
            e.HasOne(x => x.EquipmentType).WithMany().HasForeignKey(x => x.EquipmentTypeId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<EquipmentType>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TypeGroup).HasMaxLength(250);
            e.Property(x => x.Name).HasMaxLength(250).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<EquipmentInstance>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SerialNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.Index).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.HasOne(x => x.EquipmentModel).WithMany(x => x.Instances).HasForeignKey(x => x.EquipmentModelId);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<ProductComposition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(10).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasOne(x => x.EquipmentModel).WithMany(x => x.ProductCompositions).HasForeignKey(x => x.EquipmentModelId);
            e.HasOne(x => x.SupersedesProductComposition).WithMany(x => x.SupersededByCompositions)
                .HasForeignKey(x => x.SupersedesProductCompositionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SupersedesProductCompositionId)
                .HasDatabaseName("IX_ProductCompositions_SupersedesProductCompositionId");
            e.HasIndex(x => new { x.EquipmentModelId, x.Status });
            e.HasIndex(x => new { x.Status, x.EffectiveDate });
            e.HasIndex(x => new { x.EquipmentModelId, x.IsActive });
            e.HasIndex(x => x.BranchId).HasDatabaseName("IX_ProductCompositions_BranchId");
        });

        modelBuilder.Entity<ProductCompositionPart>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasOne(x => x.ProductComposition).WithMany(x => x.Parts).HasForeignKey(x => x.ProductCompositionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProductCompositionId, x.SortOrder });
        });

        modelBuilder.Entity<ProductCompositionAggregate>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.ProductComposition).WithMany().HasForeignKey(x => x.ProductCompositionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Part).WithMany(x => x.Aggregates).HasForeignKey(x => x.PartId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Aggregate).WithMany(x => x.ProductCompositionAggregates).HasForeignKey(x => x.AggregateId);
            e.HasIndex(x => new { x.ProductCompositionId, x.AggregateId }).IsUnique();
        });

        modelBuilder.Entity<AggregateComposition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(10).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasOne(x => x.Aggregate).WithMany(x => x.AggregateCompositions).HasForeignKey(x => x.AggregateId);
            e.HasOne(x => x.SupersedesAggregateComposition).WithMany(x => x.SupersededByCompositions)
                .HasForeignKey(x => x.SupersedesAggregateCompositionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SupersedesAggregateCompositionId)
                .HasDatabaseName("IX_AggregateCompositions_SupersedesAggregateCompositionId");
            e.HasIndex(x => new { x.AggregateId, x.Status });
            e.HasIndex(x => new { x.Status, x.EffectiveDate });
            e.HasIndex(x => new { x.AggregateId, x.IsActive });
            e.HasIndex(x => x.BranchId).HasDatabaseName("IX_AggregateCompositions_BranchId");
        });

        modelBuilder.Entity<AggregateCompositionNode>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.AggregateComposition).WithMany(x => x.Nodes).HasForeignKey(x => x.AggregateCompositionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Node).WithMany(x => x.AggregateCompositionNodes).HasForeignKey(x => x.NodeId);
            e.HasIndex(x => new { x.AggregateCompositionId, x.NodeId }).IsUnique();
        });

        modelBuilder.Entity<ComplexComposition>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(10).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasOne(x => x.Complex).WithMany(x => x.ComplexCompositions).HasForeignKey(x => x.ComplexId);
            e.HasOne(x => x.SupersedesComplexComposition).WithMany(x => x.SupersededByCompositions)
                .HasForeignKey(x => x.SupersedesComplexCompositionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SupersedesComplexCompositionId)
                .HasDatabaseName("IX_ComplexCompositions_SupersedesComplexCompositionId");
            e.HasIndex(x => new { x.ComplexId, x.Status });
            e.HasIndex(x => new { x.Status, x.EffectiveDate });
            e.HasIndex(x => new { x.ComplexId, x.IsActive });
            e.HasIndex(x => x.BranchId).HasDatabaseName("IX_ComplexCompositions_BranchId");
        });

        modelBuilder.Entity<ComplexCompositionItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.ComplexComposition).WithMany(x => x.Items).HasForeignKey(x => x.ComplexCompositionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.EquipmentModel).WithMany(x => x.ComplexCompositionItems).HasForeignKey(x => x.EquipmentModelId);
            e.HasIndex(x => new { x.ComplexCompositionId, x.EquipmentModelId }).IsUnique();
        });

        modelBuilder.Entity<HKCard>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Version).HasMaxLength(10).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ObjectLevel).HasConversion<int>();
            e.Property(x => x.Purpose).HasMaxLength(2000);
            e.Property(x => x.NormativeBasis).HasMaxLength(2000);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .IsRowVersion();
            e.HasOne(x => x.Branch).WithMany(x => x.HKCards).HasForeignKey(x => x.BranchId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Complex).WithMany(x => x.HKCards).HasForeignKey(x => x.ComplexId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.EquipmentModel).WithMany(x => x.HKCards).HasForeignKey(x => x.EquipmentModelId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Aggregate).WithMany(x => x.HKCards).HasForeignKey(x => x.AggregateId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Node).WithMany(x => x.HKCards).HasForeignKey(x => x.NodeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SupersedesHKCard).WithMany(x => x.SupersededBy)
                .HasForeignKey(x => x.SupersedesHKCardId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SupersedesHKCardId)
                .HasDatabaseName("IX_HKCards_SupersedesHKCardId");
            e.HasIndex(x => new { x.Code, x.Version }).IsUnique();
            e.HasIndex(x => x.NodeId)
                .HasDatabaseName("UX_HKCards_OneActivePerNode")
                .HasFilter("\"ObjectLevel\" = 4 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')")
                .IsUnique();
            e.HasIndex(x => x.AggregateId)
                .HasDatabaseName("UX_HKCards_OneActivePerAggregate")
                .HasFilter("\"ObjectLevel\" = 3 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')")
                .IsUnique();
            e.HasIndex(x => x.EquipmentModelId)
                .HasDatabaseName("UX_HKCards_OneActivePerEquipmentModel")
                .HasFilter("\"ObjectLevel\" = 2 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')")
                .IsUnique();
            e.HasIndex(x => x.ComplexId)
                .HasDatabaseName("UX_HKCards_OneActivePerComplex")
                .HasFilter("\"ObjectLevel\" = 1 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')")
                .IsUnique();
            e.HasQueryFilter(x => x.Status != Chernika.Domain.Enums.HKCardStatus.Deleted);
        });

        modelBuilder.Entity<HKCardComponent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.AddedByUserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.ChildCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.ChildVersion).HasMaxLength(10).IsRequired();
            e.HasOne(x => x.ParentHKCard).WithMany(x => x.ParentComponents)
                .HasForeignKey(x => x.ParentHKCardId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ChildHKCard).WithMany(x => x.ChildComponents)
                .HasForeignKey(x => x.ChildHKCardId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ParentHKCardId, x.ChildHKCardId }).IsUnique();
        });

        modelBuilder.Entity<HKCardItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UnitOfMeasure).HasMaxLength(20);
            e.Property(x => x.Periodicity).HasMaxLength(256);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Volume).HasPrecision(18, 6);
            e.HasOne(x => x.HKCard).WithMany(x => x.Items).HasForeignKey(x => x.HKCardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AssemblyUnit).WithMany(x => x.HKCardItems).HasForeignKey(x => x.AssemblyUnitId);
        });

        modelBuilder.Entity<HKCardItemMaterial>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.HKCardItem).WithMany(x => x.Materials).HasForeignKey(x => x.HKCardItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.GsmMaterial).WithMany(x => x.HKCardItemMaterials).HasForeignKey(x => x.GsmMaterialId);
        });

        modelBuilder.Entity<HKCardStatusLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasOne(x => x.HKCard).WithMany(x => x.StatusLog).HasForeignKey(x => x.HKCardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CoefficientType>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Group).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<Coefficient>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.ConditionDescription).HasMaxLength(2000);
            e.Property(x => x.Value).HasPrecision(18, 6);
            e.HasOne(x => x.CoefficientType).WithMany(x => x.Coefficients).HasForeignKey(x => x.CoefficientTypeId);
        });

        modelBuilder.Entity<IndividualCard>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(10).IsRequired();
            e.Property(x => x.TotalNorm).HasPrecision(18, 6);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.HasOne(x => x.EquipmentInstance).WithMany(x => x.IndividualCards).HasForeignKey(x => x.EquipmentInstanceId);
            e.HasOne(x => x.HKCard).WithMany().HasForeignKey(x => x.HKCardId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId);
            e.HasOne(x => x.ProductComposition).WithMany().HasForeignKey(x => x.ProductCompositionId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.AppliedCoefficients).WithMany(x => x.IndividualCards);
        });

        modelBuilder.Entity<IndividualCardItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BaseVolume).HasPrecision(18, 6);
            e.Property(x => x.CalculatedVolume).HasPrecision(18, 6);
            e.HasOne(x => x.IndividualCard).WithMany(x => x.Items).HasForeignKey(x => x.IndividualCardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.HKCardItem).WithMany(x => x.IndividualCardItems).HasForeignKey(x => x.HKCardItemId);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.Details).HasMaxLength(4000);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        modelBuilder.Entity<WorkTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(512).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Priority).HasConversion<int>();
            e.Property(x => x.CreatedByUserId).HasMaxLength(450);
            e.Property(x => x.AssignedToUserId).HasMaxLength(450);
            e.Property(x => x.AssignedRole).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.EntityCodeSnapshot).HasMaxLength(100);
            e.Property(x => x.EntityTitleSnapshot).HasMaxLength(512);
            e.Property(x => x.CompletedByUserId).HasMaxLength(450);
            e.Property(x => x.CompletionComment).HasMaxLength(4000);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AssignedToUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.WorkTaskGroup).WithMany().HasForeignKey(x => x.WorkTaskGroupId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.WorkTaskGroupId).HasDatabaseName("IX_WorkTasks_WorkTaskGroupId");
            e.HasIndex(x => new { x.AssignedToUserId, x.Status, x.IsDeleted })
                .HasDatabaseName("IX_WorkTasks_AssignedToUserId_Status_IsDeleted");
            e.HasIndex(x => new { x.AssignedRole, x.Status, x.IsDeleted })
                .HasDatabaseName("IX_WorkTasks_AssignedRole_Status_IsDeleted");
            e.HasIndex(x => new { x.BranchId, x.Status })
                .HasDatabaseName("IX_WorkTasks_BranchId_Status");
            e.HasIndex(x => new { x.EntityType, x.EntityId })
                .HasDatabaseName("IX_WorkTasks_EntityType_EntityId");
            e.HasIndex(x => new { x.DueDateUtc, x.Status })
                .HasDatabaseName("IX_WorkTasks_DueDateUtc_Status");
            e.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("IX_WorkTasks_CreatedAtUtc");
            e.ToTable(t => t.HasCheckConstraint(
                "CK_WorkTasks_Assignee",
                "\"AssignedToUserId\" IS NOT NULL OR \"AssignedRole\" IS NOT NULL"));
            e.ToTable(t => t.HasCheckConstraint(
                "CK_WorkTasks_CompletedAt",
                "\"CompletedAtUtc\" IS NULL OR \"Status\" IN (3, 4)"));
        });

        modelBuilder.Entity<WorkTaskGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TaskType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Title).HasMaxLength(512).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.CompletedByUserId).HasMaxLength(450);
            e.Property(x => x.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .IsRowVersion();
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CompletedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.WorkTasks).WithOne(x => x.WorkTaskGroup).HasForeignKey(x => x.WorkTaskGroupId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.BranchId })
                .HasDatabaseName("IX_WorkTaskGroups_EntityType_EntityId_BranchId");
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Type).HasConversion<int>();
            e.Property(x => x.Channel).HasConversion<int>();
            e.Property(x => x.Title).HasMaxLength(512).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.NavigationUrl).HasMaxLength(500);
            e.Property(x => x.DeduplicationKey).HasMaxLength(400);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.WorkTaskId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAtUtc })
                .HasDatabaseName("IX_Notifications_UserId_IsRead_CreatedAtUtc");
            e.HasIndex(x => new { x.UserId, x.ExpiresAtUtc })
                .HasDatabaseName("IX_Notifications_UserId_ExpiresAtUtc");
            e.HasIndex(x => x.WorkTaskId).HasDatabaseName("IX_Notifications_WorkTaskId");
            e.HasIndex(x => new { x.EntityType, x.EntityId })
                .HasDatabaseName("IX_Notifications_EntityType_EntityId");
            e.HasIndex(x => new { x.UserId, x.DeduplicationKey })
                .HasDatabaseName("UX_Notifications_DeduplicationKey")
                .IsUnique()
                .HasFilter("\"DeduplicationKey\" IS NOT NULL");
        });

        modelBuilder.Entity<RolePermissionTemplate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RoleName).HasMaxLength(100).IsRequired();
            e.Property(x => x.PermissionCode).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.RoleName, x.PermissionCode }).IsUnique()
                .HasDatabaseName("UX_RolePermissionTemplates_RoleName_PermissionCode");
        });

        modelBuilder.Entity<UserPermissionOverride>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.PermissionCode).HasMaxLength(100).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.GrantedByUserId).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.UserId, x.PermissionCode }).IsUnique()
                .HasDatabaseName("UX_UserPermissionOverrides_UserId_PermissionCode");
            e.HasIndex(x => x.UserId).HasDatabaseName("IX_UserPermissionOverrides_UserId");
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.GrantedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MilitaryBranch>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ArmedForcesType).HasMaxLength(250).IsRequired();
            e.Property(x => x.Name).HasMaxLength(250).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        modelBuilder.Entity<HKCardMilitaryBranch>(e =>
        {
            e.HasKey(x => new { x.HKCardId, x.MilitaryBranchId });
            e.HasOne(x => x.HKCard).WithMany(x => x.MilitaryBranches)
                .HasForeignKey(x => x.HKCardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.MilitaryBranch).WithMany()
                .HasForeignKey(x => x.MilitaryBranchId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HKCardAttachment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(128).IsRequired();
            e.Property(x => x.UploadedByUserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.UploadedByUserName).HasMaxLength(256);
            e.HasIndex(x => x.HKCardId).IsUnique();
            e.HasOne(x => x.HKCard).WithOne(x => x.Attachment)
                .HasForeignKey<HKCardAttachment>(x => x.HKCardId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReferenceProposal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(500).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Gost).HasMaxLength(200);
            e.Property(x => x.Type).HasMaxLength(200);
            e.Property(x => x.CreatedByUserId).HasMaxLength(450).IsRequired();
            e.HasOne(x => x.HKCard).WithMany(x => x.Proposals)
                .HasForeignKey(x => x.HKCardId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HKCardId).HasDatabaseName("IX_ReferenceProposals_HKCardId");
        });
    }
}
