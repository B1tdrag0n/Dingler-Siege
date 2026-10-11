using Dingler.Data.Entities.GameData;
using Microsoft.EntityFrameworkCore;

namespace Dingler.Data.Context;

public partial class GameDataContext : DbContext
{
    public GameDataContext()
    {
    }

    public GameDataContext(DbContextOptions<GameDataContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Account> Accounts { get; set; }

    public virtual DbSet<Deck> Decks { get; set; }

    public virtual DbSet<Friend> Friends { get; set; }

    public virtual DbSet<PlayerProfile> PlayerProfiles { get; set; }
    public virtual DbSet<Tournament> Tournaments { get; set; }

    public virtual DbSet<Siege> Sieges { get; set; }

    public virtual DbSet<SiegeLedger> SiegeLedger { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite("Data Source=data/gameData.db");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(e => e.Email, "IX_Accounts_Email").IsUnique();
            
            entity.HasOne(a => a.PlayerProfile)
                .WithOne(p => p.Account)
                .HasForeignKey<PlayerProfile>(a => a.AccountId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Deck>(entity =>
        {
            entity.Property(d => d.DeckGuid)
                .HasConversion<string>()
                .HasColumnType("TEXT");

            entity.Property(d => d.ChampionGuid)
                .HasConversion<string>()
                .HasColumnType("TEXT");

            entity.HasIndex(e => e.DeckGuid, "IX_Decks_DeckGuid").IsUnique();

            entity.HasIndex(e => new { e.DeckName, e.PlayerProfileId }, "IX_Decks_DeckName_PlayerProfileId").IsUnique();

            entity.Property(e => e.ChampionGuid).HasDefaultValue(Guid.Empty);

            entity.HasOne(d => d.PlayerProfile).WithMany(p => p.Decks)
                .HasForeignKey(d => d.PlayerProfileId)
                .OnDelete(DeleteBehavior.ClientSetNull);
        });

        modelBuilder.Entity<Friend>(entity =>
        {
            entity.HasIndex(e => new { e.RequesterId, e.RequestedId }, "IX_Friends_RequesterId_RequestedId").IsUnique();
            
            entity.HasOne(d => d.Requested)
                .WithMany(p => p.FriendRequesteds)
                .HasForeignKey(d => d.RequestedId)
                .OnDelete(DeleteBehavior.ClientSetNull);

            entity.HasOne(d => d.Requester)
                .WithMany(p => p.FriendRequesters)
                .HasForeignKey(d => d.RequesterId)
                .OnDelete(DeleteBehavior.ClientSetNull);
            
            entity.HasOne(e => e.Status)
                .WithMany(e => e.Friends)
                .HasForeignKey(e => e.FriendStatusId)
                .OnDelete(DeleteBehavior.ClientSetNull);
        });

        modelBuilder.Entity<FriendStatus>(entity =>
        {
            entity.ToTable("FriendStatus");
            entity.HasKey(e => e.Id);
            
            entity.HasIndex(e => e.Description, "IX_FriendStatus_Description").IsUnique();
            
            var friendStatusLookup = Enum.GetValues(typeof(Enums.FriendStatus)).Cast<Enums.FriendStatus>().OrderBy(v => v).Select(value => new FriendStatus(){Id = (int)value, Description = value.ToString()}).ToList();
            
            entity.HasData(friendStatusLookup);
        });
        
        modelBuilder.Entity<PlayerProfile>(entity =>
        {
            entity.HasIndex(e => e.Username, "IX_PlayerProfiles_Username").IsUnique();
    
            entity.Property(e => e.Elo)
                .HasDefaultValue(1000)
                .HasColumnName("ELO");
    
            entity.HasOne(e => e.Rank)
                .WithMany(e => e.PlayerProfiles)
                .HasForeignKey(e => e.RankId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Rank>(entity =>
        {
            entity.ToTable("Rank");
            entity.HasKey(e => e.Id);
            
            entity.HasIndex(e => e.Name, "IX_Rank_Name").IsUnique();

            var rankLookups = Enum.GetValues(typeof(Enums.Rank)).Cast<Enums.Rank>().OrderBy(v => v).Select(value =>
                new Rank() { Id = (int)value, Name = value.ToString() }).ToList();
            
            entity.HasData(rankLookups);
        });

        modelBuilder.Entity<CardSet>(entity =>
        {
            entity.ToTable("Set");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Name, "IX_Set_Name").IsUnique();

            var cardSetLookups = Enum.GetValues(typeof(Enums.CardSet)).Cast<Enums.CardSet>().OrderBy(v => v).Select(value => new CardSet() { Id = (int)value, Name = value.ToString()}).ToList();
            
            entity.HasData(cardSetLookups);
        });

        modelBuilder.Entity<DraftSet>(entity =>
        {
            entity.ToTable("DraftSets");
            entity.HasKey(e => e.Id);

            entity.HasOne(ds => ds.Tournament)
                .WithMany(p => p.DraftSets)
                .HasForeignKey(ds => ds.TournamentId);
            
            entity.HasOne(ds => ds.CardSet)
                .WithMany(p => p.DraftSets)
                .HasForeignKey(ds => ds.CardSetId);
        });

        modelBuilder.Entity<StartCondition>(entity =>
        {
            entity.ToTable("StartCondition");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Description, "IX_StartCondition_Description").IsUnique();
            
            var startConditionsLookups = Enum.GetValues(typeof(Enums.StartCondition)).Cast<Enums.StartCondition>().OrderBy(v => v).Select(value =>
                new StartCondition() { Id = (int)value, Description = value.ToString() }).ToList();
            
            entity.HasData(startConditionsLookups);
        });

        modelBuilder.Entity<Tournament>(entity =>
        {
            entity.ToTable("Tournaments");
            entity.HasKey(e => e.Id);
            
            entity.HasIndex(e => e.Description, "IX_Tournament_Description").IsUnique();
            
            entity.HasOne(e => e.StartCondition)
                .WithMany(e => e.Tournaments)
                .HasForeignKey(e => e.StartConditionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.TournamentType)
                .WithMany(e => e.Tournaments)
                .HasForeignKey(e => e.TournamentTypeId)
                .OnDelete(DeleteBehavior.Restrict);
            
            entity.HasOne(e => e.MatchType)
                .WithMany(e => e.Tournaments)
                .HasForeignKey(e => e.MatchTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            var standardOnDemand = new Tournament()
            {
                Id = 1,
                Description = "1v1 Standard Match",
                MatchTypeId = (int)Enums.MatchTypes.SingleElimination,
                StartConditionId = (int)Enums.StartCondition.WhenFull,
                TournamentTypeId = (int)Enums.TournamentTypes.Standard,
                NeededPlayers = 2,
                StartDate = DateTime.MinValue,
            };
            var immortalOnDemand = new Tournament()
            {
                Id = 2,
                MatchTypeId = (int)Enums.MatchTypes.SingleElimination,
                Description = "1v1 Immortal Match",
                StartConditionId = (int)Enums.StartCondition.WhenFull,
                TournamentTypeId = (int)Enums.TournamentTypes.Immortal,
                NeededPlayers = 2,
                StartDate = DateTime.MinValue,
            };
            
            entity.HasData(standardOnDemand);
            entity.HasData(immortalOnDemand);
        });

        modelBuilder.Entity<TournamentType>(entity =>
        {
            entity.ToTable("TournamentType");
            entity.HasKey(e => e.Id);
            
            entity.HasIndex(e => e.Name, "IX_TournamentType_Name").IsUnique();
            
            var tournamentTypeLookups = Enum.GetValues(typeof(Enums.TournamentTypes)).Cast<Enums.TournamentTypes>().OrderBy(v => v).Select(value =>
                new TournamentType() { Id = (int)value, Name = value.ToString() }).ToList();
            
            entity.HasData(tournamentTypeLookups);
        });

        modelBuilder.Entity<Entities.GameData.MatchType>(entity =>
        {
            entity.ToTable("MatchType");
            entity.HasKey(e => e.Id);
            
            entity.HasIndex(e => e.Description, "IX_MatchType_Description").IsUnique();

            var matchTypeLookups = Enum.GetValues(typeof(Enums.MatchTypes)).Cast<Enums.MatchTypes>().OrderBy(v => v)
                .Select(value =>
                    new Entities.GameData.MatchType() { Id = (int)value, Description = value.ToString() }).ToList();
            
            entity.HasData(matchTypeLookups);
        });
        
        modelBuilder.Entity<Siege>(entity =>
        {
            entity.ToTable("Sieges");

            entity.HasIndex(e => e.Status, "IX_Sieges_Status");

            entity.HasOne(e => e.PlayerProfile)
                .WithMany()
                .HasForeignKey(e => e.PlayerProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SiegeLedger>(entity =>
        {
            entity.ToTable("SiegeLedger");

            entity.HasOne(e => e.Siege)
                .WithMany(s => s.Ledger)
                .HasForeignKey(e => e.SiegeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PlayerProfile)
                .WithMany()
                .HasForeignKey(e => e.PlayerProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
