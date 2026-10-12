using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Application.Parcels.Quote;
using Domain.Pricing;
using Web.Authentication;

namespace Web.Api.V1;

/// <summary>
/// GET /api/v1/charge: what a parcel would cost the merchant before it is booked, for a checkout's shipping line or the
/// merchant's own system. The same route and rate a booking gets now, at the speed (regular or express) and for the
/// contents (general, liquid, fragile, freshFood or frozen) asked for, with what the route offers of each.
/// </summary>
[ApiController]
[Route("api/v1/charge")]
[Authorize(Policy = Policies.MerchantApi)]
public class ChargeController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<QuoteView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<QuoteView>> Get(
        [FromQuery] long? areaId,
        [FromQuery] string? area,
        [FromQuery] long? pickupPointId,
        [FromQuery] decimal weightKg,
        [FromQuery] decimal codAmount,
        [FromServices] QuoteHandler handler,
        CancellationToken cancellationToken,
        [FromQuery] DeliverySpeed speed = DeliverySpeed.Regular,
        [FromQuery] ProductType productType = ProductType.General)
    {
        var result = await handler.QuoteAsync(areaId, area, pickupPointId, weightKg, codAmount, speed, productType, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }
}
