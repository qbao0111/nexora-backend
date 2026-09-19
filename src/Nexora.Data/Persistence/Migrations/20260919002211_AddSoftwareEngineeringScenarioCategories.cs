using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // Migration-generated column arrays are immutable
#pragma warning disable IDE0161 // Keep EF-generated migration namespace style

namespace Nexora.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareEngineeringScenarioCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "scenario_categories",
                columns: new[] { "Id", "CreatedAt", "Description", "IsActive", "Name", "Slug", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-000000000004"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống phát triển API, dịch vụ backend, tích hợp hệ thống và xử lý lỗi phía máy chủ.", true, "Backend", "backend", 3, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-000000000005"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống phát triển giao diện web, state management, UX, hiệu năng và tích hợp API.", true, "Frontend", "frontend", 4, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-000000000006"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống thực tế với C#, ASP.NET Core, Entity Framework Core và hệ sinh thái .NET.", true, ".NET / C#", "dotnet", 5, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-000000000007"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống phát triển hệ thống backend bằng Java, Spring Boot, JPA và hệ sinh thái JVM.", true, "Java / Spring Boot", "java", 6, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-000000000008"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống thiết kế dữ liệu, SQL, transaction, hiệu năng truy vấn và tính toàn vẹn dữ liệu.", true, "Database & Data", "database", 7, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-000000000009"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống CI/CD, Docker, cloud, deployment, monitoring và vận hành hệ thống.", true, "DevOps / Cloud", "devops", 8, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-00000000000a"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống kiểm thử phần mềm, automation test, regression, chất lượng release và xử lý bug.", true, "QA / Testing", "qa-testing", 9, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-00000000000b"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống phát triển ứng dụng mobile, API integration, lifecycle, offline state và release app.", true, "Mobile Development", "mobile", 10, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-00000000000c"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống thiết kế hệ thống, scalability, service boundaries và lựa chọn kiến trúc.", true, "Software Architecture", "software-architecture", 11, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("40000000-0000-0000-0000-00000000000d"), new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tình huống bảo mật ứng dụng dành cho lập trình viên: authentication, authorization, data exposure và secure coding.", true, "Application Security", "app-security", 12, new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000a"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000b"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000c"));

            migrationBuilder.DeleteData(
                table: "scenario_categories",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000d"));
        }
    }
}
