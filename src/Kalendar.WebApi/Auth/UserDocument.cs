using MongoDB.Bson;

namespace Kalendar.WebApi.Auth;

public sealed class UserDocument
{
    public ObjectId Id { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; }
}
