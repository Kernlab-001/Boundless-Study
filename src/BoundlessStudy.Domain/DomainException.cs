namespace BoundlessStudy.Domain;

/// <summary>
/// Thrown when an operation would create an invalid or contradictory domain state.
/// Carries a stable machine-readable code so the application layer can map it to a
/// stable error code for the UI (system-architecture.md §6.1 error contract).
/// </summary>
public class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
