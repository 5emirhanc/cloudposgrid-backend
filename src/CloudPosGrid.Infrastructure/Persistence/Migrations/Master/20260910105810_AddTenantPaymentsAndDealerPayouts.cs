using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPosGrid.Infrastructure.Persistence.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddTenantPaymentsAndDealerPayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dealer_payouts",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DealerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dealer_payouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dealer_payouts_dealers_DealerId",
                        column: x => x.DealerId,
                        principalSchema: "public",
                        principalTable: "dealers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_payments",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Plan = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BillingCycle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DealerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommissionRate = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    CommissionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_payments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dealer_payouts_DealerId",
                schema: "public",
                table: "dealer_payouts",
                column: "DealerId");

            migrationBuilder.CreateIndex(
                name: "IX_dealer_payouts_PaidAt",
                schema: "public",
                table: "dealer_payouts",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_payments_DealerId",
                schema: "public",
                table: "tenant_payments",
                column: "DealerId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_payments_PaidAt",
                schema: "public",
                table: "tenant_payments",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_payments_TenantId",
                schema: "public",
                table: "tenant_payments",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dealer_payouts",
                schema: "public");

            migrationBuilder.DropTable(
                name: "tenant_payments",
                schema: "public");
        }
    }
}
