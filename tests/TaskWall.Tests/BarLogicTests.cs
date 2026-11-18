using TaskWall;
using Xunit;

namespace TaskWall.Tests;

public class ZOrderTests
{
    const long Board = 100, Cover = 7, Note = 5, Editor = 3;

    [Fact]
    public void WithoutRustyfilesTheBoardGoesToTheBottom() =>
        Assert.Equal((ZPlace.Bottom, 0L), ZOrder.Desktop(new long[] { Editor, Note, Board }, _ => false, Board));

    [Fact]
    public void TheBoardGoesRightAboveTheHighestCover() =>
        // Editor, Note, Cover, Cover2, Board: the board belongs under Note (right above the cover)
        Assert.Equal((ZPlace.After, Note), ZOrder.Desktop(new long[] { Editor, Note, Cover, 8, Board }, h => h is Cover or 8, Board));

    [Fact]
    public void AlreadyRightAboveTheCoverStays() =>
        Assert.Equal(ZPlace.Keep, ZOrder.Desktop(new long[] { Editor, Board, Cover }, h => h == Cover, Board).Place);

    [Fact]
    public void ACoverOnTopOfEverythingPutsTheBoardOnTop() =>
        Assert.Equal(ZPlace.Top, ZOrder.Desktop(new long[] { Cover, Editor, Board }, h => h == Cover, Board).Place);
}

public class BarGeometryTests
{
    static readonly PxRect Screen = new(0, 0, 2560, 1440);

    [Fact]
    public void TopStrip() => Assert.Equal(new PxRect(0, 0, 2560, 72), BarGeometry.Strip(Screen, top: true, 72));

    [Fact]
    public void BottomStrip() => Assert.Equal(new PxRect(0, 1368, 2560, 72), BarGeometry.Strip(Screen, top: false, 72));

    [Fact]
    public void SecondMonitorOnTheLeft() =>
        Assert.Equal(new PxRect(-1920, 1020, 1920, 60), BarGeometry.Strip(new PxRect(-1920, 0, 1920, 1080), top: false, 60));

    [Fact]
    public void AtTheBottomAboveTheWindowsTaskbar() =>
        // Windows moved the proposed strip up above its 48 px taskbar: the bar keeps its height from there
        Assert.Equal(new PxRect(0, 1320, 2560, 72), BarGeometry.AfterQuery(new PxRect(0, 1320, 2560, 72), top: false, 72));

    [Fact]
    public void ATallerGrantKeepsTheHeightFromTheEdge() =>
        // Windows may hand back the whole rest of the screen above the taskbar: the bar sits on its bottom edge
        Assert.Equal(new PxRect(0, 1320, 2560, 72), BarGeometry.AfterQuery(new PxRect(0, 0, 2560, 1392), top: false, 72));
}

public class SectorLayoutTests
{
    [Fact]
    public void TheMiddleIsCentredOnTheBar()
    {
        var (l, m, r) = SectorLayout.Place(1000, 200, 300, 100, 10);
        Assert.Equal((0d, 200d), l);
        Assert.Equal((350d, 300d), m);
        Assert.Equal((900d, 100d), r);
    }

    [Fact]
    public void AWideSideSectorPushesTheMiddleAside()
    {
        var (_, m, _) = SectorLayout.Place(1000, 500, 300, 100, 10);
        Assert.Equal((510d, 300d), m);
    }

    [Fact]
    public void WithoutRoomTheMiddleNarrows()
    {
        var (_, m, _) = SectorLayout.Place(800, 400, 600, 300, 10);
        Assert.Equal((410d, 80d), m);
    }

    [Fact]
    public void NothingFitsNothingOverlaps()
    {
        var (l, m, r) = SectorLayout.Place(300, 400, 100, 100, 10);
        Assert.Equal(300d, l.W);
        Assert.Equal(0d, r.W);
        Assert.Equal(0d, m.W);
    }
}

public class FullscreenTests
{
    static readonly PxRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void AGameCoveringTheScreen() => Assert.True(Fullscreen.Covers(new PxRect(0, 0, 1920, 1080), Screen, "UnityWndClass", ours: false));

    [Fact]
    public void AMaximisedWindowIsNotFullScreen() => Assert.False(Fullscreen.Covers(new PxRect(0, 0, 1920, 1032), Screen, "Chrome_WidgetWin_1", ours: false));

    [Fact]
    public void TheDesktopIsNotFullScreen() => Assert.False(Fullscreen.Covers(Screen, Screen, "WorkerW", ours: false));

    [Fact]
    public void OurOwnWindowIsNotFullScreen() => Assert.False(Fullscreen.Covers(Screen, Screen, "HwndWrapper", ours: true));

    [Theory]
    [InlineData(1, false)] // QUNS_NOT_PRESENT (locked)
    [InlineData(2, true)]  // QUNS_BUSY
    [InlineData(3, true)]  // QUNS_RUNNING_D3D_FULL_SCREEN
    [InlineData(4, true)]  // QUNS_PRESENTATION_MODE
    [InlineData(5, false)] // QUNS_ACCEPTS_NOTIFICATIONS
    public void NotificationState(int state, bool full) => Assert.Equal(full, Fullscreen.FromNotificationState(state));
}

public class CrashLoopTests
{
    static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0);

    [Fact]
    public void ThreeCrashesInTenMinutes() =>
        Assert.True(CrashLoop.TooMany(new[] { Now.AddMinutes(-9), Now.AddMinutes(-4), Now.AddSeconds(-5) }, Now));

    [Fact]
    public void OldCrashesDontCount() =>
        Assert.False(CrashLoop.TooMany(new[] { Now.AddMinutes(-11), Now.AddMinutes(-4), Now.AddSeconds(-5) }, Now));

    [Fact]
    public void TwoCrashesAreNotALoop() =>
        Assert.False(CrashLoop.TooMany(new[] { Now.AddMinutes(-1), Now }, Now));
}

public class BarMetricsTests
{
    [Fact]
    public void SizesFitTheirIcons()
    {
        foreach (var size in new[] { "large", "medium", "small" })
        {
            Assert.True(BarMetrics.Icon(size) < BarMetrics.Height(size, dock: true), size);
            Assert.True(BarMetrics.Height(size, true) >= BarMetrics.Height(size, false));
        }
        Assert.True(BarMetrics.Height("small", false) > 22, "a little more than a load ring (22)");
        Assert.True(BarMetrics.Height("large", true) > BarMetrics.Height("medium", true) && BarMetrics.Height("medium", true) > BarMetrics.Height("small", true));
    }

    [Fact]
    public void HeadroomHoldsTheGrownIcon()
    {
        Assert.Equal(0, BarMetrics.Headroom("large", dock: false, 1.55));
        Assert.Equal(0, BarMetrics.Headroom("large", dock: true, 1.0));
        foreach (var size in new[] { "large", "medium", "small" })
        {
            double icon = BarMetrics.Icon(size), bar = BarMetrics.Height(size, true);
            // the grown icon stands on the rest line half an icon from the middle: it reaches icon*1.55 from there
            double reach = bar / 2 - icon / 2 + icon * 1.55;
            Assert.True(reach <= bar + BarMetrics.Headroom(size, true, 1.55), size);
        }
    }

    [Fact]
    public void RadiusIsAtMostAPill()
    {
        Assert.Equal(15, BarMetrics.Radius(30));
        Assert.Equal(22, BarMetrics.Radius(56));
    }
}
