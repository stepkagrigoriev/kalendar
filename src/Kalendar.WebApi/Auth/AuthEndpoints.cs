using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kalendar.WebApi.Auth;

static class AuthEndpoints
{
    public static void AddUserAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = JwtSettings.Read(configuration);
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher<UserDocument>, PasswordHasher<UserDocument>>();
        services.AddAuthentication().AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = settings.ValidationParameters();
        });
        services.AddAuthorization();
        services.AddProblemDetails();
        services.AddExceptionHandler(options => options.StatusCodeSelector = exception =>
            exception is BadHttpRequestException requestException ? requestException.StatusCode : StatusCodes.Status500InternalServerError);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
    }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/register", RegisterAsync).RequireRateLimiting("auth");
        app.MapPost("/api/auth/login", LoginAsync).RequireRateLimiting("auth");
        app.MapGet("/api/users/me", async (ClaimsPrincipal principal, IMongoDatabase database, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(principal.FindFirstValue("sub"), out var id))
                return Results.Problem(statusCode: 401);
            var user = await database.GetCollection<UserDocument>("users").Find(x => x.Id == id && x.IsActive).FirstOrDefaultAsync(ct);
            return user is null ? Results.Problem(statusCode: 401) : Results.Ok(new { id = user.Id.ToString(), user.Username });
        }).RequireAuthorization();
    }

    internal static Dictionary<string, string[]> Validate(Credentials request)
    {
        var errors = new Dictionary<string, string[]>();
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username) || username.Length > 50 || username.Any(char.IsWhiteSpace))
            errors["username"] = ["Укажите имя пользователя длиной до 50 символов без пробелов."];
        if (request.Password is null || request.Password.Length is < 8 or > 128)
            errors["password"] = ["Пароль должен содержать от 8 до 128 символов."];
        return errors;
    }

    static async Task<IResult> RegisterAsync(Credentials request, IMongoDatabase database,
        IPasswordHasher<UserDocument> hasher, TimeProvider clock, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count != 0)
            return TypedResults.ValidationProblem(errors);
        var now = clock.GetUtcNow().UtcDateTime;
        var user = new UserDocument
        {
            Id = ObjectId.GenerateNewId(),
            Username = request.Username.Trim(),
            NormalizedUsername = request.Username.Trim().ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = true
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        try
        {
            await database.GetCollection<UserDocument>("users").InsertOneAsync(user, cancellationToken: ct);
            return Results.Created($"/api/users/me", new { id = user.Id.ToString() });
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return Results.Problem(statusCode: 409, title: "Имя пользователя уже занято.");
        }
    }

    static async Task<IResult> LoginAsync(Credentials request, IMongoDatabase database,
        IPasswordHasher<UserDocument> hasher, JwtSettings settings, TimeProvider clock, CancellationToken ct)
    {
        var users = database.GetCollection<UserDocument>("users");
        var normalizedUsername = request.Username?.Trim().ToUpperInvariant();
        var user = normalizedUsername is null ? null : await users.Find(x => x.NormalizedUsername == normalizedUsername).FirstOrDefaultAsync(ct);
        var result = user is null || request.Password is null || request.Password.Length > 128
            ? PasswordVerificationResult.Failed : hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (user is null || !user.IsActive || result == PasswordVerificationResult.Failed)
            return Results.Problem(statusCode: 401, title: "Неверные учётные данные.");
        var now = clock.GetUtcNow();
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            await users.UpdateOneAsync(x => x.Id == user.Id,
                Builders<UserDocument>.Update.Set(x => x.PasswordHash, hasher.HashPassword(user, request.Password))
                    .Set(x => x.UpdatedAt, now.UtcDateTime), cancellationToken: ct);
        var expiresAt = now.AddMinutes(settings.LifetimeMinutes);
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
            [new Claim("sub", user.Id.ToString()), new Claim("username", user.Username)],
            now.UtcDateTime, expiresAt.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)), SecurityAlgorithms.HmacSha256));
        return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresAt });
    }

    internal sealed record Credentials(string Username, string Password);
}
