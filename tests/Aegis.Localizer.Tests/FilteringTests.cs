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

using Aegis.Localizer.Filtering;
using Xunit;

namespace Aegis.Localizer.Tests;

public class NoiseFilterTests
{
    [Theory]
    [InlineData("https://api.example.com/v1")]
    [InlineData("C:\\Temp\\file.txt")]
    [InlineData("/usr/local/bin")]
    [InlineData("application/json")]
    [InlineData("yyyy-MM-dd HH:mm")]
    [InlineData("#2266EE")]
    [InlineData("SELECT Id FROM Cars")]
    [InlineData("MAX_RETRY_COUNT")]
    [InlineData("user.profile.name")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("px")]
    [InlineData("{0}")]
    public void DropsMachineStrings(string value) => Assert.True(NoiseFilter.IsNoise(value));

    [Theory]
    [InlineData("Save changes")]
    [InlineData("Your bookings")]
    [InlineData("Email address is required.")]
    [InlineData("Booking for {0} confirmed")]
    [InlineData("Settings")]
    [InlineData("Waiting for approval")]
    public void KeepsUserCopy(string value) => Assert.False(NoiseFilter.IsNoise(value));
}

public class KeyNamerTests
{
    [Fact]
    public void SameTextAlwaysGetsSameKey()
    {
        var namer = new KeyNamer();

        Assert.Equal("SaveButton", namer.Resolve("Save", "SaveButton"));
        Assert.Equal("SaveButton", namer.Resolve("Save", "SomethingElse"));
    }

    [Fact]
    public void DifferentTextNeverSharesAKey()
    {
        var namer = new KeyNamer();

        var first = namer.Resolve("Save", "Action");
        var second = namer.Resolve("Delete", "Action");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SanitizesIllegalIdentifiers()
    {
        var namer = new KeyNamer();

        Assert.Equal("SaveChanges", namer.Resolve("Save changes", "save-changes"));
        Assert.Equal("N123Go", namer.Resolve("Go", "123 go"));
    }

    [Fact]
    public void FallsBackToTheCopyWhenNoKeyIsProposed()
    {
        var namer = new KeyNamer();

        Assert.Equal("SaveYourChanges", namer.Resolve("Save your changes", null));
    }

    /// <summary>
    /// The regression that broke an already-rewritten project: a new string must never be handed a
    /// key that existing code already resolves to different copy.
    /// </summary>
    [Fact]
    public void NeverStealsAKeyThatIsAlreadyOnDisk()
    {
        var existing = new Dictionary<string, string> { ["Save"] = "SaveButton" };
        var namer = new KeyNamer(existing);

        Assert.Equal("SaveButton", namer.Resolve("Save", "SaveButton"));
        Assert.NotEqual("SaveButton", namer.Resolve("Store", "SaveButton"));
    }
}
