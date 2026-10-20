using System.Reflection;
using ErsatzTV.Core.Health;
using ErsatzTV.Core.Health.Checks;

namespace ErsatzTV.Infrastructure.Health.Checks;

public class UnifiedDockerHealthCheck : BaseHealthCheck, IUnifiedDockerHealthCheck
{
    private static readonly string InfoVersion =
        Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public override string Title => "Image Docker unifiée";

    public Task<HealthCheckResult> Check(CancellationToken cancellationToken)
    {
        if (InfoVersion.Contains("docker-vaapi") || InfoVersion.Contains("docker-nvidia") ||
            InfoVersion.Contains("docker-arm"))
        {
            return WarningResult(
                    "VAAPI, NVIDIA, and ARM docker tag suffixes are deprecated; please remove `-vaapi`, `-nvidia`, `-arm64` or `-arm` and pull the default image.",
                    "tag docker obsolète")
                .AsTask();
        }

        return NotApplicableResult().AsTask();
    }
}
