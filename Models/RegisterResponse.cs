namespace HamulProto.Models;

public sealed class RegisterResponse
{
    public string Message { get; set; } = "";
    public long UserId { get; set; }
    public string Nickname { get; set; } = "";
    public string Token { get; set; } = "";
}
