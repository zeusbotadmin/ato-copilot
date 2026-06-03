using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ato.Copilot.Core.Migrations
{
    /// <summary>
    /// Feature 050 — Epic #124: CSP Capability Lifecycle
    /// Creates CspCapabilities and CapabilityHistoryEvents tables.
    /// Issues: #158 (entity/enum), #160 (NeedsReview gate capability model).
    /// </summary>
    public partial class Feature050_CapabilityLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CspCapabilities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ParentCapabilityId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    NeedsReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CspCapabilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CapabilityHistoryEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapabilityId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapabilityHistoryEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CspCapability_Status",
                table: "CspCapabilities",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CspCapability_ParentId",
                table: "CspCapabilities",
                column: "ParentCapabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_CapabilityHistoryEvent_CapabilityId",
                table: "CapabilityHistoryEvents",
                column: "CapabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_CapabilityHistoryEvent_OccurredAt",
                table: "CapabilityHistoryEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CapabilityHistoryEvents");
            migrationBuilder.DropTable(name: "CspCapabilities");
        }
    }
}
