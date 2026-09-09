using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPosGrid.Infrastructure.Persistence.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddMasterConcurrencyPendingUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT: xmin bir PostgreSQL SİSTEM sütunudur (her tabloda hazır bulunur) — EF differ'ının
            // ürettiği AddColumn("xmin") satırları KALDIRILDI; aksi halde çalışma anında "column name
            // 'xmin' conflicts with a system column name" (42701) hatası verirdi. xmin yalnızca iyimser
            // eşzamanlılık jetonu olarak yapılandırılır (MasterDbContext); DDL gerektirmez.
            migrationBuilder.CreateIndex(
                name: "IX_subscription_requests_TenantId_Pending",
                schema: "public",
                table: "subscription_requests",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_subscription_requests_TenantId_Pending",
                schema: "public",
                table: "subscription_requests");
        }
    }
}
