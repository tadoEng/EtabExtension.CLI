using System.Globalization;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>An ETABS <c>major.minor.patch</c> version, compared on major.minor only.</summary>
public readonly record struct EtabsVersion(int Major, int Minor, int Patch)
{
    public static bool TryParse(string? value, out EtabsVersion version)
    {
        version = default;
        var parts = value?.Trim().Split('.') ?? [];
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>
    /// True when a model saved by this version cannot open in <paramref name="installed"/>:
    /// a newer major, or the same major and a newer minor. Patch levels never block.
    /// </summary>
    public bool IsNewerThan(EtabsVersion installed) =>
        Major != installed.Major ? Major > installed.Major : Minor > installed.Minor;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>The running ETABS's version, x.y.z.</summary>
public interface IEtabsInstalledVersion
{
    string Read();
}

/// <summary>
/// The managed ETABS's executable file version - the same <c>FileVersionInfo</c> read the
/// launcher uses (<see cref="FileEtabsVersionProbe"/>). Starts the session if needed, so
/// call it only inside a request that may start ETABS.
/// </summary>
public sealed class SessionEtabsInstalledVersion(
    IEtabsSession session,
    IEtabsVersionProbe probe) : IEtabsInstalledVersion
{
    public string Read() => probe.Read(session.GetOrStartOwned().Identity.ExecutablePath).FullVersion;
}

public sealed record EtabsVersionMismatchDetail(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("installed")] string Installed);

/// <summary>
/// The pre-check that stops a model saved by a newer ETABS from reaching <c>OpenFile</c>,
/// where ETABS 23.2 shows a modal "older version" box and the call never returns (DD-5).
/// Only runs when the caller states the model's version; without it the modal-dialog
/// watchdog is the protection.
/// </summary>
public static class EtabsModelVersionCheck
{
    /// <summary>
    /// Null when the model may be opened; otherwise the failure text, which is
    /// <c>[ETABS_VERSION_MISMATCH] … detail={"model":…,"installed":…}</c> for a newer model
    /// and a plain refusal for an unreadable version string.
    /// </summary>
    public static string? Check(string? expectedModelVersion, string installedVersion)
    {
        if (expectedModelVersion is null)
        {
            return null;
        }

        if (!EtabsVersion.TryParse(expectedModelVersion, out var model))
        {
            return $"expectedModelVersion '{expectedModelVersion}' is not a version (x.y.z); nothing was opened.";
        }

        if (!EtabsVersion.TryParse(installedVersion, out var installed))
        {
            return $"The running ETABS version '{installedVersion}' could not be read; nothing was opened.";
        }

        return model.IsNewerThan(installed)
            ? EtabsTypedFailureCodes.Format(
                EtabsTypedFailureCodes.VersionMismatch,
                $"The model was saved by ETABS {model}, which is newer than the running ETABS {installed}; it was not opened.",
                new EtabsVersionMismatchDetail(model.ToString(), installed.ToString()))
            : null;
    }
}
