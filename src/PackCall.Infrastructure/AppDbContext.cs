using Microsoft.EntityFrameworkCore;
using PackCall.Core.Entities;
using PackCall.Infrastructure.Journaling;
using PackCall.Infrastructure.Sending;

namespace PackCall.Infrastructure
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<Campaign> Campaigns { get; set; } = null!;
        public DbSet<Recipient> Recipients { get; set; } = null!;
        public DbSet<Delivery> Deliveries { get; set; } = null!;
        internal DbSet<DeliveryStatusJournalRecord> DeliveryStatusJournal => Set<DeliveryStatusJournalRecord>();
        internal DbSet<MockEmailSend> MockEmailSends => Set<MockEmailSend>();

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
                entity.Property(e => e.Status).HasColumnName("status").IsRequired();
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
                entity.Property(e => e.DeliveryStatus).HasColumnName("delivery_status").IsRequired();
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.Property(e => e.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
                entity.Property(e => e.WorkerId).HasColumnName("worker_id").HasMaxLength(255);
                entity.Property(e => e.LockExpiresAt).HasColumnName("lock_expires_at").HasColumnType("timestamp with time zone");
                entity.HasOne(e => e.Campaign).WithMany(c => c.Deliveries).HasForeignKey(e => e.CampaignId).HasConstraintName("fk_deliveries_campaigns").OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(e => e.Recipient).WithMany(r => r.Deliveries).HasForeignKey(e => e.RecipientId).HasConstraintName("fk_deliveries_recipients").OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(e => new { e.CampaignId, e.RecipientId }).HasDatabaseName("ix_deliveries_campaign_recipient").IsUnique();
                entity.HasIndex(e => e.DeliveryStatus).HasDatabaseName("ix_deliveries_status");
                entity.HasIndex(e => new { e.DeliveryStatus, e.LockExpiresAt }).HasDatabaseName("ix_deliveries_status_lock_expires_at");
                entity.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_deliveries_created_at");
            });

            modelBuilder.Entity<MockEmailSend>(entity =>
            {
                entity.ToTable("mock_email_sends");
                entity.HasKey(e => e.DeliveryId).HasName("pk_mock_email_sends_delivery_id");
                entity.Property(e => e.DeliveryId).HasColumnName("delivery_id");
                entity.Property(e => e.CampaignId).HasColumnName("campaign_id").IsRequired();
                entity.Property(e => e.RecipientId).HasColumnName("recipient_id").IsRequired();
                entity.Property(e => e.RecipientEmail).HasColumnName("recipient_email").HasMaxLength(255).IsRequired();
                entity.Property(e => e.MessageText).HasColumnName("message_text").HasColumnType("text").IsRequired();
                entity.Property(e => e.IsSuccess).HasColumnName("is_success").IsRequired();
                entity.Property(e => e.Error).HasColumnName("error").HasMaxLength(1000);
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_mock_email_sends_created_at");
            });

            modelBuilder.Entity<DeliveryStatusJournalRecord>(entity =>
            {
                entity.ToTable("delivery_status_journal");
                entity.HasKey(e => e.Id).HasName("pk_delivery_status_journal_id");
                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
                entity.Property(e => e.CampaignId).HasColumnName("campaign_id").IsRequired();
                entity.Property(e => e.DeliveryId).HasColumnName("delivery_id").IsRequired();
                entity.Property(e => e.PreviousStatus).HasColumnName("previous_status").IsRequired();
                entity.Property(e => e.NewStatus).HasColumnName("new_status").IsRequired();
                entity.Property(e => e.WorkerId).HasColumnName("worker_id").HasMaxLength(255);
                entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.Property(e => e.OperatorResult).HasColumnName("operator_result").HasMaxLength(1000);
                entity.HasIndex(e => new { e.CampaignId, e.OccurredAt })
                    .HasDatabaseName("ix_delivery_status_journal_campaign_occurred_at");
                entity.HasIndex(e => new { e.DeliveryId, e.OccurredAt })
                    .HasDatabaseName("ix_delivery_status_journal_delivery_occurred_at");
            });
        }
    }
}
