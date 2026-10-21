namespace Domain.Staff;

/// <summary>
/// A job title: where it is worked and the permissions it starts with. The admin may tick more for a person, because
/// people cover each other's desks. Stored as TINYINT; never renumber one that has been saved.
/// </summary>
public enum StaffJob : byte
{
    HubManager = 1,
    Receiving = 2,
    LineHaul = 3,
    DeliveryDesk = 4,
    ReturnsDesk = 5,
    CashDesk = 6,

    /// <summary>Covers any desk at any hub: what every hub login could do before jobs existed.</summary>
    Relief = 7,

    OperationsManager = 8,
    Accounts = 9,
    MerchantSupport = 10,

    /// <summary>Looks parcels up for the hotline and changes nothing.</summary>
    CustomerSupport = 11
}

public static class StaffJobs
{
    /// <summary>Every job in the order the staff page offers it: the hub's desks first, then head office.</summary>
    public static readonly IReadOnlyList<StaffJob> All = Enum.GetValues<StaffJob>();

    /// <summary>Whether the job is worked at one hub; otherwise it belongs to head office and reaches every hub.</summary>
    public static bool AtHub(this StaffJob job)
    {
        return job is >= StaffJob.HubManager and <= StaffJob.CashDesk;
    }

    /// <summary>The permissions the job starts with.</summary>
    public static StaffPermissions Preset(this StaffJob job)
    {
        return job switch
        {
            StaffJob.HubManager => StaffPermissions.HubWork | StaffPermissions.Staff | StaffPermissions.CashShortfalls,
            StaffJob.Receiving => StaffPermissions.Receive,
            StaffJob.LineHaul => StaffPermissions.Dispatch,
            StaffJob.DeliveryDesk => StaffPermissions.DeliveryDesk,
            StaffJob.ReturnsDesk => StaffPermissions.Returns,
            StaffJob.CashDesk => StaffPermissions.CashDesk,
            StaffJob.Relief => StaffPermissions.HubWork,
            StaffJob.OperationsManager => StaffPermissions.HubWork | StaffPermissions.Operations | StaffPermissions.CashShortfalls,
            StaffJob.Accounts => StaffPermissions.Finance,
            StaffJob.MerchantSupport => StaffPermissions.Merchants,
            _ => StaffPermissions.None
        };
    }

    public static string DisplayName(this StaffJob job)
    {
        return job switch
        {
            StaffJob.HubManager => "Hub manager",
            StaffJob.Receiving => "Receiving",
            StaffJob.LineHaul => "Line-haul",
            StaffJob.DeliveryDesk => "Delivery desk",
            StaffJob.ReturnsDesk => "Returns desk",
            StaffJob.CashDesk => "Cash desk",
            StaffJob.Relief => "Relief staff",
            StaffJob.OperationsManager => "Operations manager",
            StaffJob.Accounts => "Accounts",
            StaffJob.MerchantSupport => "Merchant support",
            _ => "Customer support"
        };
    }

    /// <summary>What the job is, in a line, for the staff page.</summary>
    public static string What(this StaffJob job)
    {
        return job switch
        {
            StaffJob.HubManager => "Runs one hub: every desk there, riders' cash shortfalls and the hub's own staff logins",
            StaffJob.Receiving => "Unloads vans and trucks, scans bags and parcels in, weighs them and flags problems",
            StaffJob.LineHaul => "Bags parcels for the hubs they go to and seals each bag onto the truck",
            StaffJob.DeliveryDesk => "Sends riders on today's pickups, gives them parcels and prints run sheets",
            StaffJob.ReturnsDesk => "Return lists, handing returns back and sending parcels back to merchants",
            StaffJob.CashDesk => "Counts each rider's cash, closes their run and pays the hub's cash into the bank",
            StaffJob.Relief => "Covers any desk at any hub",
            StaffJob.OperationsManager => "Every hub's work, the dashboard, stuck parcels, riders and requests",
            StaffJob.Accounts => "Payouts, adjustments, online payments, hubs' bank deposits and reports",
            StaffJob.MerchantSupport => "Sign-ups, merchants' accounts and their requests",
            _ => "Looks parcels up for the hotline; changes nothing"
        };
    }

    public static string DisplayName(this StaffPermissions permission)
    {
        return permission switch
        {
            StaffPermissions.Receive => "Receive and weigh",
            StaffPermissions.Dispatch => "Bag for other hubs",
            StaffPermissions.DeliveryDesk => "Pickups and riders",
            StaffPermissions.Returns => "Returns",
            StaffPermissions.CashDesk => "Close runs and deposit cash",
            StaffPermissions.CashShortfalls => "Accept cash shortfalls",
            StaffPermissions.Operations => "Operations",
            StaffPermissions.Merchants => "Merchants",
            StaffPermissions.Finance => "Money and reports",
            StaffPermissions.Setup => "Setup and settings",
            StaffPermissions.Staff => "Staff logins",
            _ => permission.ToString()
        };
    }

    /// <summary>Each single permission, in the order the staff page lists them.</summary>
    public static readonly IReadOnlyList<StaffPermissions> Each =
    [
        StaffPermissions.Receive,
        StaffPermissions.Dispatch,
        StaffPermissions.DeliveryDesk,
        StaffPermissions.Returns,
        StaffPermissions.CashDesk,
        StaffPermissions.CashShortfalls,
        StaffPermissions.Operations,
        StaffPermissions.Merchants,
        StaffPermissions.Finance,
        StaffPermissions.Setup,
        StaffPermissions.Staff
    ];
}
