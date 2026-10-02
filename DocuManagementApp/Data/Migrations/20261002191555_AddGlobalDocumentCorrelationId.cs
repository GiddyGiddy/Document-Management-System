using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication1.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalDocumentCorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE documents SET \"CorrelationId\" = \"Id\" WHERE \"CorrelationId\" IS NULL;");
            migrationBuilder.Sql("UPDATE outbox_messages AS outbox SET \"CorrelationId\" = document.\"CorrelationId\" FROM documents AS document WHERE outbox.\"DocumentId\" = document.\"Id\" AND outbox.\"CorrelationId\" IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "CorrelationId",
                table: "documents",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CorrelationId",
                table: "outbox_messages",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_documents_CorrelationId",
                table: "documents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_CorrelationId",
                table: "outbox_messages",
                column: "CorrelationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_CorrelationId",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_CorrelationId",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "documents");
        }
    }
}
