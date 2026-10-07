using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>A connection's vendor write APIs can be marked activated only while it may write, and losing write access
/// clears the mark, so switching back to Write starts from dry run.</summary>
public sealed class VendorWriteActivationTests
{
    private static SourceConnection Connection(SourceConnectionAccess access) =>
        new("eCW write", SourceSystemType.Healow, "https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD",
            new SourceAuthenticationConfiguration(AuthenticationType.SmartBackendServices, "client", "https://auth/token", [], null, null, "kid"),
            access: access);

    [Fact]
    public void A_read_only_connection_cannot_be_activated()
    {
        var act = () => Connection(SourceConnectionAccess.Read).SetVendorWriteApisActivated(true);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dropping_write_access_clears_the_activation()
    {
        var connection = Connection(SourceConnectionAccess.ReadWrite);
        connection.SetVendorWriteApisActivated(true);

        connection.SetAccess(SourceConnectionAccess.Read);
        connection.SetAccess(SourceConnectionAccess.Write);

        connection.VendorWriteApisActivated.Should().BeFalse();
    }

    [Fact]
    public void A_new_connection_is_not_activated()
    {
        Connection(SourceConnectionAccess.Write).VendorWriteApisActivated.Should().BeFalse();
    }
}
