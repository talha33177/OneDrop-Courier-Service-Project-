using FluentValidation;
using Domain.Common;
using Domain.Parcels;
using Domain.Pricing;

namespace Application.Parcels.CreateParcel;

/// <summary>Shape checks. Rules that need the database (the area exists, the pickup point is ours) live in the handler.</summary>
public class CreateParcelValidator : AbstractValidator<CreateParcelCommand>
{
    public CreateParcelValidator()
    {
        RuleFor(command => command.MerchantReference).MaximumLength(100);
        RuleFor(command => command.IdempotencyKey).MaximumLength(100);
        RuleFor(command => command.RecipientName)
            .NotEmpty()
            .WithMessage("Enter the recipient's name.")
            .MaximumLength(200);
        RuleFor(command => command.RecipientPhone)
            .Must(phone => PhoneNumber.Parse(phone).IsSuccess)
            .WithMessage("Enter a Bangladeshi mobile number such as 01712345678.");
        RuleFor(command => command.RecipientAddress)
            .NotEmpty()
            .WithMessage("Enter the delivery address.")
            .MaximumLength(500);
        RuleFor(command => command)
            .Must(command => command.AreaId.HasValue || !string.IsNullOrWhiteSpace(command.Area))
            .OverridePropertyName(nameof(CreateParcelCommand.Area))
            .WithMessage("Choose the delivery area.");
        RuleFor(command => command.CodAmount)
            .InclusiveBetween(0, Parcel.MaxCodAmount)
            .WithMessage($"The cash to collect must be between ৳0 and ৳{Parcel.MaxCodAmount:N0}.");
        RuleFor(command => command.WeightKg)
            .InclusiveBetween(0.01m, Parcel.MaxWeightGrams / 1000m)
            .WithMessage($"The weight must be between 0.01 and {Parcel.MaxWeightGrams / 1000} kg.");
        RuleFor(command => command.Speed).MaximumLength(DeliveryOption.MaxNameLength);
        RuleFor(command => command.ProductType).MaximumLength(DeliveryOption.MaxNameLength);
        RuleFor(command => command)
            .Must(command => GeoPoint.From(command.RecipientLatitude, command.RecipientLongitude).IsSuccess)
            .OverridePropertyName(nameof(CreateParcelCommand.RecipientLatitude))
            .WithMessage("Pin the door with both a latitude (-90 to 90) and a longitude (-180 to 180), or leave both out.");
        RuleFor(command => command.ItemDescription).MaximumLength(200);
        RuleFor(command => command.Note).MaximumLength(500);
    }
}
