using Rbm.Core.Agent;
using Rbm.Processes;
using Xunit;

namespace Rbm.Tests.Processes;

public class StartupApprovedTests
{
    [Fact]
    public void Decode_Absent_IsEnabled()
    {
        var s = StartupApproved.Decode(null);
        Assert.False(s.Present);
        Assert.True(s.Enabled);
        Assert.Null(s.DisabledAtUtc);
    }

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x07, false)]
    public void Decode_FlagByte(byte flag, bool enabled)
    {
        var data = new byte[12];
        data[0] = flag;
        var s = StartupApproved.Decode(data);
        Assert.True(s.Present);
        Assert.Equal(enabled, s.Enabled);
    }

    [Fact]
    public void Encode_Enabled_IsTwelveBytesWithZeroTimestamp()
    {
        var b = StartupApproved.Encode(true);
        Assert.Equal(12, b.Length);
        Assert.Equal(0x02, b[0]);
        Assert.All(b.Skip(1), x => Assert.Equal(0, x));
    }

    [Fact]
    public void Encode_Disabled_RoundTripsFileTime()
    {
        var when = new DateTime(2026, 11, 9, 12, 34, 56, DateTimeKind.Utc);
        var b = StartupApproved.Encode(false, when);
        Assert.Equal(0x03, b[0]);
        Assert.Equal(when.ToFileTimeUtc(), BitConverter.ToInt64(b, 4));
        var s = StartupApproved.Decode(b);
        Assert.False(s.Enabled);
        Assert.Equal(when, s.DisabledAtUtc);
    }

    [Fact]
    public void Encode_PreservesVariantBit()
    {
        var prev = new byte[12];
        prev[0] = 0x06;
        Assert.Equal(0x07, StartupApproved.Encode(false, DateTime.UtcNow, prev)[0]);
        prev[0] = 0x07;
        Assert.Equal(0x06, StartupApproved.Encode(true, null, prev)[0]);
    }

    [Fact]
    public void ValidateShape_RejectsUnknownAndNonToggleableLocations()
    {
        Assert.False(StartupManager.ValidateShape("Bogus", "x", elevated: true).Success);
        Assert.False(StartupManager.ValidateShape("3", "x", elevated: true).Success);
        Assert.False(StartupManager.ValidateShape(nameof(StartupLocation.HklmRunOnce), "x", elevated: true).Success);
        Assert.False(StartupManager.ValidateShape(nameof(StartupLocation.HklmPoliciesRun), "x", elevated: true).Success);
    }

    [Fact]
    public void ValidateShape_EnforcesElevationContext()
    {
        Assert.True(StartupManager.ValidateShape(nameof(StartupLocation.HklmRun), "Steam", elevated: true).Success);
        Assert.False(StartupManager.ValidateShape(nameof(StartupLocation.HkcuRun), "Steam", elevated: true).Success);
        var r = StartupManager.ValidateShape(nameof(StartupLocation.HklmRun32), "Steam", elevated: false);
        Assert.False(r.Success);
        Assert.Equal(ProcessErrorCodes.ElevationRequired, r.ErrorCode);
        Assert.True(StartupManager.ValidateShape(nameof(StartupLocation.HkcuRun), "Steam", elevated: false).Success);
    }

    [Theory]
    [InlineData(@"..\..\evil.lnk")]
    [InlineData(@"sub\file.lnk")]
    [InlineData("a/b.lnk")]
    [InlineData("")]
    [InlineData("bad\u0001name")]
    public void ValidateShape_RejectsBadFolderNames(string name) =>
        Assert.False(StartupManager.ValidateShape(nameof(StartupLocation.CommonStartupFolder), name, elevated: true).Success);

    [Fact]
    public void SetEnabledElevated_RejectsMissingEntry_WithoutWriting()
    {
        var r = StartupManager.SetEnabledElevated(new StartupToggleRequest(nameof(StartupLocation.HklmRun), "RBM-Inexistente-" + Guid.NewGuid(), false));
        Assert.False(r.Success);
        Assert.Equal(ProcessErrorCodes.NotFound, r.ErrorCode);
    }
}
