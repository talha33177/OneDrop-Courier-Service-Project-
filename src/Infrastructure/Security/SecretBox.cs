using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.SqlClient;
using Application.Abstractions;

namespace Infrastructure.Security;

/// <summary>Seals a courier's secrets with ASP.NET Data Protection, under a purpose of their own.</summary>
public class SecretBox(IDataProtectionProvider provider) : ISecretBox
{
    private readonly IDataProtector protector = provider.CreateProtector("Courier.Connections.Secrets");

    public string Seal(string secret)
    {
        return protector.Protect(secret);
    }

    public string? Open(string sealedSecret)
    {
        try
        {
            return protector.Unprotect(sealedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Keeps the Data Protection key ring in <c>Platform.DataProtectionKey</c>, so the sign-in cookies and the sealed
/// secrets survive a restart, a redeploy and a second server. Its own plain connection: the table is the platform's,
/// read before any courier is known, and the key ring must never pass through the tenant filters.
/// </summary>
public class SqlKeyRepository(string connectionString) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT K.Xml FROM Platform.DataProtectionKey K ORDER BY K.Id", connection);
        using var reader = command.ExecuteReader();
        var keys = new List<XElement>();
        while (reader.Read())
        {
            keys.Add(XElement.Parse(reader.GetString(0)));
        }

        return keys;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(
            "INSERT INTO Platform.DataProtectionKey (FriendlyName, Xml) VALUES (@FriendlyName, @Xml)",
            connection);
        command.Parameters.Add("@FriendlyName", System.Data.SqlDbType.NVarChar, 100).Value = friendlyName;
        command.Parameters.Add("@Xml", System.Data.SqlDbType.NVarChar, -1).Value = element.ToString(SaveOptions.DisableFormatting);
        command.ExecuteNonQuery();
    }
}
