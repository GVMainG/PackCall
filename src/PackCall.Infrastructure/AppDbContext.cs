using Microsoft.EntityFrameworkCore;
using PackCall.Infrastructure.Models;

namespace PackCall.Infrastructure
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<Campaign> Campaigns { get; set; }
        public DbSet<Recipient> Recipients { get; set; }
        public DbSet<Delivery> Deliveries { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Campaign configuration
            modelBuilder.Entity<Campaign>(entity =>
            {
                entity.ToTable("campaigns");
                entity.HasKey(e => e.Id).HasName("pk_campaigns_id");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(255);
                entity.Property(e => e.MessageText).HasColumnName("message_text").HasColumnType("text");
                entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(50).IsRequired();
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
                entity.Property(e => e.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone");
                entity.Property(e => e.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
                entity.HasMany(e => e.Deliveries).WithOne(d => d.Campaign).HasForeignKey(d => d.CampaignId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(e => e.Status).HasDatabaseName("ix_campaigns_status");

                // Для удобной пагинации и филтрации по времени создания.
                entity.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_campaigns_created_at");
            });

            // Recipient configuration
            modelBuilder.Entity<Recipient>(entity =>
            {
                entity.ToTable("recipients");
                entity.HasKey(e => e.Id).HasName("pk_recipients_id");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(255).IsRequired();
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
                entity.HasMany(e => e.Deliveries).WithOne(d => d.Recipient).HasForeignKey(d => d.RecipientId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(e => e.Email).HasDatabaseName("ix_recipients_email").IsUnique();
            });

            // Delivery configuration
            modelBuilder.Entity<Delivery>(entity =>
            {
                entity.ToTable("deliveries");
                entity.HasKey(e => e.Id).HasName("pk_deliveries_id");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CampaignId).HasColumnName("campaign_id").IsRequired();
                entity.Property(e => e.RecipientId).HasColumnName("recipient_id").IsRequired();
                entity.Property(e => e.RecipientEmail).HasColumnName("recipient_email").HasMaxLength(255).IsRequired();
                entity.Property(e => e.DeliveryStatus).HasColumnName("delivery_status").HasMaxLength(50).IsRequired();
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.Property(e => e.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
                entity.HasOne(e => e.Campaign).WithMany(c => c.Deliveries).HasForeignKey(e => e.CampaignId).HasConstraintName("fk_deliveries_campaigns").OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(e => e.Recipient).WithMany(r => r.Deliveries).HasForeignKey(e => e.RecipientId).HasConstraintName("fk_deliveries_recipients").OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(e => new { e.CampaignId, e.RecipientId }).HasDatabaseName("ix_deliveries_campaign_recipient").IsUnique();
                entity.HasIndex(e => e.DeliveryStatus).HasDatabaseName("ix_deliveries_status");
                entity.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_deliveries_created_at");
            });
        }
    }
}
