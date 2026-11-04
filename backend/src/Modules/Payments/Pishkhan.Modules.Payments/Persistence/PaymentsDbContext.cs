using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Payments.Domain;

namespace Pishkhan.Modules.Payments.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "payments";

    public override string Schema => SchemaName;

    public DbSet<MerchantAccount> MerchantAccounts => Set<MerchantAccount>();

    public DbSet<PaymentLink> Links => Set<PaymentLink>();

    public DbSet<PaymentAttempt> Attempts => Set<PaymentAttempt>();

    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();

    public DbSet<ReceiptSettings> ReceiptSettings => Set<ReceiptSettings>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MerchantAccount>(b =>
        {
            b.ToTable("merchant_accounts");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Provider).HasMaxLength(20);
            b.Property(x => x.MerchantIdEnc).HasMaxLength(1000);
            b.Property(x => x.MerchantIdHint).HasMaxLength(10);
            b.HasIndex(x => x.WorkspaceId).IsUnique();
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<PaymentLink>(b =>
        {
            b.ToTable("links");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ShopName).HasMaxLength(200);
            b.Property(x => x.Provider).HasMaxLength(20);
            b.Property(x => x.Token).HasMaxLength(64);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.ReferenceId).HasMaxLength(64);
            b.Property(x => x.CardMask).HasMaxLength(32);
            b.Property(x => x.LastError).HasMaxLength(64);
            b.Property(x => x.Lines).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, EnvelopeJson.Options),
                v => JsonSerializer.Deserialize<List<PaymentLinkLine>>(v, EnvelopeJson.Options)!,
                new ValueComparer<List<PaymentLinkLine>>((a, c) => a!.SequenceEqual(c!), v => v.Count, v => v.ToList()));
            b.HasIndex(x => x.Token).IsUnique();
            b.HasIndex(x => new { x.WorkspaceId, x.OrderId, x.CreatedAt });
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<PaymentAttempt>(b =>
        {
            b.ToTable("attempts");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Authority).HasMaxLength(64);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Code).HasMaxLength(64);
            b.HasIndex(x => x.Authority).IsUnique();
            b.HasIndex(x => new { x.WorkspaceId, x.LinkId });
        });

        modelBuilder.Entity<BankAccount>(b =>
        {
            b.ToTable("bank_accounts");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.CardNumber).HasMaxLength(16);
            b.Property(x => x.Sheba).HasMaxLength(26);
            b.Property(x => x.HolderName).HasMaxLength(100);
            b.Property(x => x.BankCode).HasMaxLength(30);
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<ReceiptSettings>(b =>
        {
            b.ToTable("receipt_settings");
            b.HasKey(x => x.WorkspaceId);
        });

        modelBuilder.Entity<Receipt>(b =>
        {
            b.ToTable("receipts");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Source).HasMaxLength(20);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Reference).HasMaxLength(30);
            b.Property(x => x.SourceCard).HasMaxLength(40);
            b.Property(x => x.DestinationCard).HasMaxLength(40);
            b.Property(x => x.DestinationSheba).HasMaxLength(40);
            b.Property(x => x.Bank).HasMaxLength(60);
            b.Property(x => x.RejectReason).HasMaxLength(Receipt.MaxReason);
            b.Property(x => x.Checks).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, EnvelopeJson.Options),
                v => JsonSerializer.Deserialize<List<ReceiptCheck>>(v, EnvelopeJson.Options)!,
                new ValueComparer<List<ReceiptCheck>>((a, c) => a!.SequenceEqual(c!), v => v.Count, v => v.ToList()));
            b.HasIndex(x => new { x.WorkspaceId, x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.WorkspaceId, x.Reference }).HasFilter("reference IS NOT NULL");
            b.HasIndex(x => x.FileId);
            b.HasIndex(x => x.MessageId).IsUnique().HasFilter("message_id IS NOT NULL");
            b.HasIndex(x => new { x.WorkspaceId, x.OrderId });
            b.Ignore(x => x.DomainEvents);
        });
    }
}

internal sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<PaymentsDbContext>(PaymentsDbContext.SchemaName), new TenantContext());
}
