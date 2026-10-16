using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Application.Dashboards;
using Application.Delivery.Capacities;
using Application.Delivery.Pickups;
using Application.Delivery.Returns;
using Application.Delivery.RiderDay;
using Application.Delivery.Riders;
using Application.Hubs;
using Application.Hubs.AssignParcels;
using Application.Hubs.HubBoard;
using Application.Hubs.HubOverview;
using Application.Hubs.HubScan;
using Application.Hubs.Runs;
using Application.Merchants.Account;
using Application.Merchants.Admin;
using Application.Merchants.ApiKeys;
using Application.Merchants.Businesses;
using Application.Merchants.Moderators;
using Application.Merchants.Onboarding;
using Application.Merchants.PayoutAccounts;
using Application.Merchants.Webhook;
using Application.Network.Coverage;
using Application.Network.ListAreas;
using Application.Notifications.Bell;
using Application.Notifications.FailedMessages;
using Application.Notifications.SendEmails;
using Application.Notifications.SendOutbox;
using Application.Notifications.SendWebhooks;
using Application.Parcels;
using Application.Parcels.Browse;
using Application.Parcels.BulkImport;
using Application.Parcels.CreateParcel;
using Application.Parcels.FraudCheck;
using Application.Parcels.Labels;
using Application.Parcels.ParcelActions;
using Application.Parcels.Quote;
using Application.Parcels.Requests;
using Application.Parcels.Stats;
using Application.Parcels.Stuck;
using Application.Parcels.Track;
using Application.Payments.AdminPayouts;
using Application.Payments.MerchantPayments;
using Application.Payments.OnlinePayments;
using Application.Payments.PayoutRequests;
using Application.Payments.RunPayouts;
using Application.Platform.Settings;
using Application.Platform.Staff;
using Application.Reports;
using Application.Pricing.DeliveryOptions;
using Application.Pricing.Rates;

namespace Application;

public static class DependencyInjection
{
    /// <summary>Handlers are plain scoped classes, injected where they are used. No mediator.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, ServiceLifetime.Singleton);

        services.AddScoped<ParcelBooking>();
        services.AddScoped<CreateParcelHandler>();
        services.AddScoped<ParcelListHandler>();
        services.AddScoped<ParcelDetailsHandler>();
        services.AddScoped<ParcelActionsHandler>();
        services.AddScoped<ParcelLabelsHandler>();
        services.AddScoped<BulkImportHandler>();
        services.AddScoped<TrackHandler>();
        services.AddScoped<QuoteHandler>();
        services.AddScoped<FraudCheckHandler>();
        services.AddScoped<ParcelStatsHandler>();
        services.AddScoped<ParcelRequestsHandler>();

        services.AddScoped<HubDirectory>();
        services.AddScoped<HubScanHandler>();
        services.AddScoped<HubBoardHandler>();
        services.AddScoped<HubOverviewHandler>();
        services.AddScoped<AssignParcelsHandler>();
        services.AddScoped<RunsHandler>();

        services.AddScoped<PickupsHandler>();
        services.AddScoped<PickupSchedule>();
        services.AddScoped<PickupsJob>();
        services.AddScoped<RiderDayHandler>();
        services.AddScoped<AdminRidersHandler>();
        services.AddScoped<VehicleCapacitiesHandler>();
        services.AddScoped<RiderLoads>();
        services.AddScoped<ReturnListsHandler>();
        services.AddScoped<MerchantReturnsHandler>();

        services.AddScoped<MerchantOnboarding>();
        services.AddScoped<AdminMerchantsHandler>();
        services.AddScoped<MerchantAccountHandler>();
        services.AddScoped<MerchantBusinessesHandler>();
        services.AddScoped<MerchantPictureHandler>();
        services.AddScoped<MerchantApiKeysHandler>();
        services.AddScoped<MerchantAccess>();
        services.AddScoped<ModeratorsHandler>();
        services.AddScoped<PayoutAccountsHandler>();
        services.AddScoped<MerchantWebhookHandler>();

        services.AddScoped<PayoutsJob>();
        services.AddScoped<MerchantPaymentsHandler>();
        services.AddScoped<AdminPayoutsHandler>();
        services.AddScoped<OnlinePaymentsHandler>();
        services.AddScoped<RecipientPaymentsHandler>();
        services.AddScoped<PayoutRequestsHandler>();
        services.AddScoped<CheckOnlinePaymentsJob>();
        services.AddScoped<ReportsHandler>();

        services.AddScoped<RatesHandler>();
        services.AddScoped<DeliveryOptionsHandler>();
        services.AddScoped<CourierSettingsHandler>();
        services.AddScoped<StaffHandler>();
        services.AddScoped<StaffAccess>();
        services.AddScoped<StuckParcelsHandler>();
        services.AddScoped<ListAreasHandler>();
        services.AddScoped<CoverageHandler>();
        services.AddScoped<CoverageAdminHandler>();
        services.AddScoped<DashboardHandler>();

        services.AddScoped<SendOutboxJob>();
        services.AddScoped<RecipientTexts>();
        services.AddScoped<SendWebhooksJob>();
        services.AddScoped<MerchantWebhooks>();
        services.AddScoped<SendEmailsJob>();
        services.AddScoped<MerchantEmails>();
        services.AddScoped<FailedMessagesHandler>();
        services.AddScoped<MerchantBellHandler>();
        services.AddScoped<AdminBellHandler>();
        services.AddScoped<MenuWorkHandler>();

        return services;
    }
}
