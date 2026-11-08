using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Kalendar.WebApi.Auth;

public sealed record JwtSettings(string SigningKey, string Issuer, string Audience, int LifetimeMinutes)
{
    public static JwtSettings Read(IConfiguration configuration)
    {
        var key = configuration["Jwt:SigningKey"];
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32 ||
            string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) ||
            !int.TryParse(configuration["Jwt:AccessTokenLifetimeMinutes"], out var minutes) || minutes <= 0)
            throw new InvalidOperationException("Некорректные настройки Jwt: необходимы ключ от 32 байт, issuer, audience и положительный срок действия.");
        return new JwtSettings(key, issuer, audience, minutes);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };
}
