using System.Reflection;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #17: «Поля текста» (резервные области номера и наименования листа) были заданы на листе А3,
/// после пересканирования листы А3 распознавались правильно, а листы других форматов — нет.
/// Штамп всегда прижат к правому нижнему углу листа, поэтому области должны находиться на одном и том же
/// расстоянии (в мм бумаги) от правого нижнего угла рамки на листе любого формата и ориентации.
///
/// До исправления масштаб считался по осям отдельно: sx = ширина рамки / ширина бумаги,
/// sy = высота рамки / высота бумаги. Универсальные прямоугольные рамки всегда получают бумагу
/// в альбомной ориентации (ширина = длинная сторона), поэтому на книжном А4 (210×297) sx = 0.707·M,
/// sy = 1.414·M, и области уезжали от штампа.
/// </summary>
public class FrameTextRegionTests
{
    private const string Geometry = "ZwcadBatchPlot.FrameTextRegionGeometry";

    // Области, указанные на штампе: мм бумаги от правого нижнего угла рамки (X влево, Y вверх).
    private static readonly double[] TitleMm = { -120d, 15d, -10d, 30d };
    private static readonly double[] NumberMm = { -20d, 0d, -10d, 8d };

    [Theory]
    // Лист, на котором задавали поля: А3 альбомный, М 1:100.
    [InlineData(420d, 297d, 100d, 420d, 297d)]
    // Универсальная рамка А4 книжная: бумага определяется альбомной 297×210 (как в RectangleFrameScanner).
    [InlineData(210d, 297d, 100d, 297d, 210d)]
    // Та же рамка А4 книжная с бумагой в правильной ориентации (рамки СПДС).
    [InlineData(210d, 297d, 100d, 210d, 297d)]
    // А3 книжный, бумага альбомная.
    [InlineData(297d, 420d, 100d, 420d, 297d)]
    // А1 альбомный, М 1:50.
    [InlineData(841d, 594d, 50d, 841d, 594d)]
    // А4х3 альбомный, М 1:1 (мм чертежа = мм бумаги).
    [InlineData(630d, 297d, 1d, 630d, 297d)]
    // А2 книжный, М 1:25, бумага альбомная.
    [InlineData(420d, 594d, 25d, 594d, 420d)]
    public void TextRegionKeepsItsDistanceFromTheRightBottomCornerOnEveryFormat(
        double sheetWidthMm, double sheetHeightMm, double drawingUnitsPerMm, double paperWidthMm, double paperHeightMm)
    {
        // Рамка где-то в модели, не в начале координат.
        var frame = Rect(5000d, -3000d, 5000d + sheetWidthMm * drawingUnitsPerMm, -3000d + sheetHeightMm * drawingUnitsPerMm);

        var title = ToWorld(Rect(TitleMm), frame, paperWidthMm, paperHeightMm);
        var number = ToWorld(Rect(NumberMm), frame, paperWidthMm, paperHeightMm);

        // Ожидаемое место: те же мм от правого нижнего угла (MaxX, MinY) в масштабе листа.
        AssertRect(ExpectedWorld(TitleMm, frame, drawingUnitsPerMm), title);
        AssertRect(ExpectedWorld(NumberMm, frame, drawingUnitsPerMm), number);
    }

    [Fact]
    public void RegionsConfiguredOnA3AreFoundOnAPortraitA4Sheet()
    {
        // Настройка «Поля текста» на А3 альбомном (М 1:100), как в issue #17.
        var a3 = Rect(0d, 0d, 42000d, 29700d);
        var titleOnA3 = ExpectedWorld(TitleMm, a3, 100d);
        var numberOnA3 = ExpectedWorld(NumberMm, a3, 100d);
        var profile = CreateProfile(a3, 420d, 297d, titleOnA3, numberOnA3);

        AssertRect(Rect(TitleMm), Get(profile, "TitleRegionMm"));
        AssertRect(Rect(NumberMm), Get(profile, "DrawingNumberRegionMm"));

        // Пересканирование: книжный А4 (М 1:100) рядом; универсальная рамка получает бумагу 297×210.
        var a4 = Rect(50000d, 0d, 71000d, 29700d);
        var titleOnA4 = ToWorld(Get(profile, "TitleRegionMm"), a4, 297d, 210d);
        var numberOnA4 = ToWorld(Get(profile, "DrawingNumberRegionMm"), a4, 297d, 210d);

        // Тот же штамп на А4: на том же расстоянии от правого нижнего угла.
        AssertRect(ExpectedWorld(TitleMm, a4, 100d), titleOnA4);
        AssertRect(ExpectedWorld(NumberMm, a4, 100d), numberOnA4);
    }

    [Fact]
    public void RegionsConfiguredOnAPortraitSheetAreTheSameAsOnA3()
    {
        // Настройка на книжном А4 с альбомной бумагой 297×210 даёт тот же профиль, что и на А3.
        var a4 = Rect(-1000d, 2000d, -1000d + 21000d, 2000d + 29700d);
        var profile = CreateProfile(
            a4, 297d, 210d,
            ExpectedWorld(TitleMm, a4, 100d),
            ExpectedWorld(NumberMm, a4, 100d));

        AssertRect(Rect(TitleMm), Get(profile, "TitleRegionMm"));
        AssertRect(Rect(NumberMm), Get(profile, "DrawingNumberRegionMm"));
    }

    [Theory]
    [InlineData(0d, 297d)]
    [InlineData(420d, 0d)]
    [InlineData(-420d, 297d)]
    public void UnknownPaperSizeIsRejected(double paperWidthMm, double paperHeightMm)
    {
        var args = new object?[] { Rect(0d, 0d, 420d, 297d), paperWidthMm, paperHeightMm, 0d };
        Assert.False((bool)Method(Geometry, "TryGetDrawingUnitsPerMm").Invoke(null, args)!);
    }

    [Fact]
    public void ScaleIsIndependentOfThePaperOrientation()
    {
        var portraitA4 = Rect(0d, 0d, 21000d, 29700d);
        Assert.Equal(100d, Scale(portraitA4, 297d, 210d), 9);
        Assert.Equal(100d, Scale(portraitA4, 210d, 297d), 9);
    }

    private static double Scale(object frame, double paperWidthMm, double paperHeightMm)
    {
        var args = new object?[] { frame, paperWidthMm, paperHeightMm, 0d };
        Assert.True((bool)Method(Geometry, "TryGetDrawingUnitsPerMm").Invoke(null, args)!);
        return (double)args[3]!;
    }

    private static object ToWorld(object paperRegion, object frame, double paperWidthMm, double paperHeightMm)
    {
        var args = new object?[] { paperRegion, frame, paperWidthMm, paperHeightMm, null };
        Assert.True((bool)Method(Geometry, "TryToWorld").Invoke(null, args)!);
        return args[4]!;
    }

    /// <summary>Как кнопка «Поля текста»: FrameTextRegionService.TryCreateProfile для универсальной рамки.</summary>
    private static object CreateProfile(object frame, double paperWidthMm, double paperHeightMm, object title, object number)
    {
        var jobType = NanoCadPrintRouteTests.PluginType("ZwcadBatchPlot.PlotJob");
        var job = Activator.CreateInstance(jobType)!;
        var source = NanoCadPrintRouteTests.PluginType("ZwcadBatchPlot.FrameSourceKind");
        jobType.GetProperty("FrameSource")!.SetValue(job, Enum.Parse(source, "Rectangle"));
        jobType.GetProperty("PaperWidthMm")!.SetValue(job, paperWidthMm);
        jobType.GetProperty("PaperHeightMm")!.SetValue(job, paperHeightMm);

        var args = new object?[] { job, frame, title, number, null, null };
        var created = (bool)Method("ZwcadBatchPlot.FrameTextRegionService", "TryCreateProfile").Invoke(null, args)!;
        Assert.True(created, (string?)args[5]);
        Assert.Equal("RECTANGLE", Get<string>(args[4]!, "Key"));
        return args[4]!;
    }

    private static object ExpectedWorld(double[] mm, object frame, double drawingUnitsPerMm)
    {
        var maxX = Get<double>(frame, "MaxX");
        var minY = Get<double>(frame, "MinY");
        return Rect(
            maxX + mm[0] * drawingUnitsPerMm,
            minY + mm[1] * drawingUnitsPerMm,
            maxX + mm[2] * drawingUnitsPerMm,
            minY + mm[3] * drawingUnitsPerMm);
    }

    private static void AssertRect(object expected, object actual)
    {
        foreach (var side in new[] { "MinX", "MinY", "MaxX", "MaxY" })
        {
            Assert.True(
                Math.Abs(Get<double>(expected, side) - Get<double>(actual, side)) <= 1e-6,
                $"{side}: expected {Describe(expected)}, actual {Describe(actual)}");
        }
    }

    private static string Describe(object rect)
        => $"[{Get<double>(rect, "MinX"):0.###}; {Get<double>(rect, "MinY"):0.###}] - [{Get<double>(rect, "MaxX"):0.###}; {Get<double>(rect, "MaxY"):0.###}]";

    private static object Rect(double[] v) => Rect(v[0], v[1], v[2], v[3]);

    private static object Rect(double x1, double y1, double x2, double y2)
        => NanoCadPrintRouteTests.PluginType("ZwcadBatchPlot.LocalRectangle")
            .GetMethod("FromPoints", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { x1, y1, x2, y2 })!;

    private static object Get(object target, string property) => Get<object>(target, property);

    private static T Get<T>(object target, string property)
        => (T)target.GetType().GetProperty(property)!.GetValue(target)!;

    private static MethodInfo Method(string type, string name)
        => NanoCadPrintRouteTests.PluginType(type).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
           ?? throw new InvalidOperationException(type + "::" + name + " was not found");
}
