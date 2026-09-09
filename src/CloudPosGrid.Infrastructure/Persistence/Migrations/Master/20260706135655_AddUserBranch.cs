using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPosGrid.Infrastructure.Persistence.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddUserBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                schema: "public",
                table: "users",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BranchId",
                schema: "public",
                table: "users");
        }
    }
}
