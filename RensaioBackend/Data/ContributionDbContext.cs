using RensaioBackend.Models.ContributionDatabase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Text.Json;
using RensaioBackend.Data.Converters;

namespace RensaioBackend.Data
{
    /// <summary>
    /// Design-time factory used by EF tooling (migration generation). Points at a local
    /// dev <c>contributor.db</c> so <c>dotnet ef migrate</c> targets the right database.
    /// </summary>
    public class ContributionDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ContributionDbContext>
    {
        public ContributionDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<ContributionDbContext>()
                .UseSqlite("Data Source=C:\\users\\mpiva\\appdata\\local\\rensaio\\contributor.db")
                .Options;
            return new ContributionDbContext(options);
        }
    }

    /// <summary>
    /// Entity Framework context for the local contributor database (contributor.db).
    /// Mirrors the Cloudflare contributor worker schema in normalized form with
    /// <c>Version</c> columns and a <c>SyncState</c> replication cursor.
    ///
    /// All 16-byte identity columns are true binary <c>BLOB(16)</c> columns via
    /// <see cref="GuidToBlob16Converter"/> — NOT the SQLite TEXT representation.
    /// </summary>
    public class ContributionDbContext : DbContext
    {
        public ContributionDbContext(DbContextOptions<ContributionDbContext> options) : base(options)
        {
        }

        public DbSet<TitleEntity> Titles { get; set; }
        public DbSet<MappingEntity> Mappings { get; set; }
        public DbSet<MappingTitleEntity> MappingTitles { get; set; }
        public DbSet<ContributionSeriesEntity> Series { get; set; }
        public DbSet<ContributionSourceEntity> Sources { get; set; }
        public DbSet<ContributionMetadataEntity> Metadata { get; set; }
        public DbSet<SyncStateEntity> SyncStates { get; set; }

        /// <summary>
        /// Value converter instance reused for every BLOB(16) identity column.
        /// </summary>
     
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TitleEntity>(entity =>
            {
                entity.HasKey(t => t.Id);
                entity.Property(t => t.Id).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(t => t.Title).HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(t => t.Version).HasColumnType("INTEGER").IsRequired();
            });

            modelBuilder.Entity<MappingEntity>(entity =>
            {
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Id).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
            });

            modelBuilder.Entity<MappingTitleEntity>(entity =>
            {
                entity.HasKey(mt => new { mt.MappingId, mt.TitleId });
                entity.Property(mt => mt.MappingId).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(mt => mt.TitleId).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(mt => mt.Version).HasColumnType("INTEGER").IsRequired();
                entity.HasOne(mt => mt.Mapping).WithMany().HasForeignKey(mt => mt.MappingId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(mt => mt.Title).WithMany().HasForeignKey(mt => mt.TitleId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ContributionSourceEntity>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(s => s.Package).HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(s => s.SourceId).HasColumnType("INTEGER").IsRequired();
                entity.Property(s => s.SourceName).HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(s => s.SourceLanguage).HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(s => s.LastBatchExecutionUTC).HasColumnType("TEXT");
                entity.Property(s => s.Version).HasColumnType("INTEGER").IsRequired();
                entity.HasIndex(s => new { s.Package, s.SourceId }).IsUnique().HasDatabaseName("IX_Sources_Package_SourceId");
            });

            modelBuilder.Entity<ContributionSeriesEntity>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(s => s.MappingId).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(s => s.SourceId).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(s => s.Data).HasJsonConversion<ContributionRecordV1>().HasColumnType("TEXT").UseCollation("BINARY");
                entity.Property(s => s.Version).HasColumnType("INTEGER").IsRequired();
                entity.HasOne(s => s.Mapping).WithMany().HasForeignKey(s => s.MappingId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(s => s.Source).WithMany().HasForeignKey(s => s.SourceId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(s => s.MappingId).HasDatabaseName("IX_Sources_MappingId");
            });

            modelBuilder.Entity<ContributionMetadataEntity>(entity =>
            {
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Id).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(m => m.MappingId).HasGuidToBlob16().HasColumnType("BLOB").IsRequired();
                entity.Property(m => m.ProviderId).HasColumnType("INTEGER").IsRequired();
                entity.Property(m => m.ProviderKey).HasColumnType("TEXT").IsRequired();
                entity.Property(m => m.MappingStatus).HasColumnType("INTEGER").IsRequired().HasConversion<int>();
                entity.Property(m => m.Version).HasColumnType("INTEGER").IsRequired();
                entity.HasOne(m => m.Mapping).WithMany().HasForeignKey(m => m.MappingId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(m => m.MappingId).HasDatabaseName("IX_Metadata_MappingId");
                entity.HasIndex(m => new { m.ProviderId, m.ProviderKey }).HasDatabaseName("IX_Metadata_ProviderId_ProviderKey");
                entity.HasIndex(m => m.MappingStatus).HasDatabaseName("IX_Metadata_MappingType");
            });

            modelBuilder.Entity<SyncStateEntity>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).HasColumnType("INTEGER").IsRequired();
                entity.Property(s => s.LastServerVersion).HasColumnType("INTEGER").IsRequired();
            });
        }
    }
}