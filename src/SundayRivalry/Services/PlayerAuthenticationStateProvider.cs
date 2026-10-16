using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace SundayRivalry.Services;

public sealed class PlayerAuthenticationStateProvider(ILoggerFactory logger, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(logger)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PlayerAccount>>();
        var user = await users.GetUserAsync(state.User);
        if (user is null) return false;
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        return state.User.FindFirst(options.ClaimsIdentity.SecurityStampClaimType)?.Value == await users.GetSecurityStampAsync(user);
    }
}
