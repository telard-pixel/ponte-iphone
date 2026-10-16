using PonteIPhone.Core.Model;

namespace PonteIPhone.Tests;

/// <summary>Perché l'app non vede l'iPhone: il verdetto deve indicare il livello giusto (cavo, driver, servizio, dati).</summary>
public sealed class NoDeviceTests
{
    private static readonly AppleUsbDevice Phone = new(@"USB\VID_05AC&PID_12A8\00008101000A1B2C3D4E5F60", "Apple Mobile Device USB Composite Device", 0);
    private static readonly AppleUsbDevice PhoneInterface = new(@"USB\VID_05AC&PID_12A8&MI_00\7&1A2B3C4D&0&0000", "Apple Mobile Device USB Driver", 0);

    [Fact]
    public void PhoneOnCableButNotInAppleServiceIsDataBlocked()
    {
        var d = NoDeviceDiagnosis.Decide(true, null, 0, 0, [Phone, PhoneInterface]);
        Assert.Equal(NoDeviceVerdict.DataBlocked, d.Verdict);
        Assert.Contains("Consenti", d.Text);
        Assert.Contains("1 interfacce", d.Summary);
    }

    [Fact]
    public void DriverProblemWinsOverEverythingElse()
    {
        var d = NoDeviceDiagnosis.Decide(false, "non raggiungibile", 0, 0, [Phone with { Problem = 28 }]);
        Assert.Equal(NoDeviceVerdict.DriverProblem, d.Verdict);
        Assert.Contains("problema 28", d.Summary);
    }

    [Fact]
    public void ProblemOnAnInterfaceAloneDoesNotCount()
    {
        // Le interfacce secondarie possono non avere driver (es. quella di rete): conta il dispositivo intero.
        var d = NoDeviceDiagnosis.Decide(true, null, 0, 0, [Phone, PhoneInterface with { Problem = 28 }]);
        Assert.Equal(NoDeviceVerdict.DataBlocked, d.Verdict);
    }

    [Fact]
    public void ServiceDownIsSaidPlainly()
    {
        var d = NoDeviceDiagnosis.Decide(false, "Servizio Apple non raggiungibile", 0, 0, []);
        Assert.Equal(NoDeviceVerdict.ServiceDown, d.Verdict);
        Assert.Contains("non risponde", d.Summary);
    }

    [Fact]
    public void WifiOnlyAndEmptyCable()
    {
        Assert.Equal(NoDeviceVerdict.OnlyWifi, NoDeviceDiagnosis.Decide(true, null, 0, 1, []).Verdict);
        Assert.Equal(NoDeviceVerdict.NothingOnCable, NoDeviceDiagnosis.Decide(true, null, 0, 0, []).Verdict);
    }
}
