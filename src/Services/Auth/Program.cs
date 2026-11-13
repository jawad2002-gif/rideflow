using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Dev-only values. Real secrets go in user-secrets or .env, never in git.
const string JwtKey = "rideflow-dev-secret-key-change-me-0123456789abcdef";
const string Issuer = "rideflow-auth";
const string Audience = "rideflow";

builder.Services.AddDbContext<AuthDb>(o => o.UseNpgsql(
    "Host=localhost;Port=5432;Database=auth;Username=rideflow;Password=rideflow"));
builder.Services.AddIdentityCore<AppUser>(o =>
{
    o.Password.RequireNonAlphanumeric = false;
    o.Password.RequiredLength = 6;
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<AuthDb>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
    db.Database.EnsureCreated();
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var r in new[] { "Rider", "Driver", "Admin" })
        if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));
}

app.MapGet("/health/live", () => Results.Ok("alive"));

app.MapPost("/auth/register", async (RegisterRequest req, UserManager<AppUser> users) =>
{
    // Admin accounts cannot be self-registered
    if (req.Role is not ("Rider" or "Driver"))
        return Results.BadRequest(new { error = "Role must be Rider or Driver" });

    var user = new AppUser { UserName = req.Email, Email = req.Email, DisplayName = req.DisplayName };
    var created = await users.CreateAsync(user, req.Password);
    if (!created.Succeeded)
        return Results.BadRequest(new { error = string.Join("; ", created.Errors.Select(e => e.Description)) });

    await users.AddToRoleAsync(user, req.Role);
    return Results.Ok(new { user.Id, user.Email, role = req.Role });
});

app.MapPost("/auth/login", async (LoginRequest req, UserManager<AppUser> users, AuthDb db) =>
{
    var user = await users.FindByEmailAsync(req.Email);
    if (user is null || !await users.CheckPasswordAsync(user, req.Password))
        return Results.Unauthorized();

    var role = (await users.GetRolesAsync(user)).FirstOrDefault() ?? "Rider";
    return Results.Ok(await IssueTokens(user, role, db));
});

app.MapPost("/auth/refresh", async (RefreshRequest req, UserManager<AppUser> users, AuthDb db) =>
{
    var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == req.RefreshToken);
    if (stored is null || stored.Revoked || stored.ExpiresAt < DateTime.UtcNow)
        return Results.Unauthorized();

    stored.Revoked = true; // rotation: each refresh token works once
    var user = await users.FindByIdAsync(stored.UserId);
    if (user is null) return Results.Unauthorized();

    var role = (await users.GetRolesAsync(user)).FirstOrDefault() ?? "Rider";
    return Results.Ok(await IssueTokens(user, role, db));
});

app.Run("http://localhost:5001");

static async Task<object> IssueTokens(AppUser user, string role, AuthDb db)
{
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
    var expires = DateTime.UtcNow.AddMinutes(15);
    var jwt = new JwtSecurityToken(
        issuer: Issuer,
        audience: Audience,
        claims: new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new Claim(ClaimTypes.Role, role),
            new Claim("name", user.DisplayName ?? "")
        },
        expires: expires,
        signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

    var refresh = new RefreshToken
    {
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        UserId = user.Id,
        ExpiresAt = DateTime.UtcNow.AddDays(7)
    };
    db.RefreshTokens.Add(refresh);
    await db.SaveChangesAsync();

    return new
    {
        accessToken = new JwtSecurityTokenHandler().WriteToken(jwt),
        refreshToken = refresh.Token,
        expiresAt = expires,
        userId = user.Id,
        role,
        name = user.DisplayName
    };
}

class AppUser : IdentityUser { public string? DisplayName { get; set; } }

class RefreshToken
{
    public int Id { get; set; }
    public string Token { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}

class AuthDb : IdentityDbContext<AppUser>
{
    public AuthDb(DbContextOptions<AuthDb> o) : base(o) { }
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
}

record RegisterRequest(string Email, string Password, string DisplayName, string Role);
record LoginRequest(string Email, string Password);
record RefreshRequest(string RefreshToken);
