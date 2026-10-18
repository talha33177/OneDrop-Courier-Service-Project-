using System.Net;
using System.Text;
using Application.Abstractions;

namespace Application.Notifications.SendEmails;

/// <summary>How an email reads at a glance: the colour of its label and of its note.</summary>
public enum EmailTone
{
    Neutral,
    Good,
    Warning,
    Bad
}

/// <summary>One line of an email's table of details; a strong line is a total.</summary>
public sealed record EmailRow(string Label, string Value, bool Strong = false);

/// <summary>
/// What one email says, in plain words: every value is text and is encoded when drawn, so a merchant's name or a
/// reason typed by the courier can never break the email or inject anything into it.
/// </summary>
public sealed record EmailContent
{
    public required string Subject { get; init; }

    /// <summary>The one line an inbox shows beside the subject.</summary>
    public required string Preview { get; init; }

    /// <summary>A short label above the title ("Payout sent").</summary>
    public required string Label { get; init; }

    public EmailTone Tone { get; init; } = EmailTone.Neutral;

    public required string Title { get; init; }

    /// <summary>"Hello Dhaka Fashion,"; null for none.</summary>
    public string? Greeting { get; init; }

    public IReadOnlyList<string> Paragraphs { get; init; } = [];

    /// <summary>A large figure (an amount, a code) with what it is.</summary>
    public (string Value, string Caption)? Highlight { get; init; }

    public IReadOnlyList<EmailRow> Rows { get; init; } = [];

    /// <summary>A boxed note, such as the courier's reason, with its own heading.</summary>
    public (string Heading, string Text)? Note { get; init; }

    public (string Text, string Url)? Button { get; init; }

    /// <summary>Small print under the button ("The link works for one day").</summary>
    public string? Small { get; init; }
}

/// <summary>The courier the email comes from, for its header and footer.</summary>
public sealed record EmailBrand(string CourierName, string Hotline, string SiteUrl);

/// <summary>
/// Draws every email the courier sends in one design: the courier's mark, a coloured label, the title, the message,
/// a highlighted figure, a table of details, a note, one button and a footer with the hotline. Tables and inline
/// styles only, because that is what Gmail, Outlook and phone mail apps all draw the same way; a plain-text copy is
/// written beside it for readers that show no HTML.
/// </summary>
public static class EmailLayout
{
    private const string Ink = "#141820";
    private const string Body = "#3b4350";
    private const string Muted = "#6b7482";
    private const string Line = "#e6eaf0";
    private const string Paper = "#f3f6f9";
    private const string Sky = "#7cc6f2";
    private const string SkySoft = "#eaf6fd";
    private const string SkyText = "#0b6a9e";

    public static EmailMessage Render(EmailContent content, EmailBrand brand)
    {
        return new EmailMessage("", null, content.Subject, Html(content, brand), Text(content, brand));
    }

    /// <summary>An amount as people read it: ৳1,250 in taka, the code after it in any other currency.</summary>
    public static string Money(decimal amount, string currency)
    {
        var figure = amount.ToString(decimal.Round(amount) == amount ? "N0" : "N2", System.Globalization.CultureInfo.InvariantCulture);

        return currency.Trim() == "BDT" ? $"৳{figure}" : $"{figure} {currency.Trim()}";
    }

    /// <summary>An amount with its sign: +৳200 credited, −৳60 charged.</summary>
    public static string Signed(decimal amount, string currency)
    {
        return (amount < 0 ? "−" : "+") + Money(Math.Abs(amount), currency);
    }

    private static string Html(EmailContent content, EmailBrand brand)
    {
        var (labelBack, labelText) = content.Tone switch
        {
            EmailTone.Good => ("#e3f6ea", "#17733d"),
            EmailTone.Warning => ("#fff3d6", "#8a5a00"),
            EmailTone.Bad => ("#fde7e7", "#a3261f"),
            _ => (SkySoft, SkyText)
        };
        var noteBorder = content.Tone switch
        {
            EmailTone.Good => "#2f9e5b",
            EmailTone.Warning => "#d99a00",
            EmailTone.Bad => "#d33b32",
            _ => Sky
        };
        var initial = E(brand.CourierName.Trim() is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "C");
        var html = new StringBuilder();

        html.Append($"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="light">
            <meta name="supported-color-schemes" content="light">
            <title>{E(content.Subject)}</title>
            </head>
            <body style="margin:0;padding:0;background:{Paper};-webkit-text-size-adjust:100%;">
            <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{E(content.Preview)}&#8199;&#847;&#8199;&#847;&#8199;&#847;&#8199;&#847;&#8199;&#847;</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Paper};">
            <tr><td align="center" style="padding:32px 12px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;">
            <tr><td style="padding:0 4px 18px 4px;">
              <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
                <td width="36" height="36" align="center" valign="middle" style="width:36px;height:36px;background:{Sky};border-radius:10px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:18px;font-weight:800;color:{Ink};">{initial}</td>
                <td style="padding-left:10px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:17px;font-weight:700;color:{Ink};">{E(brand.CourierName)}</td>
              </tr></table>
            </td></tr>
            <tr><td style="background:#ffffff;border:1px solid {Line};border-radius:18px;overflow:hidden;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
              <tr><td style="height:5px;background:{Sky};font-size:0;line-height:0;">&nbsp;</td></tr>
              <tr><td style="padding:30px 32px 8px 32px;font-family:Segoe UI,Helvetica,Arial,sans-serif;">
                <span style="display:inline-block;padding:5px 11px;border-radius:999px;background:{labelBack};color:{labelText};font-size:12px;font-weight:700;letter-spacing:.3px;text-transform:uppercase;">{E(content.Label)}</span>
                <h1 style="margin:16px 0 0 0;font-size:24px;line-height:1.3;font-weight:800;color:{Ink};">{E(content.Title)}</h1>
              </td></tr>
            """);

        var texts = new List<string>();
        if (content.Greeting is not null)
        {
            texts.Add(content.Greeting);
        }

        texts.AddRange(content.Paragraphs);
        if (texts.Count > 0)
        {
            html.Append($"""<tr><td style="padding:10px 32px 4px 32px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.65;color:{Body};">""");
            foreach (var paragraph in texts)
            {
                html.Append($"""<p style="margin:0 0 12px 0;">{E(paragraph)}</p>""");
            }

            html.Append("</td></tr>");
        }

        if (content.Highlight is { } highlight)
        {
            html.Append($"""
                <tr><td style="padding:8px 32px 8px 32px;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{SkySoft};border-radius:14px;">
                  <tr><td align="center" style="padding:22px 16px;font-family:Segoe UI,Helvetica,Arial,sans-serif;">
                    <div style="font-size:34px;line-height:1.1;font-weight:800;color:{Ink};">{E(highlight.Value)}</div>
                    <div style="margin-top:6px;font-size:13px;color:{Muted};">{E(highlight.Caption)}</div>
                  </td></tr></table>
                </td></tr>
                """);
        }

        if (content.Rows.Count > 0)
        {
            html.Append($"""<tr><td style="padding:12px 32px 4px 32px;"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:14px;">""");
            foreach (var row in content.Rows)
            {
                var top = row.Strong ? $"border-top:2px solid {Ink};" : $"border-top:1px solid {Line};";
                var weight = row.Strong ? "800" : "600";
                html.Append($"""
                    <tr>
                      <td style="{top}padding:11px 0;color:{(row.Strong ? Ink : Muted)};font-weight:{(row.Strong ? "700" : "400")};">{E(row.Label)}</td>
                      <td align="right" style="{top}padding:11px 0;color:{Ink};font-weight:{weight};">{E(row.Value)}</td>
                    </tr>
                    """);
            }

            html.Append("</table></td></tr>");
        }

        if (content.Note is { } note)
        {
            html.Append($"""
                <tr><td style="padding:14px 32px 4px 32px;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Paper};border-left:4px solid {noteBorder};border-radius:10px;">
                  <tr><td style="padding:14px 16px;font-family:Segoe UI,Helvetica,Arial,sans-serif;">
                    <div style="font-size:12px;font-weight:700;letter-spacing:.3px;text-transform:uppercase;color:{Muted};">{E(note.Heading)}</div>
                    <div style="margin-top:5px;font-size:15px;line-height:1.55;color:{Ink};">{E(note.Text)}</div>
                  </td></tr></table>
                </td></tr>
                """);
        }

        if (content.Button is { } button)
        {
            html.Append($"""
                <tr><td style="padding:22px 32px 6px 32px;">
                  <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
                    <td align="center" bgcolor="{Ink}" style="border-radius:12px;background:{Ink};">
                      <a href="{E(button.Url)}" target="_blank" style="display:inline-block;padding:14px 26px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:15px;font-weight:700;color:#ffffff;text-decoration:none;border-radius:12px;">{E(button.Text)} &rarr;</a>
                    </td>
                  </tr></table>
                  <p style="margin:12px 0 0 0;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:12px;line-height:1.5;color:{Muted};">Or open this link: <a href="{E(button.Url)}" style="color:{SkyText};word-break:break-all;">{E(button.Url)}</a></p>
                </td></tr>
                """);
        }

        if (content.Small is not null)
        {
            html.Append($"""<tr><td style="padding:10px 32px 0 32px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:13px;line-height:1.55;color:{Muted};">{E(content.Small)}</td></tr>""");
        }

        html.Append($"""
              <tr><td style="padding:26px 32px 30px 32px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.6;color:{Body};">
                Thank you,<br><strong style="color:{Ink};">The {E(brand.CourierName)} team</strong>
              </td></tr>
              </table>
            </td></tr>
            <tr><td align="center" style="padding:20px 16px 0 16px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:12px;line-height:1.6;color:{Muted};">
              Questions? Call our hotline <a href="tel:{E(brand.Hotline.Replace(" ", "", StringComparison.Ordinal))}" style="color:{SkyText};text-decoration:none;font-weight:700;">{E(brand.Hotline)}</a><br>
              <a href="{E(brand.SiteUrl)}" style="color:{Muted};">{E(brand.CourierName)}</a> &middot; This is an automatic email; replies are not read.
            </td></tr>
            </table>
            </td></tr>
            </table>
            </body>
            </html>
            """);

        return html.ToString();
    }

    private static string Text(EmailContent content, EmailBrand brand)
    {
        var text = new StringBuilder();
        text.Append(content.Title).Append("\r\n\r\n");
        if (content.Greeting is not null)
        {
            text.Append(content.Greeting).Append("\r\n\r\n");
        }

        foreach (var paragraph in content.Paragraphs)
        {
            text.Append(paragraph).Append("\r\n\r\n");
        }

        if (content.Highlight is { } highlight)
        {
            text.Append($"{highlight.Caption}: {highlight.Value}\r\n\r\n");
        }

        foreach (var row in content.Rows)
        {
            text.Append($"{row.Label}: {row.Value}\r\n");
        }

        if (content.Rows.Count > 0)
        {
            text.Append("\r\n");
        }

        if (content.Note is { } note)
        {
            text.Append($"{note.Heading}: {note.Text}\r\n\r\n");
        }

        if (content.Button is { } button)
        {
            text.Append($"{button.Text}: {button.Url}\r\n\r\n");
        }

        if (content.Small is not null)
        {
            text.Append(content.Small).Append("\r\n\r\n");
        }

        text.Append($"Thank you,\r\nThe {brand.CourierName} team\r\nHotline: {brand.Hotline}\r\n");

        return text.ToString();
    }

    private static string E(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
