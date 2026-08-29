using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// 初期MigrationをDiscaScoutDbContextへ関連付けるメタデータ
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
[Migration("20260829190000_InitialCreate")]
partial class InitialCreate
{
}
