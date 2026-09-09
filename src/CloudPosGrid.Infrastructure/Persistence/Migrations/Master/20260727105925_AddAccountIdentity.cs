using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudPosGrid.Infrastructure.Persistence.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddAccountIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #47 çok-şirket: kimlik (email/şifre/2FA/kilit) User'dan yeni "accounts" tablosuna taşınır.
            // Mevcut her User → bir Account'a kopyalanır (bugün email global-unique olduğundan 1:1), sonra
            // User.AccountId bağlanır. Sıra önemli: önce accounts + nullable kolon + backfill, SONRA NOT NULL/FK.

            // 1) accounts tablosu (backfill hedefi).
            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorSecret = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RecoveryCodesJson = table.Column<string>(type: "text", nullable: true),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Email",
                schema: "public",
                table: "accounts",
                column: "Email",
                unique: true);

            // 2) users.AccountId — önce NULLABLE (mevcut satırlar backfill'e kadar boş kalabilsin).
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                schema: "public",
                table: "users",
                type: "uuid",
                nullable: true);

            // 3) BACKFILL: her user için bir account oluştur (kimlik alanlarını kopyala), email ile bağla.
            migrationBuilder.Sql(
                """
                INSERT INTO public.accounts
                    ("Id","Email","PasswordHash","TwoFactorEnabled","TwoFactorSecret","RecoveryCodesJson","FailedLoginCount","LockoutEndUtc","CreatedAt","UpdatedAt")
                SELECT gen_random_uuid(), u."Email", u."PasswordHash", u."TwoFactorEnabled", u."TwoFactorSecret",
                       u."RecoveryCodesJson", u."FailedLoginCount", u."LockoutEndUtc", u."CreatedAt", u."UpdatedAt"
                FROM public.users u;
                """);
            migrationBuilder.Sql(
                """
                UPDATE public.users u
                SET "AccountId" = a."Id"
                FROM public.accounts a
                WHERE a."Email" = u."Email";
                """);

            // 4) Artık her user bir account'a bağlı → AccountId NOT NULL.
            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId",
                schema: "public",
                table: "users",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // 5) users.Email GLOBAL UNIQUE → non-unique (benzersizlik artık accounts.Email'de).
            migrationBuilder.DropIndex(
                name: "IX_users_Email",
                schema: "public",
                table: "users");
            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                schema: "public",
                table: "users",
                column: "Email");

            // 6) Aynı hesap aynı tenant'a iki kez üye olamaz.
            migrationBuilder.CreateIndex(
                name: "IX_users_AccountId_TenantId",
                schema: "public",
                table: "users",
                columns: new[] { "AccountId", "TenantId" },
                unique: true);

            // 7) FK users.AccountId → accounts.Id (hesap silinince üyelikler de silinir).
            migrationBuilder.AddForeignKey(
                name: "FK_users_accounts_AccountId",
                schema: "public",
                table: "users",
                column: "AccountId",
                principalSchema: "public",
                principalTable: "accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_accounts_AccountId",
                schema: "public",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_AccountId_TenantId",
                schema: "public",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_Email",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AccountId",
                schema: "public",
                table: "users");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "public");

            // Geri alım: e-posta yeniden global-unique (çok-şirket kayıtları varsa bu başarısız olur — bilinçli).
            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                schema: "public",
                table: "users",
                column: "Email",
                unique: true);
        }
    }
}
