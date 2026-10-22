using System.Text.Json;
using Domain.Common;
using Domain.Delivery;
using Domain.Merchants;
using Domain.Notifications;
using Domain.Parcels;
using Domain.Payments;

namespace Application.Notifications;

/// <summary>A parcel changed status: post it to its merchant's webhook. The status is the one it moved to then.</summary>
public sealed record ParcelStatusChangedMessage(long ParcelId, long MerchantId, ParcelStatus Status);

/// <summary>A parcel reached a status the recipient is texted about (out for delivery, delivered).</summary>
public sealed record RecipientTextMessage(long ParcelId, ParcelStatus Status);

/// <summary>Why a merchant is being emailed. Stored by name in the outbox, so never rename a value in flight.</summary>
public enum MerchantEmailKind
{
    PayoutSent = 1,
    SignedUp = 2,
    Approved = 3,
    Suspended = 4,
    Reactivated = 5,
    PayoutsHeld = 6,
    PayoutsReleased = 7,
    PayoutRequestRefused = 8,
    ParcelRequestAnswered = 9,
    ReturnListToConfirm = 10,
    PaymentReceived = 11,
    PaymentRefunded = 12,
    Adjustment = 13,
    Terminated = 14,
    Restored = 15
}

/// <summary>Something a merchant is told about by email; <paramref name="SubjectId"/> is the row it is about.</summary>
public sealed record MerchantEmailMessage(long MerchantId, MerchantEmailKind Kind, long SubjectId);

/// <summary>Why the courier's own super admins are emailed.</summary>
public enum CourierEmailKind
{
    MerchantSignedUp = 1
}

/// <summary>Something the courier's super admins are told about by email; <paramref name="SubjectId"/> is its row.</summary>
public sealed record CourierEmailMessage(CourierEmailKind Kind, long SubjectId);

/// <summary>Why a recipient who paid online is emailed, at the address they gave.</summary>
public enum RecipientEmailKind
{
    PaymentReceipt = 1,
    Refunded = 2
}

/// <summary>A recipient's online payment to write about.</summary>
public sealed record RecipientEmailMessage(long OnlinePaymentId, RecipientEmailKind Kind);

/// <summary>Why a login is emailed about itself.</summary>
public enum AccountEmailKind
{
    /// <summary>The person asked for a new password: a link to choose one.</summary>
    PasswordReset = 1,

    /// <summary>Someone made them a login (a moderator, staff): where to sign in, and a link to choose a password.</summary>
    LoginCreated = 2
}

/// <summary>An email to one login; the link in it is made when it is sent, never stored.</summary>
public sealed record AccountEmailMessage(long UserId, AccountEmailKind Kind);

/// <summary>
/// Turns domain events into outbox rows. The row stores the contract's name and its JSON; the ids are read after the
/// entities are saved, which is why the save writes the outbox in a second step of the same transaction.
/// </summary>
public static class OutboxContracts
{
    /// <summary>Statuses the recipient hears about by SMS.</summary>
    public static readonly ParcelStatus[] Texted =
        [ParcelStatus.OutForDelivery, ParcelStatus.Delivered, ParcelStatus.PartlyDelivered];

    public static IEnumerable<OutboxMessage> ToOutbox(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case ParcelStatusChanged changed:
                yield return Write(new ParcelStatusChangedMessage(changed.Parcel.Id, changed.Parcel.MerchantId, changed.Status));
                if (Texted.Contains(changed.Status))
                {
                    yield return Write(new RecipientTextMessage(changed.Parcel.Id, changed.Status));
                }

                break;

            case ParcelBooked booked:
                yield return Write(new RecipientTextMessage(booked.Parcel.Id, ParcelStatus.Pending));
                break;

            case PayoutPaid paid:
                yield return Write(new MerchantEmailMessage(
                    paid.Payout.MerchantId,
                    MerchantEmailKind.PayoutSent,
                    paid.Payout.Id));
                break;

            case MerchantAccountChanged changed:
                yield return Write(new MerchantEmailMessage(
                    changed.Merchant.Id,
                    changed.Notice switch
                    {
                        MerchantNotice.SignedUp => MerchantEmailKind.SignedUp,
                        MerchantNotice.Approved => MerchantEmailKind.Approved,
                        MerchantNotice.Suspended => MerchantEmailKind.Suspended,
                        MerchantNotice.Reactivated => MerchantEmailKind.Reactivated,
                        MerchantNotice.PayoutsHeld => MerchantEmailKind.PayoutsHeld,
                        MerchantNotice.Terminated => MerchantEmailKind.Terminated,
                        MerchantNotice.Restored => MerchantEmailKind.Restored,
                        _ => MerchantEmailKind.PayoutsReleased
                    },
                    changed.Merchant.Id));
                if (changed.Notice == MerchantNotice.SignedUp)
                {
                    yield return Write(new CourierEmailMessage(CourierEmailKind.MerchantSignedUp, changed.Merchant.Id));
                }

                break;

            case PayoutRequestRefused refused:
                yield return Write(new MerchantEmailMessage(refused.Request.MerchantId, MerchantEmailKind.PayoutRequestRefused, refused.Request.Id));
                break;

            case ParcelRequestAnswered answered:
                yield return Write(new MerchantEmailMessage(answered.Request.MerchantId, MerchantEmailKind.ParcelRequestAnswered, answered.Request.Id));
                break;

            case ReturnListHandedOver handed:
                yield return Write(new MerchantEmailMessage(handed.List.MerchantId, MerchantEmailKind.ReturnListToConfirm, handed.List.Id));
                break;

            case AdjustmentWritten written:
                yield return Write(new MerchantEmailMessage(written.Entry.MerchantId, MerchantEmailKind.Adjustment, written.Entry.Id));
                break;

            case OnlinePaymentPaid paid:
                if (!paid.Payment.IsForParcel)
                {
                    yield return Write(new MerchantEmailMessage(paid.Payment.MerchantId, MerchantEmailKind.PaymentReceived, paid.Payment.Id));
                }
                else if (paid.Payment.PayerEmail is not null)
                {
                    yield return Write(new RecipientEmailMessage(paid.Payment.Id, RecipientEmailKind.PaymentReceipt));
                }

                break;

            case OnlinePaymentRefunded refunded:
                if (!refunded.Payment.IsForParcel)
                {
                    yield return Write(new MerchantEmailMessage(refunded.Payment.MerchantId, MerchantEmailKind.PaymentRefunded, refunded.Payment.Id));
                }
                else if (refunded.Payment.PayerEmail is not null)
                {
                    yield return Write(new RecipientEmailMessage(refunded.Payment.Id, RecipientEmailKind.Refunded));
                }

                break;

            default:
                throw new InvalidOperationException($"No outbox message is defined for {domainEvent.GetType().Name}.");
        }
    }

    /// <summary>An outbox row for a message no entity raises, such as a login's own email.</summary>
    public static OutboxMessage Write<TMessage>(TMessage message)
    {
        return OutboxMessage.Create(typeof(TMessage).Name, JsonSerializer.Serialize(message));
    }
}
