using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// What one read of ETABS's own "unsaved changes" state concluded (card v2.4 §1).
///
/// <para>Only a dirty-flag-class signal qualifies: ETABS's own unsaved-changes state, or an
/// equivalent that live check L7' proves complete. Checksums, object counts and table hashes
/// were DROPPED as candidates because their coverage cannot be proven.</para>
/// </summary>
public enum ChangeSignalState
{
    /// <summary>No qualifying signal exists in this build. Every stamp is then unverified.</summary>
    Unsupported,

    /// <summary>The signal reads clean: ETABS reports no unsaved changes.</summary>
    Clean,

    /// <summary>The signal reads dirty: ETABS reports unsaved changes.</summary>
    Dirty,

    /// <summary>
    /// The signal exists but L7' did not prove it detects every edit category (geometry,
    /// assignment, load assignment, definition, analysis/design setting). A partial signal
    /// is not an assurance, so it selects the fallback exactly like Unsupported.
    /// </summary>
    Incomplete
}

/// <summary>One read of the change signal, and where it came from.</summary>
public sealed record ChangeSignalReading(ChangeSignalState State, string? Source = null)
{
    public static readonly ChangeSignalReading Unsupported = new(ChangeSignalState.Unsupported);

    /// <summary>True only for a clean read of a qualifying, complete signal.</summary>
    public bool IsVerifiedClean => State == ChangeSignalState.Clean;
}

/// <summary>
/// The source of the change signal, behind an interface so L7' can plug the real one in
/// without touching the save or the close path. The default reports "unsupported".
/// </summary>
public interface IEtabsChangeSignalSource
{
    /// <summary>
    /// Reads the signal. Called on the STA, in the SAME call as the save (right after
    /// <c>SaveFile</c> returned 0) and again by <c>shutdown-if-unchanged</c>.
    /// </summary>
    ChangeSignalReading Read(ETABSApplication application);
}

/// <summary>Reports that no qualifying signal exists, so every save stamp is unverified.</summary>
public sealed class UnsupportedChangeSignalSource : IEtabsChangeSignalSource
{
    public ChangeSignalReading Read(ETABSApplication application) => ChangeSignalReading.Unsupported;
}
