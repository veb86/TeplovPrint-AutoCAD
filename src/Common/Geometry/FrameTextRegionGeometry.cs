using System;

namespace ZwcadBatchPlot;

/// <summary>
/// Геометрия резервных полей текста (кнопка «Поля текста», issue #17).
///
/// Штамп ГОСТ/СПДС всегда прижат к ПРАВОМУ НИЖНЕМУ углу листа, поэтому только этот угол
/// находится на одном и том же расстоянии от полей номера и наименования на листах любого
/// формата (А4 книжный, А3 альбомный, А1, А4х3 и т. д.). Области хранятся в мм бумаги
/// относительно правого нижнего угла рамки: X ≤ 0 — влево от правой границы, Y ≥ 0 — вверх
/// от нижней границы.
///
/// Перевод мм ↔ единицы чертежа делается ОДНИМ масштабом для обеих осей. Раньше масштаб
/// считался по осям отдельно (ширина рамки / ширина бумаги, высота рамки / высота бумаги).
/// Для универсальных прямоугольных рамок бумага всегда определяется альбомной (длинная
/// сторона = ширина), поэтому у книжного А4 (210×297) получалось sx = 210/297 и sy = 297/210:
/// смещения от правого нижнего угла растягивались/сжимались, и поля, заданные на листе
/// А3, на листах других форматов указывали не туда. Масштаб листа не зависит от ориентации,
/// поэтому он берётся по коротким сторонам рамки и бумаги.
/// </summary>
public static class FrameTextRegionGeometry
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Единиц чертежа на 1 мм бумаги для рамки <paramref name="frameWorld"/>, напечатанной на бумаге
    /// <paramref name="paperWidthMm"/>×<paramref name="paperHeightMm"/> (ориентация бумаги не важна).
    /// </summary>
    public static bool TryGetDrawingUnitsPerMm(
        LocalRectangle frameWorld,
        double paperWidthMm,
        double paperHeightMm,
        out double drawingUnitsPerMm)
    {
        drawingUnitsPerMm = 0;
        if (frameWorld == null || !frameWorld.HasArea())
            return false;
        if (!(paperWidthMm > 0) || !(paperHeightMm > 0))
            return false;

        var frameWidth = Math.Abs(frameWorld.MaxX - frameWorld.MinX);
        var frameHeight = Math.Abs(frameWorld.MaxY - frameWorld.MinY);
        var frameShort = Math.Min(frameWidth, frameHeight);
        var paperShort = Math.Min(paperWidthMm, paperHeightMm);
        if (frameShort <= Epsilon || paperShort <= Epsilon)
            return false;

        drawingUnitsPerMm = frameShort / paperShort;
        return !double.IsNaN(drawingUnitsPerMm)
               && !double.IsInfinity(drawingUnitsPerMm)
               && drawingUnitsPerMm > Epsilon;
    }

    /// <summary>Область в координатах чертежа → мм бумаги от правого нижнего угла рамки.</summary>
    public static LocalRectangle ToPaperRelative(
        LocalRectangle worldRegion,
        LocalRectangle frameWorld,
        double drawingUnitsPerMm)
    {
        return LocalRectangle.FromPoints(
            (worldRegion.MinX - frameWorld.MaxX) / drawingUnitsPerMm,
            (worldRegion.MinY - frameWorld.MinY) / drawingUnitsPerMm,
            (worldRegion.MaxX - frameWorld.MaxX) / drawingUnitsPerMm,
            (worldRegion.MaxY - frameWorld.MinY) / drawingUnitsPerMm);
    }

    /// <summary>мм бумаги от правого нижнего угла рамки → область в координатах чертежа.</summary>
    public static LocalRectangle ToWorld(
        LocalRectangle paperRegion,
        LocalRectangle frameWorld,
        double drawingUnitsPerMm)
    {
        return LocalRectangle.FromPoints(
            frameWorld.MaxX + paperRegion.MinX * drawingUnitsPerMm,
            frameWorld.MinY + paperRegion.MinY * drawingUnitsPerMm,
            frameWorld.MaxX + paperRegion.MaxX * drawingUnitsPerMm,
            frameWorld.MinY + paperRegion.MaxY * drawingUnitsPerMm);
    }

    /// <summary>Переводит выбранную на рамке область в профиль (мм от правого нижнего угла).</summary>
    public static bool TryToPaperRelative(
        LocalRectangle worldRegion,
        LocalRectangle frameWorld,
        double paperWidthMm,
        double paperHeightMm,
        out LocalRectangle paperRegion)
    {
        paperRegion = new LocalRectangle();
        if (worldRegion == null || !TryGetDrawingUnitsPerMm(frameWorld, paperWidthMm, paperHeightMm, out var scale))
            return false;

        paperRegion = ToPaperRelative(worldRegion, frameWorld, scale);
        return true;
    }

    /// <summary>Находит на рамке область профиля (мм от правого нижнего угла) в координатах чертежа.</summary>
    public static bool TryToWorld(
        LocalRectangle paperRegion,
        LocalRectangle frameWorld,
        double paperWidthMm,
        double paperHeightMm,
        out LocalRectangle worldRegion)
    {
        worldRegion = new LocalRectangle();
        if (paperRegion == null || !TryGetDrawingUnitsPerMm(frameWorld, paperWidthMm, paperHeightMm, out var scale))
            return false;

        worldRegion = ToWorld(paperRegion, frameWorld, scale);
        return true;
    }
}
