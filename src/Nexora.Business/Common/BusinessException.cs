namespace Nexora.Business.Common;

public enum BusinessErrorKind { Validation, Unauthorized, Forbidden, NotFound, Conflict, ExternalFailure }

public sealed class BusinessException(string code, string message, BusinessErrorKind kind) : Exception(message)
{
    public string Code { get; } = code;
    public BusinessErrorKind Kind { get; } = kind;
}
