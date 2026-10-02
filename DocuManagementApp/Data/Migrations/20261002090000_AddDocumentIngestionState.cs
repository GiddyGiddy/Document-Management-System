using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication1.Data.Migrations
{
    public partial class AddDocumentIngestionState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProcessingStatus",
                table: "documents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Completed");

            migrationBuilder.AddColumn<string>(
                name: "RequestedOutputFormat",
                table: "documents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Original");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessingStartedAtUtc",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessingCompletedAtUtc",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureSummary",
                table: "documents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE documents SET \"ProcessingCompletedAtUtc\" = \"UploadedAtUtc\" WHERE \"ProcessingStatus\" = 'Completed';");

            migrationBuilder.CreateIndex(
                name: "IX_documents_ProcessingStatus_UploadedAtUtc",
                table: "documents",
                columns: new[] { "ProcessingStatus", "UploadedAtUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_documents_ProcessingStatus_UploadedAtUtc",
                table: "documents");

            migrationBuilder.DropColumn(name: "ProcessingStatus", table: "documents");
            migrationBuilder.DropColumn(name: "RequestedOutputFormat", table: "documents");
            migrationBuilder.DropColumn(name: "ProcessingStartedAtUtc", table: "documents");
            migrationBuilder.DropColumn(name: "ProcessingCompletedAtUtc", table: "documents");
            migrationBuilder.DropColumn(name: "FailureSummary", table: "documents");
        }
    }
}
