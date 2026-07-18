/*
 * Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
 * 34 Middletown Ave, Atlantic Highlands, NJ 07716
 *
 * THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
 * Aegis AO Soft LLC and Alexander Orlov.
 *
 * This code may be used, reproduced, modified, or distributed ONLY with the
 * prior written permission of Aegis AO Soft LLC / Alexander Orlov.
 *
 * Author: Alexander Orlov
 * Aegis AO Soft LLC
 */

namespace Demo.App;

/// <summary>Sample file used to exercise the extractor. Deliberately mixes copy and machine strings.</summary>
public sealed class BookingService
{
    private const string ConnectionName = "AegisDb";
    private static readonly string[] Statuses = ["pending", "approved", "declined"];

    public string Title => "Your bookings";

    public string Confirm(string carName, decimal total)
    {
        // Interpolated copy: should become a format string with two placeholders.
        return $"Booking for {carName} confirmed. Total: {total}";
    }

    public string Validate(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "Email address is required.";

        // A comparison, not copy - must be left alone.
        if (email == "test@example.com")
            return "This address cannot be used.";

        if (!email.Contains("@"))
            return "Please enter a valid email address.";

        return string.Empty;
    }

    public string BuildQuery() =>
        "SELECT Id, Name FROM dbo.Cars WHERE Deleted = 0";

    public string Endpoint => "https://api.example.com/v1/bookings";

    public string Format => "yyyy-MM-dd";

    public string Status(int code) => code switch
    {
        0 => "Waiting for approval",
        1 => "Approved",
        _ => "Unknown"
    };

    public void Report(Action<string> log)
    {
        // Developer-facing: excluded unless --include-diagnostics is passed.
        log("Booking pipeline finished");
    }

    public string Reference() => nameof(BookingService);

    public string Connection() => ConnectionName + "." + Statuses[0];
}
