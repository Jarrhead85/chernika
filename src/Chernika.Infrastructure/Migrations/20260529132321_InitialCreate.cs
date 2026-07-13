using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssemblyUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssemblyUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CoefficientTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Group = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoefficientTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EquipmentModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Brand = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Modification = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GsmMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Gost = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GsmMaterials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Position = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Coefficients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CoefficientTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConditionDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Coefficients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Coefficients_CoefficientTypes_CoefficientTypeId",
                        column: x => x.CoefficientTypeId,
                        principalTable: "CoefficientTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EquipmentInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SerialNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EquipmentModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EquipmentInstances_EquipmentModels_EquipmentModelId",
                        column: x => x.EquipmentModelId,
                        principalTable: "EquipmentModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductCompositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCompositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCompositions_EquipmentModels_EquipmentModelId",
                        column: x => x.EquipmentModelId,
                        principalTable: "EquipmentModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HKCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NormativeBasis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCards_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HKCards_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleClaims_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserClaims_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_UserLogins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_UserTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HKCardItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssemblyUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Volume = table.Column<decimal>(type: "numeric", nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Periodicity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardItems_AssemblyUnits_AssemblyUnitId",
                        column: x => x.AssemblyUnitId,
                        principalTable: "AssemblyUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HKCardItems_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HKCardStatusLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ToStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardStatusLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardStatusLogs_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndividualCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TotalNorm = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCards_EquipmentInstances_EquipmentInstanceId",
                        column: x => x.EquipmentInstanceId,
                        principalTable: "EquipmentInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IndividualCards_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IndividualCards_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductCompositionNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductCompositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCompositionNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCompositionNodes_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProductCompositionNodes_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductCompositionNodes_ProductCompositions_ProductComposit~",
                        column: x => x.ProductCompositionId,
                        principalTable: "ProductCompositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HKCardItemMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    GsmMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardItemMaterials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardItemMaterials_GsmMaterials_GsmMaterialId",
                        column: x => x.GsmMaterialId,
                        principalTable: "GsmMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HKCardItemMaterials_HKCardItems_HKCardItemId",
                        column: x => x.HKCardItemId,
                        principalTable: "HKCardItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CoefficientIndividualCard",
                columns: table => new
                {
                    AppliedCoefficientsId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardsId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoefficientIndividualCard", x => new { x.AppliedCoefficientsId, x.IndividualCardsId });
                    table.ForeignKey(
                        name: "FK_CoefficientIndividualCard_Coefficients_AppliedCoefficientsId",
                        column: x => x.AppliedCoefficientsId,
                        principalTable: "Coefficients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CoefficientIndividualCard_IndividualCards_IndividualCardsId",
                        column: x => x.IndividualCardsId,
                        principalTable: "IndividualCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndividualCardItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseVolume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CalculatedVolume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardItems_HKCardItems_HKCardItemId",
                        column: x => x.HKCardItemId,
                        principalTable: "HKCardItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IndividualCardItems_IndividualCards_IndividualCardId",
                        column: x => x.IndividualCardId,
                        principalTable: "IndividualCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssemblyUnits_Code",
                table: "AssemblyUnits",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoefficientIndividualCard_IndividualCardsId",
                table: "CoefficientIndividualCard",
                column: "IndividualCardsId");

            migrationBuilder.CreateIndex(
                name: "IX_Coefficients_CoefficientTypeId",
                table: "Coefficients",
                column: "CoefficientTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentInstances_EquipmentModelId",
                table: "EquipmentInstances",
                column: "EquipmentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItemMaterials_GsmMaterialId",
                table: "HKCardItemMaterials",
                column: "GsmMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItemMaterials_HKCardItemId",
                table: "HKCardItemMaterials",
                column: "HKCardItemId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItems_AssemblyUnitId",
                table: "HKCardItems",
                column: "AssemblyUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItems_HKCardId",
                table: "HKCardItems",
                column: "HKCardId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_BranchId",
                table: "HKCards",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_Code",
                table: "HKCards",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_NodeId",
                table: "HKCards",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardStatusLogs_HKCardId",
                table: "HKCardStatusLogs",
                column: "HKCardId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardItems_HKCardItemId",
                table: "IndividualCardItems",
                column: "HKCardItemId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardItems_IndividualCardId",
                table: "IndividualCardItems",
                column: "IndividualCardId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_EquipmentInstanceId",
                table: "IndividualCards",
                column: "EquipmentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_HKCardId",
                table: "IndividualCards",
                column: "HKCardId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_NodeId",
                table: "IndividualCards",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Code",
                table: "Nodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_HKCardId",
                table: "ProductCompositionNodes",
                column: "HKCardId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_NodeId",
                table: "ProductCompositionNodes",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_ProductCompositionId",
                table: "ProductCompositionNodes",
                column: "ProductCompositionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositions_EquipmentModelId",
                table: "ProductCompositions",
                column: "EquipmentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleClaims_RoleId",
                table: "RoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "Roles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserClaims_UserId",
                table: "UserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLogins_UserId",
                table: "UserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "Users",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Users_BranchId",
                table: "Users",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "Users",
                column: "NormalizedUserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "CoefficientIndividualCard");

            migrationBuilder.DropTable(
                name: "HKCardItemMaterials");

            migrationBuilder.DropTable(
                name: "HKCardStatusLogs");

            migrationBuilder.DropTable(
                name: "IndividualCardItems");

            migrationBuilder.DropTable(
                name: "ProductCompositionNodes");

            migrationBuilder.DropTable(
                name: "RoleClaims");

            migrationBuilder.DropTable(
                name: "UserClaims");

            migrationBuilder.DropTable(
                name: "UserLogins");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "UserTokens");

            migrationBuilder.DropTable(
                name: "WorkTasks");

            migrationBuilder.DropTable(
                name: "Coefficients");

            migrationBuilder.DropTable(
                name: "GsmMaterials");

            migrationBuilder.DropTable(
                name: "HKCardItems");

            migrationBuilder.DropTable(
                name: "IndividualCards");

            migrationBuilder.DropTable(
                name: "ProductCompositions");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "CoefficientTypes");

            migrationBuilder.DropTable(
                name: "AssemblyUnits");

            migrationBuilder.DropTable(
                name: "EquipmentInstances");

            migrationBuilder.DropTable(
                name: "HKCards");

            migrationBuilder.DropTable(
                name: "EquipmentModels");

            migrationBuilder.DropTable(
                name: "Branches");

            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}
