namespace Nexora.Api.Contracts;

public sealed record ApiResponse<T>(T Data);
public sealed record ApiError(string Code, string Message, string RequestId);
public sealed record ApiErrorEnvelope(ApiError Error);
