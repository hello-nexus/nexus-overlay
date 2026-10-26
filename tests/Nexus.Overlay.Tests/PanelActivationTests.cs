using System;
using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelActivationTests
{
    private static readonly IntPtr Self = new(100);
    private static readonly IntPtr App = new(200);
    private static readonly IntPtr Other = new(300);

    [Fact]
    public void StartsNotActivatable()
        => Assert.False(new PanelActivation().Activatable);

    [Fact]
    public void SettingTheSameValueIsANoop()
    {
        var a = new PanelActivation();
        Assert.False(a.Set(false, App, Self));
        Assert.True(a.Set(true, App, Self));
        Assert.False(a.Set(true, Other, Self));
        Assert.Equal(App, a.TakeRestoreTarget());
    }

    [Fact]
    public void ForegroundThatIsTheKioskItselfIsNotARestoreTarget()
    {
        var a = new PanelActivation();
        a.Set(true, Self, Self);
        Assert.Equal(IntPtr.Zero, a.TakeRestoreTarget());
    }

    [Fact]
    public void ActivationReportsTheDeactivatedWindowAsTheRestoreTarget()
    {
        var a = new PanelActivation();
        a.Set(true, App, Self);
        a.OnActivated(Other, Self);
        Assert.Equal(Other, a.TakeRestoreTarget());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ActivationFromNothingOrSelfKeepsTheEarlierTarget(int deactivated)
    {
        var a = new PanelActivation();
        a.Set(true, App, Self);
        a.OnActivated(new IntPtr(deactivated), Self);
        Assert.Equal(App, a.TakeRestoreTarget());
    }

    [Fact]
    public void ActivationWhileNotActivatableRecordsNothing()
    {
        var a = new PanelActivation();
        a.OnActivated(Other, Self);
        a.Set(true, IntPtr.Zero, Self);
        Assert.Equal(IntPtr.Zero, a.TakeRestoreTarget());
    }

    [Fact]
    public void RestoreTargetIsTakenOnce()
    {
        var a = new PanelActivation();
        a.Set(true, App, Self);
        Assert.Equal(App, a.TakeRestoreTarget());
        Assert.Equal(IntPtr.Zero, a.TakeRestoreTarget());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    public void OnlyBrowserAndRendererExitAreFatal(int kind, bool fatal)
        => Assert.Equal(fatal, PanelActivation.IsFatalProcessFailure(kind));

    [Theory]
    [InlineData("{\"type\":\"nexus.panel.activatable\",\"value\":true}", true)]
    [InlineData("{\"type\":\"nexus.panel.activatable\",\"value\":false}", false)]
    public void ActivatableMessageCarriesTheBoolean(string json, bool expected)
    {
        var m = PanelHostMessage.Parse(json, 100, 100);
        Assert.Equal(PanelMessageKind.Activatable, m.Kind);
        Assert.Equal(expected, m.RaiseValue);
    }

    [Theory]
    [InlineData("{\"type\":\"nexus.panel.activatable\"}")]
    [InlineData("{\"type\":\"nexus.panel.activatable\",\"value\":\"true\"}")]
    [InlineData("{\"type\":\"nexus.panel.activatable\",\"value\":1}")]
    public void ActivatableWithoutABooleanIsIgnored(string json)
        => Assert.Equal(PanelMessageKind.Ignored, PanelHostMessage.Parse(json, 100, 100).Kind);

    [Fact]
    public void WidgetEditingEndingKeepsAPageRequestActivatable()
    {
        var a = new PanelActivation();
        Assert.True(a.Set(true, App, Self));
        Assert.False(a.SetWidgetEditing(true, App, Self));
        Assert.False(a.SetWidgetEditing(false, App, Self));
        Assert.True(a.Activatable);
        Assert.Equal(App, a.TakeRestoreTarget());
    }

    [Fact]
    public void PageRequestEndingKeepsWidgetEditingActivatable()
    {
        var a = new PanelActivation();
        Assert.True(a.SetWidgetEditing(true, App, Self));
        Assert.False(a.Set(true, Other, Self));
        Assert.False(a.Set(false, Other, Self));
        Assert.True(a.Activatable);
        Assert.True(a.SetWidgetEditing(false, Other, Self));
        Assert.False(a.Activatable);
    }
}
