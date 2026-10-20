using MediatR;
using ErsatzTV.Application.Configuration;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Nounours;

/// <summary>
///     Préférences d'interface de l'édition Nounours.
///     Mode simple (par défaut) : le menu ne montre que l'essentiel. Mode expert : tout ErsatzTV.
/// </summary>
public sealed class UiPreferences(IMediator mediator)
{
    public static event EventHandler ExpertModeChanged;

    public async Task<bool> GetExpertMode(CancellationToken cancellationToken) =>
        await mediator.Send(new GetConfigElementByKey(ConfigElementKey.PagesExpertMode), cancellationToken)
            .MapT(result => bool.TryParse(result.Value, out bool value) && value)
            .IfNoneAsync(false);

    public async Task SetExpertMode(bool value, CancellationToken cancellationToken)
    {
        await mediator.Send(
            new SaveConfigElementByKey(ConfigElementKey.PagesExpertMode, value.ToString()),
            cancellationToken);
        ExpertModeChanged?.Invoke(this, EventArgs.Empty);
    }
}
