namespace ScreenSorter.Core.Vision;

/// <summary>Starts the local server that runs the vision model.</summary>
public interface IVisionServerStarter
{
    /// <summary>Whether starting is switched on, the server is on this machine and the tool that starts it is installed.</summary>
    bool CanStart { get; }

    /// <summary>Whether the most recent attempt failed. Stays <see langword="false"/> until there has been an attempt.</summary>
    bool LastStartFailed { get; }

    /// <summary>Starts the server, unless something already listens on its port.</summary>
    /// <param name="ct">Cancels the attempt; throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>Whether the server was started just now; <see langword="false"/> if <see cref="CanStart"/> is off or it was already running.</returns>
    Task<bool> TryStartAsync(CancellationToken ct);
}
