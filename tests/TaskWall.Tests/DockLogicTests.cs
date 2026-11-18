using TaskWall;
using Xunit;

namespace TaskWall.Tests;

// Ported from Rustyfiles' own tests (crates/rf-win/src/dock/spring.rs and magnify.rs, commit 243f22c): the same inputs, the same
// expectations, so the C# port moves exactly like the original.

public class SpringTests
{
    [Fact]
    public void ASpringWithoutBounceArrivesWithoutSwingingPastAndIsNearlyThereAfterItsDuration()
    {
        var s = Spring.Smooth(240);
        double last = 1;
        for (int t = 0; t <= 1000; t++)
        {
            var (e, _) = s.At(1, 0, t);
            Assert.True(e <= last + 1e-6 && e >= 0, $"at {t}: {e}");
            last = e;
        }
        Assert.Equal((1d, 0d), s.At(1, 0, 0));
        Assert.True(Math.Abs(s.At(1, 0, 64).E - 0.5) < 0.02);
        Assert.True(s.At(1, 0, 240).E < 0.015);
        Assert.True(s.At(1, 0, 360).E < 0.001);
        Assert.True(s.At(1, 0, 24).E > 0.8);
    }

    [Fact]
    public void TheClosedFormIsTheSameWhateverTheStepsAre()
    {
        foreach (var bounce in new[] { 0.0, 0.15, 0.3 })
        {
            var s = Spring.Create(240, bounce);
            var whole = s.At(1, 0, 300);
            double e = 1, v = 0;
            for (int i = 0; i < 300; i++) (e, v) = s.At(e, v, 1);
            Assert.True(Math.Abs(e - whole.E) < 2e-4 && Math.Abs(v - whole.V) < 2e-5);
        }
    }

    [Fact]
    public void ASpringTurnedAroundInMidFlightKeepsItsSpeedAndDoesNotJump()
    {
        var s = Spring.Smooth(240);
        var (e, v) = s.At(-1, 0, 60);
        double x = 1 + e;
        Assert.True(v > 0 && x > 0.3 && x < 0.7);
        var (x1, v1) = s.At(x, v, 1);
        Assert.True(Math.Abs(x1 - x) < 0.02);
        Assert.True(v1 > 0 && v1 < v);
        double peak = Enumerable.Range(0, 200).Select(t => s.At(x, v, t).E).Aggregate(x, Math.Max);
        Assert.True(peak > x && peak < 1);
        Assert.True(Math.Abs(s.At(x, v, 600).E) < 1e-3);
        Assert.True(Enumerable.Range(0, 800).All(t => s.At(x, v, t).E > -1e-6));
    }

    [Fact]
    public void BounceSwingsPastByWhatTheDampingSays()
    {
        foreach (var (bounce, past) in new[] { (0.15, 0.0063), (0.3, 0.046) })
        {
            var s = Spring.Create(300, bounce);
            double lowest = Enumerable.Range(0, 2000).Select(t => s.At(1, 0, t).E).Aggregate(1.0, Math.Min);
            Assert.True(Math.Abs(-lowest - past) < 0.003, $"{bounce}: {lowest}");
        }
        Assert.Equal(Spring.Create(300, Spring.MaxBounce), Spring.Create(300, 5));
        Assert.Equal(Spring.Smooth(300), Spring.Create(300, -1));
    }

    [Fact]
    public void TheSettleTimeIsNeverTooEarly()
    {
        foreach (var bounce in new[] { 0.0, 0.15, 0.4 })
        {
            var s = Spring.Create(240, bounce);
            foreach (var (e0, v0) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.5, 0.004), (0.2, -0.01), (0.0, 0.01), (-0.7, 0.008) })
            {
                double t = s.SettleMs(e0, v0, 0.001);
                Assert.True(double.IsFinite(t) && t >= 0 && t < 2000);
                foreach (var later in new[] { 0.0, 1, 10, 50, 200, 1000 })
                    Assert.True(Math.Abs(s.At(e0, v0, t + later).E) <= 0.001 + 1e-6);
            }
            Assert.Equal(0, s.SettleMs(0, 0, 0.001));
        }
        Assert.True(Spring.Smooth(240).SettleMs(1, 0, 0.001) < 400);
    }

    [Fact]
    public void TheCurveGoesFrom0ToExactly1()
    {
        Assert.Equal(0, Spring.Curve(0));
        Assert.Equal(1, Spring.Curve(1));
        double last = 0;
        for (int i = 0; i <= 1000; i++)
        {
            double u = Spring.Curve(i / 1000.0);
            Assert.True(u >= last - 1e-6 && u is >= 0 and <= 1);
            last = u;
        }
        Assert.True(Spring.Curve(0.5) > 0.88 && Spring.Curve(0.25) > 0.55 && Spring.Curve(0.05) < 0.07);
    }
}

public class PresenceTests
{
    [Fact]
    public void PresenceIsASpringThatArrivesAndNeverJumps()
    {
        var p = new Presence(Magnify.FadeMs);
        Assert.True(p.AtRest(0) && !p.IsIn);
        p.Enter(1000);
        Assert.Equal(0, p.Value(1000));
        Assert.True(p.Value(1016) < 0.1);
        Assert.True(Math.Abs(p.Value(1064) - 0.5) < 0.03);
        Assert.True(p.Value(1150) > 0.88 && p.Value(1240) > 0.98);
        Assert.True(!p.Settled(1240) && p.Settled(1400));
        Assert.Equal(1, p.Value(1400));
        p.Leave(2000);
        double before = p.Value(2059), mid = p.Value(2060);
        Assert.True(mid < before && mid > 0.3 && mid < 0.7);
        p.Enter(2060);
        Assert.True(Math.Abs(p.Value(2060) - mid) < 1e-6, "no jump when the direction changes");
        double after = p.Value(2061);
        Assert.True(after < mid, "it slows before it turns");
        Assert.True(p.Settled(2600) && p.Value(2600) == 1);
    }

    [Fact]
    public void WithAnimationsOffTheBellJumps()
    {
        var p = new Presence(0);
        p.Enter(10);
        Assert.Equal(1, p.Value(10));
        p.Leave(20);
        Assert.True(p.AtRest(20));
    }
}

public class MagnifyTests
{
    static (List<Slot> Slots, List<(double X, double W)> RestRow, double Width) Icons(int n)
    {
        var slots = Enumerable.Repeat(Slot.Icon, n).ToList();
        var sizes = Enumerable.Repeat(Magnify.IconRest, n).ToList();
        double w = Magnify.NaturalWidth(slots, sizes);
        return (slots, Magnify.Fit(slots, sizes, w), w);
    }

    [Fact]
    public void TheBellIsOneAtThePointerZeroAtTheReach()
    {
        Assert.Equal(1, Magnify.Bell(0));
        Assert.True(Math.Abs(Magnify.Bell(Magnify.Reach)) < 1e-9 && Magnify.Bell(Magnify.Reach * 3) == 0);
        Assert.Equal(Magnify.Bell(-40), Magnify.Bell(40));
        double slot = Magnify.IconRest + Magnify.IconGap;
        Assert.InRange(Magnify.Bell(slot), 0.5, 0.8);
        Assert.True(Magnify.Bell(2 * slot) < 0.15);
        Assert.Equal(135, Magnify.Reach);
    }

    [Fact]
    public void TheItemUnderThePointerIsTheBiggest()
    {
        var (slots, rest, w) = Icons(9);
        double centre = rest[4].X + rest[4].W / 2;
        var f = Magnify.Layout(slots, rest, w, centre, 1, Strength.Full, w + 200);
        var sizes = f.Spans.Select(s => s.W).ToList();
        Assert.True(Math.Abs(sizes[4] - 44 * 1.55) < 1e-3);
        Assert.True(sizes[3] < sizes[4] && sizes[3] > sizes[2] && sizes[2] > sizes[1] && sizes[1] >= 44);
        Assert.Equal(44, sizes[0]);
    }

    [Fact]
    public void ASymmetricDockWithThePointerInTheMiddleWidensAroundItsCentre()
    {
        var (slots, rest, w) = Icons(9);
        var f = Magnify.Layout(slots, rest, w, w / 2, 1, Strength.Full, w + 200);
        for (int i = 0; i < 4; i++) Assert.True(Math.Abs(f.Spans[i].W - f.Spans[8 - i].W) < 1e-3);
        Assert.True(Math.Abs(f.Left + f.Width / 2 - w / 2) < 1e-3);
    }

    [Fact]
    public void NothingOverlapsAndTheRowStaysInItsRoom()
    {
        var (slots, rest, w) = Icons(12);
        for (double p = -80; p < w + 80; p += 7.3)
            foreach (var room in new[] { 0.0, 30, 80, 400 })
            {
                var f = Magnify.Layout(slots, rest, w, p, 1, Strength.Full, w + room);
                Assert.True(f.Width >= w - 1e-3 && f.Width <= w + room + 1e-3);
                for (int i = 1; i < f.Spans.Count; i++)
                    Assert.True(f.Spans[i].X - (f.Spans[i - 1].X + f.Spans[i - 1].W) >= Magnify.MinGap - 1e-3);
            }
    }

    [Fact]
    public void ThePointerMovingALittleChangesEverythingALittle()
    {
        var slots = new List<Slot> { Slot.Icon, Slot.Icon, Slot.Icon, Slot.Separator, Slot.Icon, Slot.Icon };
        var sizes = Enumerable.Repeat(Magnify.IconRest, 5).ToList();
        double w = Magnify.NaturalWidth(slots, sizes);
        var rest = Magnify.Fit(slots, sizes, w);
        var prev = Magnify.Layout(slots, rest, w, -20, 1, Strength.Full, w + 120);
        for (double p = -19.5; p < w + 20; p += 0.5)
        {
            var f = Magnify.Layout(slots, rest, w, p, 1, Strength.Full, w + 120);
            Assert.True(Math.Abs(f.Width - prev.Width) <= 0.5 + 1e-3);
            for (int i = 0; i < f.Spans.Count; i++)
            {
                Assert.True(Math.Abs(f.Spans[i].W - prev.Spans[i].W) <= 0.2 + 1e-3);
                Assert.True(Math.Abs(f.Spans[i].X - prev.Spans[i].X) <= 0.75 + 1e-3);
            }
            prev = f;
        }
    }

    [Fact]
    public void AtRestTheFrameIsTheRestLayout()
    {
        var (slots, rest, w) = Icons(5);
        foreach (var f in new[] { Magnify.Layout(slots, rest, w, 50, 0, Strength.Full, w + 100), Magnify.Layout(slots, rest, w, 50, 1, Strength.Off, w + 100) })
        {
            Assert.Equal(0, f.Left);
            for (int i = 0; i < rest.Count; i++) Assert.True(Math.Abs(f.Spans[i].X - rest[i].X) < 1e-3 && Math.Abs(f.Spans[i].W - rest[i].W) < 1e-3);
        }
    }

    [Fact]
    public void ADockWithNoRoomGivesWayThroughTheGapsThenTheIcons()
    {
        var (slots, rest, w) = Icons(8);
        var f = Magnify.Layout(slots, rest, w, w / 2, 1, Strength.Full, w);
        Assert.Equal(w, f.Width);
        var (x, ww) = f.Spans[^1];
        Assert.True(x + ww <= w - Magnify.DockPadding + 1e-3);
    }
}

public class TaskRulesTests
{
    [Theory]
    [InlineData(false, 0u, true)]                // a plain top-level window
    [InlineData(true, 0u, false)]                // owned (a dialog)
    [InlineData(false, 0x80u, false)]            // a tool window
    [InlineData(true, 0x40000u, true)]           // owned, but asks for a button
    [InlineData(false, 0x08000000u, false)]      // never activates
    public void StyleRules(bool owner, uint ex, bool task) => Assert.Equal(task, TaskRules.StyleIsTask(owner, ex));

    [Fact]
    public void HelpersAndMinimisedWindows()
    {
        Assert.True(TaskRules.IsHelper("", 0, 0, minimized: false));
        Assert.False(TaskRules.IsHelper("", 0, 0, minimized: true));
        Assert.False(TaskRules.IsHelper("Notatnik", 0, 0, minimized: false));
    }

    [Fact]
    public void GroupsByAppIdThenProgram() =>
        Assert.Equal(new[] { "microsoft.windowsterminal_8wekyb3d8bbwe!app", @"c:\program files\blender\blender.exe", "pid:42" },
            new[] { TaskRules.Group("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", @"C:\x.exe", 1), TaskRules.Group(null, @"C:\Program Files\Blender\blender.exe", 2), TaskRules.Group(null, null, 42) });
}

public class DockRowTests
{
    [Fact]
    public void PinnedFirstThenALineThenTheOthers()
    {
        var (items, slots) = DockRow.Build(new[] { "a", "b" }, new[] { ("c", 1), ("a", 2), ("c", 1) });
        Assert.Equal(new[] { "a", "b", "c" }, items.Select(i => i.Key));
        Assert.Equal(new[] { Slot.Icon, Slot.Icon, Slot.Separator, Slot.Icon }, slots);
        Assert.Equal(2, items[0].Windows);
        Assert.Equal(0, items[1].Windows);
        Assert.Equal(2, items[2].Windows);
    }

    [Fact]
    public void NoLineWithoutBothKinds()
    {
        Assert.DoesNotContain(Slot.Separator, DockRow.Build(new[] { "a" }, new[] { ("a", 1) }).Slots);
        Assert.DoesNotContain(Slot.Separator, DockRow.Build(Array.Empty<string>(), new[] { ("a", 1) }).Slots);
    }

    [Fact]
    public void DropIndex()
    {
        var centres = new[] { 30.0, 90, 150 };
        Assert.Equal(0, DockRow.DropIndex(centres, 10));
        Assert.Equal(2, DockRow.DropIndex(centres, 100));
        Assert.Equal(3, DockRow.DropIndex(centres, 400));
    }
}
