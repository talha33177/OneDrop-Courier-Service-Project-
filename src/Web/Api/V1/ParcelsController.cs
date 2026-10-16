using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Application.Parcels.Browse;
using Application.Parcels.CreateParcel;
using Application.Parcels.ParcelActions;
using Domain.Parcels;
using Web.Authentication;

namespace Web.Api.V1;

/// <summary>
/// A parcel as the API returns it: its status, where the money stands, the day it should be delivered by (DueOn,
/// once picked up when a time was promised; Late when still on its way after it), a problem the courier flagged
/// on it (Issue: inReview or exceptional, with IssueNote; null when there is none), its speed and contents (their codes) with
/// the extras they added, the door's pin (null when not pinned) and, while it waits for pickup, the day it is collected.
/// </summary>
public sealed record ParcelStatusResult(
    string TrackingCode,
    string? MerchantReference,
    ParcelStatus Status,
    string Area,
    string RecipientName,
    decimal CodAmount,
    decimal? CollectedAmount,
    decimal DeliveryCharge,
    decimal TotalCharge,
    int Attempts,
    string? Reason,
    DateTime Booked,
    DateTime? Closed,
    DateOnly? DueOn,
    bool Late,
    ParcelIssue? Issue,
    string? IssueNote,
    string Speed,
    string ProductType,
    decimal SpeedCharge,
    decimal ProductCharge,
    decimal? RecipientLatitude,
    decimal? RecipientLongitude,
    DateOnly? PickupOn)
{
    /// <summary>
    /// What the recipient paid online before the door (the rider collects the rest); null while it is paid in cash.
    /// </summary>
    public decimal? PaidOnline { get; init; }
}

public sealed record CancelParcelRequest(string? Reason);

/// <summary>The merchant parcel API. Authenticated by API key; everything is scoped to the key's merchant.</summary>
[ApiController]
[Route("api/v1/parcels")]
[Authorize(Policy = Policies.MerchantApi)]
public class ParcelsController : ControllerBase
{
    /// <summary>
    /// Books a parcel. Send an Idempotency-Key header to make retries safe: the same key returns the same parcel (200)
    /// instead of booking a second one (201).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<CreateParcelResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<CreateParcelResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateParcelResult>> Create(
        CreateParcelCommand command,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromServices] CreateParcelHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(command with { IdempotencyKey = idempotencyKey }, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error!);
        }

        return result.Value.Replayed
            ? Ok(result.Value)
            : CreatedAtAction(nameof(Get), new { code = result.Value.TrackingCode }, result.Value);
    }

    /// <summary>One of the caller's parcels. Anyone else's parcel is a 404, never a 403.</summary>
    [HttpGet("{code}")]
    [ProducesResponseType<ParcelStatusResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ParcelStatusResult>> Get(
        string code,
        [FromServices] ParcelDetailsHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.GetAsync(code, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error!);
        }

        var parcel = result.Value;

        return Ok(new ParcelStatusResult(
            parcel.TrackingCode,
            parcel.MerchantReference,
            parcel.Status,
            parcel.Area,
            parcel.RecipientName,
            parcel.CodAmount,
            parcel.CollectedAmount,
            parcel.DeliveryCharge,
            parcel.TotalCharge,
            parcel.Attempts,
            parcel.Status == ParcelStatus.OnHold ? parcel.HoldReason : parcel.ReturnReason,
            parcel.Booked,
            parcel.Closed,
            parcel.DueOn,
            parcel.Late,
            parcel.Issue,
            parcel.IssueNote,
            parcel.Speed,
            parcel.ProductType,
            parcel.SpeedCharge,
            parcel.ProductCharge,
            parcel.Pin?.Latitude,
            parcel.Pin?.Longitude,
            parcel.PickupOn)
        {
            PaidOnline = parcel.PaidOnline
        });
    }

    /// <summary>Cancels a parcel not yet picked up.</summary>
    [HttpPost("{code}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Cancel(
        string code,
        CancelParcelRequest? request,
        [FromServices] ParcelActionsHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.CancelAsync(code, request?.Reason, cancellationToken);

        return result.IsSuccess ? NoContent() : this.ToProblem(result.Error!);
    }
}
