using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ErsatzTV.Application.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Iptv;
using ErsatzTV.Filters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

/// <summary>
///     Guide des programmes en JSON, prêt pour un site web (ex. NounoursTV) :
///     chaînes avec logo et liens de flux, programme en cours / suivant, et grille détaillée
///     (titres, résumés, images, saison/épisode, catégories…).
///     Construit à partir du même guide que <c>xmltv.xml</c> : ce que voit le site = ce que voient les lecteurs IPTV.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[ServiceFilter(typeof(ConditionalIptvAuthorizeFilter))]
public class NounoursEpgController(IMediator mediator) : ControllerBase
{
    [HttpGet("iptv/nounours/epg.json")]
    public async Task<IActionResult> GetEpg(
        [FromQuery] double heures = 24,
        [FromQuery] string chaines = null,
        [FromQuery] bool passes = false,
        CancellationToken cancellationToken = default)
    {
        string accessToken = Request.Query["access_token"];
        string requestBase = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

        Either<BaseError, ChannelGuide> guide = await mediator.Send(
            new GetChannelGuide(Request.Scheme, Request.Host.ToString(), Request.PathBase, accessToken),
            cancellationToken);

        return guide.Match<IActionResult>(
            g => Ok(ErsatzTV.Application.Nounours.EpgJson.Build(g.ToXml(), requestBase, accessToken, heures, chaines, passes)),
            error => Problem(error.ToString(), statusCode: StatusCodes.Status503ServiceUnavailable));
    }

}
