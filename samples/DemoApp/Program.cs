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

using System.Globalization;
using System.Text;
using Demo.App;

// Smoke test for the rewritten sample: prints the same copy in both cultures so the .resx
// lookup and the generated accessor are exercised for real.
Console.OutputEncoding = Encoding.UTF8;

var service = new BookingService();

foreach (var name in new[] { "en", "ru", "es" })
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    Console.WriteLine($"[{name}]");
    Console.WriteLine("  " + service.Title);
    Console.WriteLine("  " + service.Confirm("Tesla Model 3", 249.50m));
    Console.WriteLine("  " + service.Validate(string.Empty));
    Console.WriteLine("  " + service.Status(0));
    Console.WriteLine();
}
