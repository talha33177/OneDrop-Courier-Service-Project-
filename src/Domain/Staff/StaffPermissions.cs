namespace Domain.Staff;

/// <summary>
/// What one of the courier's staff may do. Stored as INT; the values are a bit field, so never renumber one that has
/// been saved. The hub's desks are worked at one hub (or at every hub from head office); the head office's are worked
/// only from head office. Super admins are not listed here: their role lets them do everything.
/// </summary>
[Flags]
public enum StaffPermissions
{
    None = 0,

    /// <summary>Receive parcels on the scanner, weigh them and flag a problem on one.</summary>
    Receive = 1,

    /// <summary>Send parcels on to other hubs on the scanner (line-haul).</summary>
    Dispatch = 2,

    /// <summary>Today's pickups, assigning parcels to riders and their run sheets.</summary>
    DeliveryDesk = 4,

    /// <summary>Return lists, handing returns back at the counter and sending a parcel back to its merchant.</summary>
    Returns = 8,

    /// <summary>Closing each rider's run with the cash handed in.</summary>
    CashDesk = 16,

    /// <summary>Head office: the dashboard, stuck parcels, failed messages, riders and merchants' requests.</summary>
    Operations = 32,

    /// <summary>Head office: sign-ups, merchants' accounts and their requests.</summary>
    Merchants = 64,

    /// <summary>Head office: payouts, adjustments, online payments, payout requests and reports.</summary>
    Finance = 128,

    /// <summary>Head office: coverage, rates, vehicles and the courier's settings.</summary>
    Setup = 256,

    /// <summary>Add, change and stop staff logins: a hub's own staff at a hub, everyone below super admin at head office.</summary>
    Staff = 512,

    /// <summary>Every desk at a hub.</summary>
    HubWork = Receive | Dispatch | DeliveryDesk | Returns | CashDesk,

    /// <summary>What only head office may hold.</summary>
    HeadOffice = Operations | Merchants | Finance | Setup,

    All = HubWork | HeadOffice | Staff
}
