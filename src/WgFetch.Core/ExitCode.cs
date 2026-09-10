namespace WgFetch.Core;

/// <summary>
/// Distinct exit codes per failure class. Exit codes, log records and <c>--json</c> events must agree
/// (see docs/REQUIREMENTS.md, "CLI surface").
/// </summary>
public enum ExitCode
{
    Success = 0,
    UsageError = 1,
    Unresolved = 2,
    Ambiguous = 3,
    VerificationFailed = 4,
    HashMismatch = 5,
    MissingPrerequisite = 6,
    RequiresAuth = 7,
    RateLimited = 8,
    NetworkError = 9,
    Cancelled = 130,
}
