namespace Chernika.Infrastructure.Services;

public sealed record HKValidationError(string Field, string Message, string Code);

public sealed record HKValidationResult(bool IsValid, IReadOnlyList<HKValidationError> Errors)
{
    public static HKValidationResult Success() => new(true, Array.Empty<HKValidationError>());

    public static HKValidationResult Fail(IReadOnlyList<HKValidationError> errors) => new(false, errors);

    public string ToUserMessage() =>
        string.Join(Environment.NewLine, Errors.Select(e => "• " + e.Message));
}

public sealed class HKCardValidationException : Exception
{
    public IReadOnlyList<HKValidationError> Errors { get; }

    public HKCardValidationException(IReadOnlyList<HKValidationError> errors)
        : base(string.Join("; ", errors.Select(e => e.Message)))
    {
        Errors = errors;
    }

    public string ToUserMessage() =>
        string.Join(Environment.NewLine, Errors.Select(e => "• " + e.Message));
}
