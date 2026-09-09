using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPosGrid.Infrastructure.Persistence.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddDealer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DealerId",
                schema: "public",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dealers",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CommissionRate = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dealers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_DealerId",
                schema: "public",
                table: "tenants",
                column: "DealerId");

            migrationBuilder.CreateIndex(
                name: "IX_dealers_Code",
                schema: "public",
                table: "dealers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dealers_Email",
                schema: "public",
                table: "dealers",
                column: "Email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_tenants_dealers_DealerId",
                schema: "public",
                table: "tenants",
                column: "DealerId",
                principalSchema: "public",
                principalTable: "dealers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tenants_dealers_DealerId",
                schema: "public",
                table: "tenants");

            migrationBuilder.DropTable(
                name: "dealers",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "IX_tenants_DealerId",
                schema: "public",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "DealerId",
                schema: "public",
                table: "tenants");
        }
    }
}
