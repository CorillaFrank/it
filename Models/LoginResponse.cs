namespace HamulProto.Models;

public sealed class LoginResponse
{
    public long UserId { get; set; }
    public string Nickname { get; set; } = "";
    public string Token { get; set; } = "";
}
