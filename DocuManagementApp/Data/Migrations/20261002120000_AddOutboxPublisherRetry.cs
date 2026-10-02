using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication1.Data.Migrations
{
    public partial class AddOutboxPublisherRetry : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAtUtc",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntilUtc",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockToken",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "outbox_messages",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_Unpublished_CreatedAtUtc",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Unpublished_NextAttempt_CreatedAt",
                table: "outbox_messages",
                columns: new[] { "NextAttemptAtUtc", "CreatedAtUtc" },
                filter: "\"PublishedAtUtc\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_Unpublished_NextAttempt_CreatedAt",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Unpublished_CreatedAtUtc",
                table: "outbox_messages",
                column: "CreatedAtUtc",
                filter: "\"PublishedAtUtc\" IS NULL");

            migrationBuilder.DropColumn(name: "LastAttemptAtUtc", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "NextAttemptAtUtc", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "LockedUntilUtc", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "LockToken", table: "outbox_messages");
            migrationBuilder.DropColumn(name: "LastError", table: "outbox_messages");
        }
    }
}