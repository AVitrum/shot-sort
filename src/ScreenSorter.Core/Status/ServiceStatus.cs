namespace ScreenSorter.Core.Status;

/// <summary>How one part of the app is doing.</summary>
public enum ServiceState
{
    /// <summary>Working.</summary>
    Ok,

    /// <summary>Not a fault: it is waiting for something that will happen by itself.</summary>
    Waiting,

    /// <summary>Not working until the user does something.</summary>
    Problem,
}

/// <summary>The result of checking one part of the app.</summary>
/// <param name="Name">What was checked, for display.</param>
/// <param name="State">The result.</param>
/// <param name="Detail">One sentence for the user.</param>
/// <param name="Fix">What the user can do about it, if anything.</param>
/// <param name="FixCommand">A command the user can copy to fix it, if there is one.</param>
/// <param name="NeedsAttention">
/// Whether this is worth interrupting the user for. A helper the user never set up is a problem but not news; a helper
/// that files are waiting for, or that was running a moment ago, is.
/// </param>
public sealed record ServiceCheck(
    string Name,
    ServiceState State,
    string Detail,
    string? Fix = null,
    string? FixCommand = null,
    bool NeedsAttention = false);

/// <summary>Checks one part of the app.</summary>
public interface IServiceProbe
{
    /// <summary>Runs the check. Failing to reach something is a result, not an exception.</summary>
    /// <param name="ct">Cancels the check; throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The result.</returns>
    Task<ServiceCheck> CheckAsync(CancellationToken ct);
}
