using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace SundayRivalry.Services;

public static class AccountPages
{
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Token(HttpContext context, IAntiforgery antiforgery)
    {
        var token = antiforgery.GetAndStoreTokens(context);
        return $"<input type=\"hidden\" name=\"{H(token.FormFieldName)}\" value=\"{H(token.RequestToken)}\">";
    }
    private static IResult Page(HttpContext context, string title, string content, string? error = null, int status = 200)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Content($$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="theme-color" content="#193d32"><title>{{H(title)}} · Sunday Rivalry</title><link rel="icon" href="/favicon.svg"><link rel="stylesheet" href="/account.css"><script src="/account.js" defer></script></head>
        <body><main class="account-shell"><aside class="account-story"><a class="account-brand" href="/"><img src="/football.svg" alt="" width="52" height="40"> <span>Sunday<br><strong>Rivalry<span class="orange">.</span></strong></span></a><div><span class="eyebrow">YOUR OWN LITTLE LEAGUE</span><h1>Same couch.<br>Different sidelines.</h1><p>A fresh lineup. A weekly showdown.<br>All the bragging rights.</p><div class="story-field" aria-hidden="true"><span>×</span><span>○</span><span>↗</span><span>×</span><span>○</span></div></div><small>Built for two. Played for fun.</small></aside><section class="account-panel"><div class="account-content"><span class="eyebrow">SUNDAY RIVALRY / YOUR ACCOUNT</span><h2>{{H(title)}}</h2>{{(error is null ? "" : $"<div class=\"account-error\" role=\"alert\">{H(error)}</div>")}}{{content}}</div><footer>Private football. Friendly competition.</footer></section></main></body></html>
        """, "text/html", statusCode: status);
    }
    private static string EmailField(string email = "") => $"<label>Email address<input type=\"email\" name=\"email\" value=\"{H(email)}\" required maxlength=\"254\" autocomplete=\"email\" placeholder=\"you@gmail.com\"></label>";
    private static string PasswordField(string label, string name, string autocomplete, bool isNew = false) => $"<label>{label}<input type=\"password\" name=\"{name}\" required {(isNew ? "minlength=\"12\"" : "")} maxlength=\"128\" autocomplete=\"{autocomplete}\"></label>";
    private static IResult ExpiredForm(HttpContext c, string path) => Page(c, "Let's refresh that page.",
        $"<p>Your sign-in session changed or this form expired. Open a fresh page and try again. Your information hasn't been submitted.</p><a class=\"button\" href=\"{H(path)}\">Continue safely</a>");
    private static IResult Login(HttpContext c, IAntiforgery a, bool first, string? error = null, string email = "", int status = 200)
    {
        var content = first
            ? "<p>Your home field is ready. Create the first player account, then invite your favorite rival.</p><a class=\"button\" href=\"/register\">Create your account →</a><p class=\"fine-print\">Use your Gmail or another email address, with a password just for this app.</p>"
            : $"<p>Your next showdown is waiting.</p><form method=\"post\" action=\"/login\">{Token(c, a)}{EmailField(email)}{PasswordField("Password", "password", "current-password")}<button class=\"button\">Sign in →</button></form><p class=\"fine-print\">New here? Open the invitation link from your league partner.</p><p class=\"fine-print\">Use your Sunday Rivalry password. This app doesn't sign in to your Google account. Email password resets aren't available yet.</p>";
        return Page(c, first ? "Your rivalry starts here." : "Welcome back.", content, error, status);
    }
    private static IResult Register(HttpContext c, IAntiforgery a, string invite, bool first, string? error = null, string email = "", string name = "", int status = 200)
    {
        var content = $"<p>{(first ? "Create your player account. You'll invite the second player next." : "A little friendly rivalry starts with your own account.")}</p><form method=\"post\" action=\"/register\">{Token(c, a)}<input type=\"hidden\" name=\"invite\" value=\"{H(invite)}\"><label>Your display name<input name=\"name\" required maxlength=\"30\" autocomplete=\"nickname\" value=\"{H(name)}\" placeholder=\"What should we call you?\"></label>{EmailField(email)}{PasswordField("Create a password", "password", "new-password", true)}<small class=\"field-help\">Use 12–128 characters. A memorable passphrase works well.</small>{PasswordField("Confirm password", "confirmPassword", "new-password", true)}<button class=\"button\">Create account →</button></form><p class=\"fine-print\">Gmail and other email addresses work. Choose a password for Sunday Rivalry; we don't need your Gmail password. Email ownership isn't verified yet.</p><p class=\"account-link\">Already registered? <a href=\"/login\">Sign in</a></p>";
        return Page(c, first ? "Claim your home field." : "Meet your new rival.", content, error, status);
    }
    private static async Task<IResult> Account(HttpContext c, IAntiforgery a, PlayerAccount user, AccountsDb db, string? error = null, string? invitation = null, string? invitedEmail = null, bool passwordChanged = false)
    {
        var count = await db.Users.CountAsync();
        var content = $"<p class=\"account-identity\"><strong>{H(user.DisplayName)}</strong><span>{H(user.Email)}</span></p><a class=\"back-link\" href=\"/\">← Back to your league</a>";
        if (c.Request.Query.ContainsKey("welcome")) content += "<div class=\"account-success\">Your account is ready. Invite your partner below to complete your league.</div>";
        if (passwordChanged) content += "<div class=\"account-success\" role=\"status\">Password changed. Use your new password next time you sign in.</div>";
        if (count < 2 && user.PlayerSlot == 0)
        {
            content += $"<section class=\"account-section\"><h3>Invite your favorite rival</h3><p>Enter their Gmail or other email address. We'll create a private link for you to share; no email is sent.</p><form method=\"post\" action=\"/account/invite\">{Token(c, a)}{EmailField()}<button class=\"button secondary\">Create invitation link</button></form><small class=\"field-help\">Links expire after seven days. Creating another replaces the previous invitation.</small></section>";
        }
        if (invitation is not null)
        {
            var path = "/register?invite=" + Uri.EscapeDataString(invitation);
            content += $"<section class=\"invite-result\"><strong>Your invitation is ready</strong><p>Share this link with {H(invitedEmail)}. They must register with that address on their own device or browser. For phones, create or copy this link while using your Tailscale HTTPS address, with both devices connected to Tailscale.</p><label>Invitation link<input id=\"invite-url\" value=\"{H(path)}\" readonly></label><button type=\"button\" class=\"button secondary\" id=\"copy-invite\">Copy invitation</button><span id=\"copy-status\" role=\"status\"></span></section>";
        }
        content += $"<section class=\"account-section\"><h3>Change your password</h3><form method=\"post\" action=\"/account/password\">{Token(c, a)}{PasswordField("Current password", "currentPassword", "current-password")}{PasswordField("New password", "password", "new-password", true)}{PasswordField("Confirm new password", "confirmPassword", "new-password", true)}<button class=\"button secondary\">Save new password</button></form></section><form method=\"post\" action=\"/logout\" class=\"logout-form\">{Token(c, a)}<button class=\"text-button\">Sign out</button></form>";
        return Page(c, "Your side of the field.", content, error);
    }

    public static void MapAccountPages(this WebApplication app)
    {
        app.MapGet("/login", async (HttpContext c, IAntiforgery a, AccountsDb db) => c.User.Identity?.IsAuthenticated == true
            ? Results.Redirect("/") : Login(c, a, !await db.Users.AnyAsync()));
        app.MapGet("/register", async (HttpContext c, IAntiforgery a, AccountsDb db) =>
        {
            if (c.User.Identity?.IsAuthenticated == true) return Results.Redirect("/account");
            var count = await db.Users.CountAsync();
            if (count >= 2) return Login(c, a, false, "Both player accounts are set up. Sign in to continue.");
            var invite = c.Request.Query["invite"].ToString();
            if (count == 0) return Register(c, a, "", true);
            var hash = AccountRegistration.HashToken(invite);
            var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.TokenHash == hash);
            if (invitation is null || invitation.UsedAt is not null || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
                return Login(c, a, false, "Ask your league partner for a current invitation link.");
            return Register(c, a, invite, false);
        });
        app.MapPost("/register", async (HttpContext c, IAntiforgery a, AccountsDb db, UserManager<PlayerAccount> users, SignInManager<PlayerAccount> signIn, AccountRegistration registration, GameService games) =>
        {
            if (c.User.Identity?.IsAuthenticated == true) return Results.Redirect("/account");
            if (!await a.IsRequestValidAsync(c))
            {
                var invite = c.Request.HasFormContentType ? (await c.Request.ReadFormAsync())["invite"].ToString() : "";
                return ExpiredForm(c, "/register?invite=" + Uri.EscapeDataString(invite));
            }
            var f = await c.Request.ReadFormAsync();
            var (user, error) = await registration.Register(db, users, games, f["email"].ToString(), f["name"].ToString(), f["password"].ToString(), f["confirmPassword"].ToString(), f["invite"].ToString());
            if (user is null) return Register(c, a, f["invite"].ToString(), !await db.Users.AnyAsync(), error, f["email"].ToString(), f["name"].ToString(), 400);
            await signIn.SignInAsync(user, isPersistent: true);
            return Results.Redirect(user.PlayerSlot == 0 ? "/account?welcome=1" : "/");
        }).RequireRateLimiting("registration");
        app.MapPost("/login", async (HttpContext c, IAntiforgery a, AccountsDb db, UserManager<PlayerAccount> users, SignInManager<PlayerAccount> signIn) =>
        {
            // Another tab may have signed in since this form was opened. Redirecting
            // changes no account state and avoids rejecting its now-anonymous token.
            if (c.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");
            if (!await a.IsRequestValidAsync(c))
                return Login(c, a, !await db.Users.AnyAsync(), "Your sign-in page expired or your session changed. Please enter your details again.");
            var f = await c.Request.ReadFormAsync(); var email = f["email"].ToString().Trim(); var password = f["password"].ToString();
            var user = email.Length <= 254 ? await users.FindByEmailAsync(email) : null;
            var result = user is not null && password.Length <= 128
                ? await signIn.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true) : SignInResult.Failed;
            if (result.Succeeded) return Results.Redirect("/");
            await Task.Delay(300);
            return Login(c, a, false, "Couldn't sign in. Check your email and app password. After repeated attempts, wait 15 minutes before retrying.", email);
        }).RequireRateLimiting("login");
        app.MapGet("/account", async (HttpContext c, IAntiforgery a, UserManager<PlayerAccount> users, AccountsDb db) =>
        {
            var user = await users.GetUserAsync(c.User);
            return user is null ? Results.Redirect("/login") : await Account(c, a, user, db);
        });
        app.MapPost("/account/invite", async (HttpContext c, IAntiforgery a, AccountsDb db, UserManager<PlayerAccount> users, AccountRegistration registration) =>
        {
            if (!await a.IsRequestValidAsync(c)) return ExpiredForm(c, "/account");
            var user = await users.GetUserAsync(c.User);
            if (user is null) return Results.Redirect("/login");
            if (user.PlayerSlot != 0) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var email = (await c.Request.ReadFormAsync())["email"].ToString();
            try { var token = await registration.Invite(db, email); return await Account(c, a, user, db, invitation: token, invitedEmail: email); }
            catch (InvalidOperationException ex) { return await Account(c, a, user, db, ex.Message); }
        }).RequireRateLimiting("registration");
        app.MapPost("/account/password", async (HttpContext c, IAntiforgery a, AccountsDb db, UserManager<PlayerAccount> users, SignInManager<PlayerAccount> signIn) =>
        {
            if (!await a.IsRequestValidAsync(c)) return ExpiredForm(c, "/account");
            var user = await users.GetUserAsync(c.User);
            if (user is null) return Results.Redirect("/login");
            var f = await c.Request.ReadFormAsync(); var password = f["password"].ToString();
            if (password.Length is < 12 or > 128 || f["currentPassword"].ToString().Length > 128 || password != f["confirmPassword"])
                return await Account(c, a, user, db, "Use matching passwords between 12 and 128 characters.");
            var result = await users.ChangePasswordAsync(user, f["currentPassword"].ToString(), password);
            if (!result.Succeeded) return await Account(c, a, user, db, "Couldn't change your password. Check your current password and try again.");
            await signIn.RefreshSignInAsync(user);
            return await Account(c, a, user, db, passwordChanged: true);
        }).RequireRateLimiting("login");
        app.MapPost("/logout", async (HttpContext c, IAntiforgery a, SignInManager<PlayerAccount> signIn) =>
        {
            if (!await a.IsRequestValidAsync(c)) return ExpiredForm(c, "/account");
            await signIn.SignOutAsync(); return Results.Redirect("/login");
        });
    }
}
