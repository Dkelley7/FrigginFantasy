using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SundayRivalry.Services;

public class PlayerAccount : IdentityUser
{
    public int PlayerSlot { get; set; }
    public string DisplayName { get; set; } = "";
}

public class PlayerInvitation
{
    public int Id { get; set; }
    public string TokenHash { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public class AccountsDb(DbContextOptions<AccountsDb> options) : IdentityDbContext<PlayerAccount>(options)
{
    public DbSet<PlayerInvitation> Invitations => Set<PlayerInvitation>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PlayerAccount>().HasIndex(u => u.PlayerSlot).IsUnique();
        builder.Entity<PlayerAccount>().HasIndex(u => u.NormalizedEmail).IsUnique();
        builder.Entity<PlayerInvitation>().HasIndex(i => i.TokenHash).IsUnique();
    }
}

public class PlayerClaimsFactory(UserManager<PlayerAccount> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<PlayerAccount>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(PlayerAccount user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new("player_slot", user.PlayerSlot.ToString()));
        return identity;
    }
}

// Single-process registration gate plus database constraints prevent concurrent
// registrations from claiming the same roster. Accounts have their own database;
// introducing Identity never recreates or replaces existing game data.
public class AccountRegistration
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static string NormalizeEmail(string email)
    {
        email = email.Trim();
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email || !email.Contains('.'))
            throw new InvalidOperationException("Enter a valid email address, such as your Gmail address.");
        return email.ToLowerInvariant();
    }
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<(PlayerAccount? User, string? Error)> Register(AccountsDb db, UserManager<PlayerAccount> users,
        GameService games, string email, string name, string password, string confirmation, string invite)
    {
        if (password.Length is < 12 or > 128) return (null, "Use a password between 12 and 128 characters.");
        if (password != confirmation) return (null, "The passwords don't match.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 30) return (null, "Choose a display name between 1 and 30 characters.");
        try { email = NormalizeEmail(email); } catch (InvalidOperationException e) { return (null, e.Message); }
        await gate.WaitAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var count = await db.Users.CountAsync();
            if (count >= 2) return (null, "Both player accounts are set up. Sign in to continue.");
            PlayerInvitation? invitation = null;
            if (count != 0)
            {
                var hash = HashToken(invite);
                invitation = await db.Invitations.SingleOrDefaultAsync(i => i.TokenHash == hash);
                if (invitation is null || invitation.UsedAt is not null || invitation.ExpiresAt <= DateTimeOffset.UtcNow || invitation.Email != email)
                    return (null, "Use a current invitation link and the email address it was created for.");
            }
            var user = new PlayerAccount { Email = email, UserName = email, PlayerSlot = count, DisplayName = name.Trim() };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded) return (null, "That account couldn't be created. Check the email address and password, or sign in if you already registered.");
            if (invitation is not null) invitation.UsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            await games.Mutate(s => s.Names[user.PlayerSlot] = user.DisplayName);
            return (user, null);
        }
        finally { gate.Release(); }
    }

    public async Task<string> Invite(AccountsDb db, string email)
    {
        email = NormalizeEmail(email);
        await gate.WaitAsync();
        try
        {
            if (await db.Users.CountAsync() >= 2) throw new InvalidOperationException("Both player accounts are already set up.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            foreach (var old in await db.Invitations.Where(i => i.UsedAt == null).ToListAsync()) old.UsedAt = DateTimeOffset.UtcNow;
            db.Invitations.Add(new() { Email = email, TokenHash = HashToken(token), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) });
            await db.SaveChangesAsync(); return token;
        }
        finally { gate.Release(); }
    }
}
