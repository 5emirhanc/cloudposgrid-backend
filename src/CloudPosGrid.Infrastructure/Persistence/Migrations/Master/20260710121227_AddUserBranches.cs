using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPosGrid.Infrastructure.Persistence.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddUserBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "BranchIds",
                schema: "public",
                table: "users",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            // Geriye dönük veri: mevcut tek-şube atamaları yeni izinli-şube kümesine taşınır.
            // (BranchId artık yetki kaynağı değil; boş küme = kısıtsız kullanıcı.)
            migrationBuilder.Sql("""
                UPDATE public.users
                SET "BranchIds" = ARRAY["BranchId"]
                WHERE "BranchId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BranchIds",
                schema: "public",
                table: "users");
        }
    }
}
