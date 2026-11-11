using Microsoft.EntityFrameworkCore;
using PackCall.Core.Entities;
using PackCall.Infrastructure.Journaling;

namespace PackCall.Infrastructure
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<Campaign> Campaigns { get; set; } = null!;
        public DbSet<Contact> Contacts { get; set; } = null!;
        public DbSet<ContactChannel> ContactChannels => Set<ContactChannel>();
        public DbSet<CampaignContact> CampaignContacts => Set<CampaignContact>();
        public DbSet<CampaignChannel> CampaignChannels => Set<CampaignChannel>();
        public DbSet<Delivery> Deliveries { get; set; } = null!;
        internal DbSet<DeliveryStatusJournalRecord> DeliveryStatusJournal => Set<DeliveryStatusJournalRecord>();

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

            modelBuilder.Entity<Contact>(e => {
                e.ToTable("contacts"); e.HasKey(x => x.Id).HasName("pk_contacts_id");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(255);
                e.Property(x => x.CreatedAt).HasColumnName("created_at");
                e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            });
            modelBuilder.Entity<ContactChannel>(e => {
                e.ToTable("contact_channels"); e.HasKey(x => x.Id);
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.ContactId).HasColumnName("contact_id");
                e.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(32);
                e.Property(x => x.Address).HasColumnName("address").HasMaxLength(255);
                e.HasOne(x => x.Contact).WithMany(x => x.Channels).HasForeignKey(x => x.ContactId);
                e.HasIndex(x => new { x.Channel, x.Address }).IsUnique().HasDatabaseName("ix_contact_channels_channel_address");
                e.HasIndex(x => new { x.ContactId, x.Channel }).IsUnique();
            });
            modelBuilder.Entity<CampaignContact>(e => {
                e.ToTable("campaign_contacts"); e.HasKey(x => new { x.CampaignId, x.ContactId });
                e.Property(x => x.CampaignId).HasColumnName("campaign_id"); e.Property(x => x.ContactId).HasColumnName("contact_id");
                e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId);
                e.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId);
            });
            modelBuilder.Entity<CampaignChannel>(e => {
                e.ToTable("campaign_channels"); e.HasKey(x => new { x.CampaignId, x.Channel });
                e.Property(x => x.CampaignId).HasColumnName("campaign_id"); e.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(32);
                e.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId);
            });

            // Delivery configuration
            modelBuilder.Entity<Delivery>(entity =>
            {
                entity.ToTable("deliveries");
                entity.HasKey(e => e.Id).HasName("pk_deliveries_id");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.CampaignId).HasColumnName("campaign_id").IsRequired();
                entity.Property(e => e.ContactId).HasColumnName("contact_id").IsRequired();
                entity.Property(e => e.AddressSnapshot).HasColumnName("address_snapshot").HasMaxLength(255);
                entity.Property(e => e.Channel).HasColumnName("channel").HasMaxLength(32).IsRequired();
                entity.HasOne<CampaignContact>().WithMany().HasForeignKey(e => new { e.CampaignId, e.ContactId });
                entity.HasOne<CampaignChannel>().WithMany().HasForeignKey(e => new { e.CampaignId, e.Channel });
                entity.Property(e => e.DeliveryStatus).HasColumnName("delivery_status").IsRequired();
                entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();
                entity.Property(e => e.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
                entity.Property(e => e.WorkerId).HasColumnName("worker_id").HasMaxLength(255);
                entity.Property(e => e.LockExpiresAt).HasColumnName("lock_expires_at").HasColumnType("timestamp with time zone");
                entity.HasOne(e => e.Campaign).WithMany(c => c.Deliveries).HasForeignKey(e => e.CampaignId).HasConstraintName("fk_deliveries_campaigns").OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(e => e.Contact).WithMany(r => r.Deliveries).HasForeignKey(e => e.ContactId).HasConstraintName("fk_deliveries_contacts").OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(e => new { e.CampaignId, e.ContactId, e.Channel }).HasDatabaseName("ix_deliveries_campaign_contact_channel").IsUnique();
                entity.HasIndex(e => e.DeliveryStatus).HasDatabaseName("ix_deliveries_status");
                entity.HasIndex(e => new { e.DeliveryStatus, e.LockExpiresAt }).HasDatabaseName("ix_deliveries_status_lock_expires_at");
                entity.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_deliveries_created_at");
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
