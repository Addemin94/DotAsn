namespace Asn1.Cli;

/// <summary>Process exit codes (stable; suitable for CI).</summary>
internal static class ExitCodes
{
    /// <summary>Success (warnings may have been reported).</summary>
    public const int Success = 0;

    /// <summary>Errors in the ASN.1 input or an invalid/undecodable message.</summary>
    public const int Errors = 1;

    /// <summary>Invalid command-line usage or configuration.</summary>
    public const int Usage = 2;

    /// <summary>The requested feature (e.g. an encoding rule) is not implemented.</summary>
    public const int Unsupported = 3;

    /// <summary>An internal failure (a bug).</summary>
    public const int Internal = 4;

    /// <summary>The operation was cancelled.</summary>
    public const int Cancelled = 130;
}
