using System;
using System.Collections.Generic;
using System.Linq;
#if NANOCAD
using Teigha.DatabaseServices;
#elif AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// Универсальные резервные области номера и наименования листа.
/// Профиль хранится в мм бумаги относительно правого нижнего угла рамки (там всегда находится штамп),
/// поэтому одна настройка работает при разных масштабах модели, форматах и ориентации бумаги.
/// Пересчёт мм ↔ единицы чертежа — <see cref="FrameTextRegionGeometry"/>.
/// </summary>
public static class FrameTextRegionService
{
    public static string GetProfileKey(FrameSourceKind source, string? blockName)
    {
        return source switch
        {
            FrameSourceKind.Spds => "SPDS",
            FrameSourceKind.Rectangle => "RECTANGLE",
            FrameSourceKind.Library => "LIBRARY:" + (blockName ?? "").Trim(),
            _ => source.ToString().ToUpperInvariant()
        };
    }

    public static FrameTextRegionProfile? FindProfile(
        AppSettings settings,
        FrameSourceKind source,
        string? blockName)
    {
        if (settings?.FrameTextRegionProfiles == null)
            return null;

        var key = GetProfileKey(source, blockName);
        return settings.FrameTextRegionProfiles.FirstOrDefault(profile =>
            profile.Enabled
            && string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase)
            && profile.TitleRegionMm?.HasArea() == true
            && profile.DrawingNumberRegionMm?.HasArea() == true);
    }

    public static void UpsertProfile(AppSettings settings, FrameTextRegionProfile profile)
    {
        settings.FrameTextRegionProfiles ??= new List<FrameTextRegionProfile>();
        var existing = settings.FrameTextRegionProfiles.FirstOrDefault(item =>
            string.Equals(item.Key, profile.Key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            settings.FrameTextRegionProfiles.Add(profile);
            return;
        }

        existing.Enabled = profile.Enabled;
        existing.TitleRegionMm = profile.TitleRegionMm;
        existing.DrawingNumberRegionMm = profile.DrawingNumberRegionMm;
    }

    public static bool RemoveProfile(AppSettings settings, FrameSourceKind source, string? blockName)
    {
        if (settings.FrameTextRegionProfiles == null)
            return false;

        var key = GetProfileKey(source, blockName);
        return settings.FrameTextRegionProfiles.RemoveAll(profile =>
            string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public static bool TryCreateProfile(
        PlotJob job,
        LocalRectangle frameWorld,
        LocalRectangle titleWorld,
        LocalRectangle numberWorld,
        out FrameTextRegionProfile profile,
        out string error)
    {
        profile = new FrameTextRegionProfile();
        error = "";

        if (!frameWorld.HasArea())
        {
            error = "Не удалось определить границы выбранной рамки.";
            return false;
        }

        if (job.PaperWidthMm <= 0 || job.PaperHeightMm <= 0)
        {
            error = "Для выбранной рамки не определён физический размер бумаги.";
            return false;
        }

        if (!titleWorld.HasArea() || !numberWorld.HasArea())
        {
            error = "Области номера и наименования должны иметь ненулевой размер.";
            return false;
        }

        // Один масштаб для обеих осей, не зависящий от ориентации листа (issue #17).
        if (!FrameTextRegionGeometry.TryGetDrawingUnitsPerMm(
                frameWorld, job.PaperWidthMm, job.PaperHeightMm, out var drawingUnitsPerMm))
        {
            error = "Не удалось вычислить масштаб рамки.";
            return false;
        }

        profile = new FrameTextRegionProfile
        {
            Key = GetProfileKey(job.FrameSource, job.BlockName),
            Enabled = true,
            TitleRegionMm = FrameTextRegionGeometry.ToPaperRelative(titleWorld, frameWorld, drawingUnitsPerMm),
            DrawingNumberRegionMm = FrameTextRegionGeometry.ToPaperRelative(numberWorld, frameWorld, drawingUnitsPerMm)
        };
        return true;
    }

    public static bool TryRead(
        Transaction tr,
        BlockTableRecord owner,
        LocalRectangle frameWorld,
        double paperWidthMm,
        double paperHeightMm,
        FrameTextRegionProfile? profile,
        CadTextExtractor.OwnerTextCache? ownerTextCache,
        out string drawingNumber,
        out string title)
    {
        drawingNumber = "";
        title = "";
        if (profile == null || !profile.Enabled || !frameWorld.HasArea())
            return false;
        if (paperWidthMm <= 0 || paperHeightMm <= 0)
            return false;

        // Поиск ведётся от правого нижнего угла рамки одним масштабом для обеих осей:
        // штамп на листе любого формата и ориентации прижат к этому углу (issue #17).
        if (!FrameTextRegionGeometry.TryGetDrawingUnitsPerMm(
                frameWorld, paperWidthMm, paperHeightMm, out var drawingUnitsPerMm))
            return false;

        var titleRegion = profile.TitleRegionMm == null
            ? new LocalRectangle()
            : FrameTextRegionGeometry.ToWorld(profile.TitleRegionMm, frameWorld, drawingUnitsPerMm);
        var numberRegion = profile.DrawingNumberRegionMm == null
            ? new LocalRectangle()
            : FrameTextRegionGeometry.ToWorld(profile.DrawingNumberRegionMm, frameWorld, drawingUnitsPerMm);

        if (titleRegion.HasArea())
            title = CadTextExtractor.ExtractWorldRegionText(tr, owner, titleRegion, ownerTextCache).Trim();
        if (numberRegion.HasArea())
            drawingNumber = CadTextExtractor.ExtractWorldRegionText(tr, owner, numberRegion, ownerTextCache).Trim();

        return !string.IsNullOrWhiteSpace(drawingNumber) || !string.IsNullOrWhiteSpace(title);
    }

    public static LocalRectangle FromJobWorldBounds(PlotJob job)
    {
        if (job.CornerPoints is { Length: >= 8 } points)
        {
            var xs = new[] { points[0], points[2], points[4], points[6] };
            var ys = new[] { points[1], points[3], points[5], points[7] };
            return LocalRectangle.FromPoints(xs.Min(), ys.Min(), xs.Max(), ys.Max());
        }

        return LocalRectangle.FromPoints(job.MinX, job.MinY, job.MaxX, job.MaxY);
    }
}
