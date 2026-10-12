using IisControlCenter.Core.Security;

namespace IisControlCenter.Web.Services;

public sealed class ForbiddenException(string message) : Exception(message);

public sealed record OpResult(bool Ok, string? Error = null)
{
    public static OpResult Success() => new(true);
    public static OpResult Fail(string error) => new(false, error);
}

public sealed record OpResult<T>(bool Ok, T? Value, string? Error = null)
{
    public static OpResult<T> Success(T value) => new(true, value);
    public static OpResult<T> Fail(string error) => new(false, default, error);
}

/// <summary>Who is doing something. Carries the evaluator so every service uses the same permission rules.</summary>
public sealed class Actor
{
    public string UserId { get; }
    public string UserName { get; }
    public string? Ip { get; }
    public string? Email { get; }
    public AccessEvaluator Access { get; }

    public Actor(string userId, string userName, string? ip, AccessEvaluator access, string? email = null)
    {
        UserId = userId; UserName = userName; Ip = ip; Access = access; Email = email;
    }

    public bool Can(Permission p, string? environment = null) => Access.Can(p, environment);
}
