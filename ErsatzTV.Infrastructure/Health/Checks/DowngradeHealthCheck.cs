using ErsatzTV.Core.Health;
using ErsatzTV.Core.Health.Checks;
using ErsatzTV.Core.Interfaces.Database;

namespace ErsatzTV.Infrastructure.Health.Checks;

public class DowngradeHealthCheck(IDatabaseMigrations databaseMigrations) : BaseHealthCheck, IDowngradeHealthCheck
{
    public override string Title => "Retour à une version antérieure";

    public async Task<HealthCheckResult> Check(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> unknownMigrations = await databaseMigrations.GetUnknownMigrations();
        if (unknownMigrations.Any())
        {
            return FailResult(
                "Version antérieure détectée : NON PRIS EN CHARGE, la stabilité sera affectée",
                "Version antérieure détectée",
                new HealthCheckLink("https://ersatztv.org/docs/installation/#downgrading"));
        }

        return NotApplicableResult();
    }
}
