using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
#if NANOCAD
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using HostMgd.EditorInput;
using Teigha.Geometry;
using CadApp = HostMgd.ApplicationServices.Application;
#elif AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>

/// </summary>
public sealed partial class BatchPlotForm : Window
{
    private readonly Document _currentDocument;
    private readonly BindingList<PlotJob> _jobs = new();
    private CancellationTokenSource? _printCts;
    private readonly List<string> _logLines = new();
    private readonly List<string> _selectedDwgFiles = new();
    private readonly TemporarySequenceOverlay _sequenceOverlay;
    private readonly AppSettings _settings;
    private bool _sequenceOverlayFollowsCurrentJobs;

    private bool _suppressSequenceOverlayFromMultiFile;
    private int _overlayScheduleGeneration;
    private bool _outputDirectoryIsCustom;
    private bool _outputDirectoryModified;
    private bool _updatingPrintSelection;
    private bool _allowDoubleClickTextEdit;
    private bool _uiReady;
    private bool _closed;
    private List<PlotJob>? _pendingPrintToggleJobs;
    private DrawingNumberReorderDialog? _renumberDialog;
    private Dictionary<PlotJob, string>? _renumberOriginalNumbers;
    private List<PlotJob>? _renumberCurrentJobs;
    private PlotJob? _highlightedJob;
    private string _lastLogPath = "";
    private string _mergedOutputPath = "";
    private string _dwfPlotDevice = "";
    private string _pdfPlotDevice = AcadPlotterInstaller.UsesBundledLaPlotters ? AcadPlotterInstaller.PreferredPdfPlotter : "";
    private long _nextSortPriority;
    private HashSet<string> _duplicateDrawingNumbers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _duplicateTitles = new(StringComparer.OrdinalIgnoreCase);
    private bool _styleSelectionReady;
    private readonly bool _spdsMode;
    private FrameSourceMode _frameSourceMode;


    private readonly UIElement[] _plotOnlyControls = default!;

    public BatchPlotForm(Document currentDocument) : this(currentDocument, false)
    {
    }

    public BatchPlotForm(Document currentDocument, bool spdsMode)
    {
        _currentDocument = currentDocument;
        _spdsMode = spdsMode;
        _sequenceOverlay = new TemporarySequenceOverlay(currentDocument);
        _settings = AppSettingsStore.Load();
        InitializeComponent();
        InitializeFrameSourceModes(spdsMode ? FrameSourceMode.Spds : _settings.BatchFrameSourceMode);

        _plotOnlyControls = new UIElement[]
        {
            _styleCombo,
            _styleSettingsButton,
            _leaveMarginCheckBox,
            _marginInput
        };


        _mergePdfCheckBox.IsChecked = _settings.MergePdf;
        _leaveMarginCheckBox.IsChecked = _settings.LeavePaperMargin;
        InitMarginCombo(_marginInput, 68, _settings.PaperMarginMm);
        _marginInput.IsEnabled = _leaveMarginCheckBox.IsChecked == true;
        _outputDirectory.Text = GetDefaultOutputDirectory();

        _grid.ItemsSource = _jobs;

        LoadPlotOptions();
        RefreshStatus();
        _uiReady = true;

        Closing += (_, _) =>
        {

            _printCts?.Cancel();
            _renumberDialog?.Close();
            ClearSequenceOverlay(repaint: false);
            SaveCurrentSettings();
        };
        Closed += (_, _) =>
        {
            _closed = true;

            _sequenceOverlay.Dispose();
        };
    }



    private void LoadPlotOptions()
    {
        _outputFormatCombo.Items.Clear();
        _outputFormatCombo.Items.Add("PDF");
        _outputFormatCombo.Items.Add("PNG");
        _outputFormatCombo.Items.Add("JPG");
        _outputFormatCombo.Items.Add("DWF");
        _outputFormatCombo.Items.Add("DWG");
        _outputFormatCombo.SelectedIndex = 0;
        RefreshSavePathModeOptions(preserveSelection: false);
        var printerChoice = AcadPlotterInstaller.AllowsPdfPrinterChoice ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        _printerLabel.Visibility = printerChoice;
        _printerCombo.Visibility = printerChoice;

        try
        {
            var pdfInstall = AcadPlotterInstaller.InstallBundledPlotter();
            var pngInstall = AcadPlotterInstaller.InstallPngPlotter();
            var jpgInstall = AcadPlotterInstaller.InstallJpgPlotter();
            var dwfInstall = AcadPlotterInstaller.InstallDwfPlotter();
            AcadPlotterInstaller.RefreshPlotterDevicesIfNeeded(
                pdfInstall.Written || pngInstall.Written || jpgInstall.Written || dwfInstall.Written);
            var validator = PlotSettingsValidator.Current;
            var devices = validator.GetPlotDeviceList()
                .Cast<object>()
                .Select(item => item?.ToString() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            _pdfPlotDevice = AcadPlotterInstaller.ResolvePdfPlotDevice(devices, _settings.PdfPrinter);
            _dwfPlotDevice = FindDwfPlotDevice(devices, dwfInstall.DeviceName);
            LoadPrinterChoices(devices);
            foreach (var style in PlotStyleManager.GetAvailableCtbStyles())
            {
                _styleCombo.Items.Add(style);
            }

            PlotStyleManager.RestoreSavedStyle(_styleCombo, _settings.LastStyleSheet);
        }
        catch (Exception ex)
        {
        System.Windows.MessageBox.Show("Не удалось загрузить CTB: " + ex.Message, "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateOutputFormatUi();
        SaveCurrentSettings();
        _styleSelectionReady = true;
    }

    /// <summary>
    /// nanoCAD (issue #13): the "Принтер" list next to CTB. Shows the devices of nanoCAD with the printer PDF, PNG and JPG
    /// are printed with selected: the one chosen earlier or, until the user chooses, the automatic one.
    /// </summary>
    private void LoadPrinterChoices(IReadOnlyList<string> devices)
    {
        _printerCombo.Items.Clear();
        if (!AcadPlotterInstaller.AllowsPdfPrinterChoice)
        {
            return;
        }

        foreach (var printer in PlotDeviceSelector.ListPrinterChoices(devices))
        {
            _printerCombo.Items.Add(printer);
        }

        _printerCombo.SelectedItem = PlotDeviceSelector.FindChoice(devices, _pdfPlotDevice);
    }

    private static string FindDwfPlotDevice(IReadOnlyList<string> devices, string installedPlotter)
    {
#if NANOCAD
        // LA_dwf.pc3 is not installed in nanoCAD (issue #5): use its own DWF plotter.
        return PlotDeviceSelector.SelectDwfDevice(devices) ?? "";
#else
        var preferred = new[]
        {
            installedPlotter,
            AcadPlotterInstaller.PreferredDwfPlotter,
            "DWF6 ePlot.pc3",
            "DWF6 ePlot.pc5",
            "ZWPLOT_DWF.pc5",
            "M_DWF.pc5"
        };
        foreach (var expected in preferred.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var match = devices.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return devices.FirstOrDefault(value => value.IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                                               && value.IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0)
               ?? installedPlotter;
#endif
    }



    private TitleBlockScanScope? PromptScanScope() => BatchPlotCommands.PromptScanScope(this);

    private void InitializeFrameSourceModes(FrameSourceMode initial)
    {
        _frameSourceCombo.Items.Clear();
        _frameSourceCombo.Items.Add(new FrameSourceChoice(FrameSourceMode.Spds, "Форматы CSoft СПДС"));
        _frameSourceCombo.Items.Add(new FrameSourceChoice(FrameSourceMode.Library, "Форматы из библиотеки"));
        _frameSourceCombo.Items.Add(new FrameSourceChoice(FrameSourceMode.Rectangles, "Прочие форматы"));
        _frameSourceCombo.Items.Add(new FrameSourceChoice(FrameSourceMode.Manual, "Ручной выбор"));
        _frameSourceCombo.SelectedItem = _frameSourceCombo.Items.Cast<FrameSourceChoice>().First(x => x.Mode == initial);
        ApplyFrameSourceMode(initial);
    }

    private void ApplyFrameSourceMode(FrameSourceMode mode)
    {
        _frameSourceMode = mode;
        var spds = mode == FrameSourceMode.Spds;
        Title = spds ? "SPDS Print — пакетная печать форматов CSoft СПДС" : "Teplov Print — пакетная печать по форматам";
        _scanObjectsButton.IsEnabled = true;
        _addFilesButton.IsEnabled = mode != FrameSourceMode.Manual;
        _scanButton.ToolTip = mode switch
        {
            FrameSourceMode.Spds => "Найти форматы CSoft СПДС (SPDSFORMAT) в выбранных пространствах.",
            FrameSourceMode.Library => "Найти форматы, описанные в библиотеке форматов.",
            FrameSourceMode.Rectangles => "Найти прочие прямоугольные форматы.",
            _ => "Сканировать форматы, выбранные вручную в текущем чертеже."
        };
    }

    private void ScanCurrentDrawing()
    {
        _suppressSequenceOverlayFromMultiFile = false;

        if (_frameSourceMode == FrameSourceMode.Manual)
        {
            ScanSelectedObjects();
            return;
        }

        var scope = PromptScanScope();
        if (scope == null)
            return;

        var library = TitleBlockLibraryStore.Load();
        if (_frameSourceMode == FrameSourceMode.Library && library.Blocks.Count == 0)
        {
        System.Windows.MessageBox.Show("Библиотека форматов пуста. Сначала выберите «Добавить формат» в меню пакетной печати.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
            ClearSequenceOverlay();
            RefreshStatus();
            return;
        }

        _selectedDwgFiles.Clear();
        var scannedJobs = ScanByCurrentSource(scope.Value, null, library);
        TransformScannedJobsToDcs(scannedJobs);
        var addedCount = CountNewPlotJobs(_jobs, scannedJobs);
        var mergedJobs = MergePlotJobs(_jobs, scannedJobs);
        SortAndRefreshOutputPaths(mergedJobs);
        ScheduleSequenceOverlayForCurrentJobs();
        AppendLog("INFO", $"Сканирование текущего чертежа завершено; добавлено: {addedCount}, всего листов: {_jobs.Count}.");
    }

    internal void ScanSpdsOnOpen()
    {
        if (_spdsMode)
            ScanCurrentDrawing();
    }

    /// <summary>


    /// </summary>
    private void ScanSelectedObjects()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        var library = TitleBlockLibraryStore.Load();
        if (_frameSourceMode == FrameSourceMode.Library && library.Blocks.Count == 0)
        {
        System.Windows.MessageBox.Show("Библиотека форматов пуста. Сначала добавьте формат.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
            ClearSequenceOverlay();
            return;
        }

        CadWindowFocus.HideForCadInput(this);
        try
        {
            var selectionFilter = _frameSourceMode == FrameSourceMode.Spds
                ? ObjectSelectionPrompt.SpdsFormatFilter()
                : _frameSourceMode == FrameSourceMode.Manual
                    ? ObjectSelectionPrompt.AllSupportedFrameFilter(_settings.RecognizeFourLineRectangleFrames)
                    : _frameSourceMode == FrameSourceMode.Rectangles
                        ? ObjectSelectionPrompt.RectangleFrameFilter(_settings.RecognizeFourLineRectangleFrames)
                        : ObjectSelectionPrompt.TitleBlockFilter();
            var prompt = ObjectSelectionPrompt.Prompt(
                _currentDocument.Editor,
                "\nВыберите форматы для пакетной печати (правой кнопкой — область сканирования): ",
                selectionFilter);
            if (prompt.Cancelled)
            {
                return;
            }

            List<PlotJob> scannedJobs;
            if (prompt.Scope is { } scope)
            {
                scannedJobs = ScanByCurrentSource(scope, null, library);
            }
            else if (prompt.SelectedIds is { Length: > 0 } selectedIds)
            {
                scannedJobs = ScanByCurrentSource(TitleBlockScanScope.CurrentSpace, selectedIds, library);
                if (scannedJobs.Count == 0)
                {
        System.Windows.MessageBox.Show("Среди выбранных объектов рамки не найдены.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            else
            {
                return;
            }

            _selectedDwgFiles.Clear();
            TransformScannedJobsToDcs(scannedJobs);
            var addedCount = CountNewPlotJobs(_jobs, scannedJobs);
            var mergedJobs = MergePlotJobs(_jobs, scannedJobs);
            SortAndRefreshOutputPaths(mergedJobs);
            ScheduleSequenceOverlayForCurrentJobs();
            AppendLog(
                "INFO",
                prompt.Scope != null
            ? $"Сканирование области завершено: добавлено {addedCount}, всего {_jobs.Count}."
            : $"Сканирование выбранных объектов завершено: добавлено {addedCount}, всего {_jobs.Count}.");

        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private List<PlotJob> ScanByCurrentSource(
        TitleBlockScanScope scope,
        IEnumerable<ObjectId>? selectedIds,
        TitleBlockLibrary library)
    {
        var result = new List<PlotJob>();
        if (_frameSourceMode == FrameSourceMode.Library || _frameSourceMode == FrameSourceMode.Manual)
        {
            result.AddRange(selectedIds == null
                ? TitleBlockScanner.Scan(_currentDocument, library, scope, _settings.PaperMatchToleranceMm)
                : TitleBlockScanner.Scan(_currentDocument, library, selectedIds, _settings.PaperMatchToleranceMm));
        }
        if (_frameSourceMode == FrameSourceMode.Spds || _frameSourceMode == FrameSourceMode.Manual)
        {
            var warnings = new List<string>();
            result.AddRange(SpdsFormatScanner.Scan(
                _currentDocument.Database,
                _currentDocument.Database.Filename ?? _currentDocument.Name,
                scope,
                selectedIds,
                _settings.PaperMatchToleranceMm,
                null,
                warnings));
            foreach (var warning in warnings) AppendLog("WARN", warning);
        }
        if (_frameSourceMode == FrameSourceMode.Rectangles || _frameSourceMode == FrameSourceMode.Manual)
        {
            var rectangles = selectedIds == null
                ? RectangleFrameScanner.ScanScope(
                    _currentDocument, scope, _settings.PaperMatchToleranceMm,
                    _settings.RecognizeFourLineRectangleFrames)
                : RectangleFrameScanner.ScanSelection(
                    _currentDocument, selectedIds, _settings.PaperMatchToleranceMm,
                    _settings.RecognizeFourLineRectangleFrames);
            result = MergePlotJobs(result, rectangles.Select(item => item.Job));
        }
        return result;
    }


    private void TransformScannedJobsToDcs(List<PlotJob> jobs)
    {
        try
        {
            var wcsToDcs = BatchPlotCommands.BuildWcsToDcsMatrix(_currentDocument.Editor);
            foreach (var job in jobs)
            {
                if (job.UsesUserCoordinateSystem)
                {


                    job.IsDcsWindow = false;
                    job.IsManualWindow = true;
                    continue;
                }


                if (job.IsPaperSpace)
                {
                    job.IsDcsWindow = true;
                    job.IsManualWindow = true;
                    continue;
                }




                Point3d[] pts;
                var cp = job.CornerPoints;
                if (cp != null)
                {
                    pts = new[]
                    {
                        new Point3d(cp[0], cp[1], 0).TransformBy(wcsToDcs),
                        new Point3d(cp[2], cp[3], 0).TransformBy(wcsToDcs),
                        new Point3d(cp[4], cp[5], 0).TransformBy(wcsToDcs),
                        new Point3d(cp[6], cp[7], 0).TransformBy(wcsToDcs)
                    };
                }
                else
                {
                    pts = new[]
                    {
                        new Point3d(job.MinX, job.MinY, 0).TransformBy(wcsToDcs),
                        new Point3d(job.MaxX, job.MinY, 0).TransformBy(wcsToDcs),
                        new Point3d(job.MaxX, job.MaxY, 0).TransformBy(wcsToDcs),
                        new Point3d(job.MinX, job.MaxY, 0).TransformBy(wcsToDcs)
                    };
                }
                job.MinX = pts.Min(p => p.X);
                job.MinY = pts.Min(p => p.Y);
                job.MaxX = pts.Max(p => p.X);
                job.MaxY = pts.Max(p => p.Y);
                job.IsDcsWindow = true;

                job.IsManualWindow = true;
            }
        }
        catch (System.Exception ex)
        {
            AppendLog("WARN", $"Не удалось преобразовать координаты рамки WCS→DCS; используются координаты WCS: {ex.Message}");
        }
    }

    private void AddDwgFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Файлы DWG (*.dwg)|*.dwg",
            Multiselect = true,
            Title = "Выберите DWG для пакетной печати"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var files = dialog.FileNames
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            return;
        }

        var catalogErrors = new List<string>();
        var spaces = DwgSpaceCatalog.ListSpaces(files, catalogErrors);
        if (catalogErrors.Count > 0)
        {
            AppendLog("WARN", "Не удалось прочитать листы некоторых файлов: " + string.Join("; ", catalogErrors));
        }

        if (spaces.Count == 0)
        {
            System.Windows.MessageBox.Show(
                catalogErrors.Count > 0
            ? "Не удалось получить пространства модели/листы для сканирования.\n" + string.Join("\n", catalogErrors)
            : "В выбранных DWG нет доступных для сканирования модели или листов.",
            "Пакетная печать",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var defaultStyle = _styleCombo.SelectedItem?.ToString() ?? "";
        var picker = new ScanSpacePickerDialog(
            spaces,
            defaultStyle,
            PlotStyleManager.GetAvailableCtbStyles());
        if (CadDialog.ShowModal(picker, this) != true)
        {
            return;
        }

        var selected = picker.SelectedSpaces;
        if (selected.Count == 0)
        {
            return;
        }


        foreach (var file in selected.Select(s => s.FilePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_selectedDwgFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                _selectedDwgFiles.Add(file);
            }
        }

        var library = TitleBlockLibraryStore.Load();
        var added = new List<PlotJob>();
        var errors = new List<string>();

        foreach (var fileGroup in selected.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var file = fileGroup.Key;
            var allowed = new HashSet<string>(
                fileGroup.Select(s => s.LayoutName),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var fileStyle = fileGroup.FirstOrDefault()?.StyleSheet ?? "";
                var scanned = ScanExternalFile(file, library, allowed);
                foreach (var job in scanned)
                {
                    job.StyleSheet = fileStyle;
                }

                added.AddRange(scanned);
                AppendLog("INFO", $"Файл {file}: проверено пространств — {allowed.Count}, найдено листов — {scanned.Count}.");
            }
            catch (Exception ex)
            {
                var message = $"{file}: {ex}";
                errors.Add($"{file}: {ex.Message}");
                AppendLog("ERROR", "Ошибка сканирования: " + message);
            }
        }

        SortAndRefreshOutputPaths(MergePlotJobs(_jobs, added));

        _suppressSequenceOverlayFromMultiFile = true;
        ClearSequenceOverlay();

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
            "Не удалось отсканировать некоторые DWG:\n" + string.Join("\n", errors),
            "Пакетная печать",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private List<PlotJob> ScanExternalFile(
        string file,
        TitleBlockLibrary library,
        ISet<string>? allowedLayoutNames = null)
    {
        string currentPath;
        try
        {
            currentPath = string.IsNullOrWhiteSpace(_currentDocument.Database.Filename)
                ? ""
                : Path.GetFullPath(_currentDocument.Database.Filename);
        }
        catch
        {
            currentPath = _currentDocument.Database.Filename ?? "";
        }

        if (!string.IsNullOrWhiteSpace(currentPath)
            && string.Equals(Path.GetFullPath(file), currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return ScanDatabaseByCurrentSource(_currentDocument.Database, file, library, allowedLayoutNames);
        }

        using var db = new Database(false, true);
        db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        return ScanDatabaseByCurrentSource(db, file, library, allowedLayoutNames);
    }

    private List<PlotJob> ScanDatabaseByCurrentSource(
        Database db,
        string sourceFile,
        TitleBlockLibrary library,
        ISet<string>? allowedLayoutNames)
    {
        var result = new List<PlotJob>();
        if (_frameSourceMode == FrameSourceMode.Library)
        {
            result.AddRange(TitleBlockScanner.Scan(
                db, library, sourceFile, null, TitleBlockScanScope.AllSpaces, null,
                _settings.PaperMatchToleranceMm, null, allowedLayoutNames));
        }
        if (_frameSourceMode == FrameSourceMode.Spds)
        {
            var warnings = new List<string>();
            result.AddRange(SpdsFormatScanner.Scan(
                db, sourceFile, TitleBlockScanScope.AllSpaces, null,
                _settings.PaperMatchToleranceMm, allowedLayoutNames, warnings));
            foreach (var warning in warnings) AppendLog("WARN", warning);
        }
        if (_frameSourceMode == FrameSourceMode.Rectangles)
        {
            var rectangles = RectangleFrameScanner.ScanDatabase(
                db,
                sourceFile,
                TitleBlockScanScope.AllSpaces,
                allowedLayoutNames,
                _settings.PaperMatchToleranceMm,
                _settings.RecognizeFourLineRectangleFrames);
            result = MergePlotJobs(result, rectangles.Select(item => item.Job));
        }
        return result;
    }

    /// <summary>



    /// </summary>



    private static string PlotJobIdentityKey(PlotJob job)
    {
        var width = Math.Abs(job.MaxX - job.MinX);
        var height = Math.Abs(job.MaxY - job.MinY);
        if (width > 1e-6 && height > 1e-6)
        {
            return $"G|{job.SourceFile}|{job.SpaceName}|{job.MinX:0.###}|{job.MinY:0.###}|{job.MaxX:0.###}|{job.MaxY:0.###}";
        }

        return $"H|{job.SourceFile}|{job.SpaceName}|{job.BlockHandle}";
    }

    private static int CountNewPlotJobs(IEnumerable<PlotJob> existing, IEnumerable<PlotJob> incoming)
    {
        var keys = new HashSet<string>(existing.Select(PlotJobIdentityKey), StringComparer.OrdinalIgnoreCase);
        return incoming.Count(job => keys.Add(PlotJobIdentityKey(job)));
    }

    private static List<PlotJob> MergePlotJobs(IEnumerable<PlotJob> existing, IEnumerable<PlotJob> incoming)
    {
        var merged = existing.ToList();
        var keys = new HashSet<string>(merged.Select(PlotJobIdentityKey), StringComparer.OrdinalIgnoreCase);
        foreach (var job in incoming)
        {
            if (keys.Add(PlotJobIdentityKey(job)))
            {
                merged.Add(job);
            }
        }

        return merged;
    }

    private void SortAndRefreshOutputPaths(IReadOnlyList<PlotJob>? sourceJobs = null)
    {
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var overlayKeyBefore = CaptureOverlayRebuildKey();


        var sorted = SortTitleBlockJobs(sourceJobs == null ? _jobs.ToList() : sourceJobs.ToList());

        var sequenceDigits = FileNameSanitizer.ResolveSequenceDigits(
            _settings.AutoFileNameSequenceDigits,
            _settings.FileNameSequenceDigits,
            _settings.FileNameSequenceStartNumber,
            sorted.Count);
        var dwgOutputPaths = IsDwgOutput
            ? DwgSplitService.BuildOutputPaths(
                sorted,
                _currentDocument,
                _settings,
                customOutputDirectory: CustomOutputDirectory,
                sourceSubfolder: AutomaticOutputSubfolder)
            : null;
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refreshed = new List<PlotJob>(sorted.Count);
        for (var index = 0; index < sorted.Count; index++)
        {
            var job = sorted[index];
            if (dwgOutputPaths != null)
            {
                job.OutputPath = dwgOutputPaths[job];
            }
            else
            {
                var sequenceNumber = _settings.FileNameSequenceStartNumber + index;
                job.OutputPath = BuildOutputPath(job, sequenceNumber, sequenceDigits, reservedPaths);
            }

            job.DisplayOutputFileName = job.OutputFileName;
            refreshed.Add(job);
        }

        ReplaceBindingListContents(_jobs, refreshed);
        _grid.Items.Refresh();

        RebuildDuplicateIdentitySets();
        RefreshStatus();
        if (_sequenceOverlayFollowsCurrentJobs
            && !OverlayRebuildKeysEqual(overlayKeyBefore, CaptureOverlayRebuildKey()))
        {
            ScheduleSequenceOverlayForCurrentJobs();
        }
    }

    /// <summary>


    /// </summary>
    private static void ReplaceBindingListContents<T>(BindingList<T> target, IEnumerable<T> values)
    {
        target.RaiseListChangedEvents = false;
        try
        {
            target.Clear();
            foreach (var value in values)
            {
                target.Add(value);
            }
        }
        finally
        {
            target.RaiseListChangedEvents = true;
            target.ResetBindings();
        }
    }



    private void GridCellMouseDownLeft(PlotJob clickedJob)
    {
        if (_grid.SelectedItems.Count > 1)
        {

            var highlightedJobs = GetHighlightedJobs();
            _pendingPrintToggleJobs = highlightedJobs.Contains(clickedJob) ? highlightedJobs : null;
        }
        else
        {
            _pendingPrintToggleJobs = null;
        }
    }

    private static DataGridRow? HitTestRow(DependencyObject? source)
    {
        while (source != null && source is not DataGridRow)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return source as DataGridRow;
    }

    private static DataGridCell? HitTestCell(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is DataGridCell cell)
            {
                return cell;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = HitTestRow(e.OriginalSource as DependencyObject);
        if (row?.Item is not PlotJob clickedJob)
        {
            return;
        }

        var cell = HitTestCell(e.OriginalSource as DependencyObject);
        if (cell?.Column == _printColumn)
        {

            GridCellMouseDownLeft(clickedJob);
        }
    }

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = HitTestRow(e.OriginalSource as DependencyObject);
        if (row?.Item is not PlotJob job)
        {
            return;
        }

        if (!_grid.SelectedItems.Contains(job))
        {
            _grid.UnselectAll();
            _grid.SelectedItem = job;
        }

        _grid.CurrentCell = new DataGridCellInfo(job, _grid.CurrentColumn ?? _grid.Columns[0]);
    }

    private void Grid_LoadingRow(object sender, DataGridRowEventArgs e)
    {

        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_grid.CurrentItem is PlotJob job)
        {

            HighlightSequenceOverlayJob(job);
        }
    }

    private void MoveCurrentJobToFirst()
    {
        if (_settings.TitleBlockBatchSortMode == TitleBlockSortMode.Spatial)
        {
            return;
        }

        if (_grid.CurrentItem is not PlotJob job)
        {
            return;
        }

        job.SortPriority = ++_nextSortPriority;
        SortAndRefreshOutputPaths();
        _grid.SelectedItem = job;
        if (_grid.Columns.Count > 0)
        {
            _grid.CurrentCell = new DataGridCellInfo(job, _grid.Columns[0]);
        }
        _grid.ScrollIntoView(job);
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var enabled = _grid.CurrentItem is PlotJob;

        _moveToFirstItem.IsEnabled = enabled
                                     && _settings.TitleBlockBatchSortMode != TitleBlockSortMode.Spatial;
        _changeFormatItem.IsEnabled = enabled;
        _markNotPrintItem.IsEnabled = enabled;
        _deleteItem.IsEnabled = enabled;
        if (!enabled)
        {
            e.Handled = true;
        }
    }

    private void MoveToFirst_Click(object sender, RoutedEventArgs e) => MoveCurrentJobToFirst();

    private void ChangeFormat_Click(object sender, RoutedEventArgs e) => ChangeHighlightedFormats();

    private void MarkNotPrint_Click(object sender, RoutedEventArgs e) => MarkHighlightedJobsNotPrint();

    private void DeleteHighlighted_Click(object sender, RoutedEventArgs e) => RemoveHighlightedJobs();

    private void MarkHighlightedJobsNotPrint()
    {
        foreach (var job in GetHighlightedJobs())
        {
            job.Selected = false;
        }


        RemoveUnselectedJobs();
    }

    private List<PlotJob> GetHighlightedJobs()
    {
        var jobs = _grid.SelectedItems.OfType<PlotJob>().Distinct().ToList();
        if (jobs.Count == 0 && _grid.CurrentItem is PlotJob current)
        {
            jobs.Add(current);
        }

        return jobs;
    }

    private void ChangeHighlightedFormats()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var jobs = GetHighlightedJobs();
        if (jobs.Count == 0)
        {
            System.Windows.MessageBox.Show("Выберите один или несколько листов.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetJobSize(jobs[0], out var width, out var height))
        {
            System.Windows.MessageBox.Show("Не удалось определить размеры выбранного формата.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SinglePlotPaperSelectionForm(PaperSizeDetector.CreateStandardFormatCandidates(width, height));
        if (CadDialog.ShowModal(dialog, this) != true)
        {
            return;
        }

        var selectedName = dialog.SelectedPaper.PaperName;
        var changed = 0;
        foreach (var job in jobs)
        {
            if (!TryGetJobSize(job, out width, out height))
            {
                continue;
            }

            var paper = PaperSizeDetector.CreateStandardFormatCandidates(width, height)
                .FirstOrDefault(candidate => string.Equals(candidate.PaperName, selectedName, StringComparison.OrdinalIgnoreCase));
            if (paper == null)
            {
                continue;
            }

            ApplySelectedPaper(job, paper);
            changed++;
        }

        if (changed > 0)
        {
            SortAndRefreshOutputPaths();
        }
    }

    private static bool TryGetJobSize(PlotJob job, out double width, out double height)
    {
        width = Math.Abs(job.MaxX - job.MinX);
        height = Math.Abs(job.MaxY - job.MinY);
        return width > 1e-6 && height > 1e-6;
    }

    private static void ApplySelectedPaper(PlotJob job, PaperDetection paper)
    {
        job.PaperName = paper.PaperName;
        job.PaperWidthMm = paper.PaperWidthMm;
        job.PaperHeightMm = paper.PaperHeightMm;
        job.PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm";
        job.ScaleText = paper.ScaleText;
        job.DetectedRequiresCustomPaperRegistration = false;
        job.RequiresCustomPaperRegistration = false;
        job.RequireExactPaperSize = false;
        job.UseExactWindowScale = false;
        job.CustomPaperWasAdded = false;
        job.DetectionNote = paper.Note;
    }

    private void RemoveHighlightedJobs()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var highlightedJobs = GetHighlightedJobs();

        if (highlightedJobs.Count == 0)
        {
        System.Windows.MessageBox.Show("Нет выделенной строки листа.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var job in highlightedJobs)
        {
            _jobs.Remove(job);
        }

        SortAndRefreshOutputPaths();
        RefreshSelectedOverlay();
    }

    private void PrintCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingPrintSelection)
        {
            return;
        }

        if (((CheckBox)sender).DataContext is not PlotJob changedJob)
        {
            return;
        }

        ApplyPrintSelectionToHighlightedRows(changedJob);
        if (!changedJob.Selected)
        {
            RemoveUnselectedJobs();
            return;
        }

        RefreshStatus();
        RefreshSelectedOverlay();
    }


    private void RemoveUnselectedJobs()
    {
        var removed = _jobs.Where(job => !job.Selected).ToList();
        if (removed.Count == 0)
        {
            _grid.Items.Refresh();
            return;
        }

        foreach (var job in removed)
        {
            if (ReferenceEquals(_highlightedJob, job))
            {
                _highlightedJob = null;
            }
            _jobs.Remove(job);
        }


        SortAndRefreshOutputPaths();
    }

    private void ApplyPrintSelectionToHighlightedRows(PlotJob changedJob)
    {
        var targetJobs = _pendingPrintToggleJobs ?? GetHighlightedJobs();
        _pendingPrintToggleJobs = null;
        if (targetJobs.Count <= 1 || !targetJobs.Contains(changedJob))
        {
            return;
        }

        try
        {
            _updatingPrintSelection = true;

            foreach (var job in targetJobs)
            {
                job.Selected = changedJob.Selected;
            }
        }
        finally
        {
            _updatingPrintSelection = false;
        }

        _grid.Items.Refresh();
    }

    private void RefreshSelectedOverlay()
    {
        if (_sequenceOverlayFollowsCurrentJobs)
        {
            ScheduleSequenceOverlayForCurrentJobs();
        }
    }

    private void Grid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {

        if (IsDrawingIdentityColumn(e.Column) && !_allowDoubleClickTextEdit)
        {
            e.Cancel = true;
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = HitTestCell(e.OriginalSource as DependencyObject);
        if (cell?.Column == null || cell.Column != _drawingNumberColumn && cell.Column != _titleColumn)
        {
            return;
        }

        try
        {
            _allowDoubleClickTextEdit = true;
            _grid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
            _grid.BeginEdit();
        }
        finally
        {

            _allowDoubleClickTextEdit = false;
        }
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row?.Item is not PlotJob job)
        {
            return;
        }

        var titleChanged = e.Column == _titleColumn && !string.Equals(job.Title, job.CadTitle, StringComparison.Ordinal);
        var numberChanged = e.Column == _drawingNumberColumn && !string.Equals(job.DrawingNumber, job.CadDrawingNumber, StringComparison.Ordinal);
        if (!titleChanged && !numberChanged)
        {
            return;
        }

        var ok = CadTextUpdater.TryUpdateOpenDocument(
            job,
            titleChanged ? job.Title : null,
            numberChanged ? job.DrawingNumber : null,
            _currentDocument,
            out var message);

        if (ok)
        {
            if (titleChanged)
            {
                job.CadTitle = job.Title;
            }

            if (numberChanged)
            {
                job.CadDrawingNumber = job.DrawingNumber;
            }
        }

        AppendLog(ok ? "INFO" : "WARN", message);
        if (numberChanged)
        {

            job.SortPriority = 0;
        }
        SortAndRefreshOutputPaths();
    }

    private bool IsDrawingIdentityColumn(DataGridColumn? column)
    {
        return column == _drawingNumberColumn || column == _titleColumn;
    }

    /// <summary>


    /// </summary>
    private void RebuildDuplicateIdentitySets()
    {
        _duplicateDrawingNumbers = FindDuplicateIdentityKeys(_jobs.Select(job => job.DrawingNumber));
        _duplicateTitles = FindDuplicateIdentityKeys(_jobs.Select(job => job.Title));
        DuplicateBrushConverter.DrawingNumbers = _duplicateDrawingNumbers;
        DuplicateBrushConverter.Titles = _duplicateTitles;
        _grid.Items.Refresh();
    }

    /// <summary>

    /// </summary>
    private static HashSet<string> FindDuplicateIdentityKeys(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = NormalizeIdentityText(value);
            if (key.Length == 0)
            {
                continue;
            }

            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        return new HashSet<string>(
            counts.Where(pair => pair.Value > 1).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeIdentityText(string? value)
    {
        return (value ?? string.Empty).Trim();
    }



    private void ShowSequenceOverlayForCurrentJobs()
    {
        _sequenceOverlayFollowsCurrentJobs = true;
        try
        {
            var currentJobs = _jobs.Where(job => job.Selected && IsCurrentDocumentJob(job)).ToList();
            var highlightJob = _highlightedJob != null && currentJobs.Contains(_highlightedJob) ? _highlightedJob : null;
            _sequenceOverlay.Show(currentJobs, highlightJob);
        }
        catch (Exception ex)
        {
            _sequenceOverlayFollowsCurrentJobs = false;
            _sequenceOverlay.Clear();
            AppendLog("WARN", "Не удалось показать временные номера: " + ex.Message);
        }
    }

    /// <summary>


    /// </summary>
    private void ScheduleSequenceOverlayForCurrentJobs()
    {
        if (_suppressSequenceOverlayFromMultiFile)
        {
            ClearSequenceOverlay();
            return;
        }

        if (_closed)
        {
            return;
        }

        var generation = ++_overlayScheduleGeneration;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed || generation != _overlayScheduleGeneration)
            {
                return;
            }

            ShowSequenceOverlayForCurrentJobs();
        }));
    }

    private void ShowRenumberPreviewOverlay(IReadOnlyList<PlotJob> previewOrder)
    {
        _sequenceOverlayFollowsCurrentJobs = true;
        try
        {
            var currentJobs = previewOrder
                .Where(job => job.Selected && IsCurrentDocumentJob(job))
                .ToList();
            var highlightJob = _highlightedJob != null && currentJobs.Contains(_highlightedJob) ? _highlightedJob : null;

            _sequenceOverlay.Show(currentJobs, highlightJob, (job, _) => job.DrawingNumber);
        }
        catch (Exception ex)
        {
            _sequenceOverlayFollowsCurrentJobs = false;
            _sequenceOverlay.Clear();
            AppendLog("WARN", "Не удалось показать предварительную перенумерацию: " + ex.Message);
        }
    }

    private void HighlightSequenceOverlayJob(PlotJob job)
    {
        _highlightedJob = job;
        if (!_sequenceOverlayFollowsCurrentJobs)
        {
            return;
        }

        if (!job.Selected || !IsCurrentDocumentJob(job))
        {

            _sequenceOverlay.SetHighlight(null);
            return;
        }

        _sequenceOverlay.SetHighlight(job);
    }

    private void ClearSequenceOverlay(bool repaint = true)
    {
        _overlayScheduleGeneration++;
        _sequenceOverlayFollowsCurrentJobs = false;
        _sequenceOverlay.Clear(repaint);
    }

    private bool IsCurrentDocumentJob(PlotJob job)
    {
        var source = job.SourceFile;
        var file = _currentDocument.Database.Filename;
        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(file))
        {
            try
            {
                return string.Equals(Path.GetFullPath(source), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(source, file, StringComparison.OrdinalIgnoreCase);
            }
        }

        return string.Equals(source, _currentDocument.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>

    /// </summary>
    private List<(PlotJob Job, string DrawingNumber)> CaptureOverlayRebuildKey()
    {
        var keys = new List<(PlotJob Job, string DrawingNumber)>();
        foreach (var job in _jobs)
        {
            if (!job.Selected || !IsCurrentDocumentJob(job))
            {
                continue;
            }

            keys.Add((job, job.DrawingNumber ?? ""));
        }

        return keys;
    }

    /// <summary>

    /// </summary>
    private static bool OverlayRebuildKeysEqual(
        IReadOnlyList<(PlotJob Job, string DrawingNumber)> left,
        IReadOnlyList<(PlotJob Job, string DrawingNumber)> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i].Job, right[i].Job)
                || !string.Equals(left[i].DrawingNumber, right[i].DrawingNumber, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }



    private void RenumberDrawingNumbers()
    {
        if (_jobs.Count == 0) return;
        if (_renumberDialog != null)
        {
            _renumberDialog.Activate();
            return;
        }


        var currentJobs = _jobs.Where(j => IsCurrentDocumentJob(j)).ToList();
        if (currentJobs.Count == 0)
        {
        System.Windows.MessageBox.Show("В текущем документе нет рамок для перенумерации.", "Перенумерация листов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }


        _renumberCurrentJobs = currentJobs;
        _renumberOriginalNumbers = currentJobs.ToDictionary(j => j, j => j.DrawingNumber);
        var detectedPrefix = DetectCommonDrawingNumberPrefix(currentJobs);
        _renumberDialog = new DrawingNumberReorderDialog(
            currentJobs.Count,
            detectedPrefix,
            _settings.SortOrderHorizontalFirst);
        _renumberDialog.PreviewRequested += PreviewRenumberDrawingNumbers;
        _renumberDialog.Closed += RenumberDialogClosed;
        _renumberDialog.Show();
        _renumberDialog.Activate();
    }

    private void PreviewRenumberDrawingNumbers()
    {
        if (_renumberDialog == null || _renumberCurrentJobs == null)
        {
            return;
        }

        var sorted = SortRenumberJobsByLayout(_renumberCurrentJobs, _renumberDialog.HorizontalFirst);
        ApplyRenumbering(sorted, _renumberDialog.Prefix, _renumberDialog.Suffix, _renumberDialog.StartNumber, _renumberDialog.Digits);
        RebuildDuplicateIdentitySets();
        _grid.Items.Refresh();
        ShowRenumberPreviewOverlay(sorted);
    }

    private void RenumberDialogClosed(object? sender, EventArgs e)
    {
        var dialog = (DrawingNumberReorderDialog)sender!;
        var currentJobs = _renumberCurrentJobs ?? new List<PlotJob>();
        var originalNumbers = _renumberOriginalNumbers ?? new Dictionary<PlotJob, string>();
        _renumberDialog = null;
        _renumberCurrentJobs = null;
        _renumberOriginalNumbers = null;

        if (!dialog.Accepted)
        {

            foreach (var kv in originalNumbers)
            {
                kv.Key.DrawingNumber = kv.Value;
            }
            _grid.Items.Refresh();
            SortAndRefreshOutputPaths();
            return;
        }

        var finalSorted = SortRenumberJobsByLayout(currentJobs, dialog.HorizontalFirst);
        ApplyRenumbering(finalSorted, dialog.Prefix, dialog.Suffix, dialog.StartNumber, dialog.Digits);
        foreach (var job in currentJobs)
        {

            job.SortPriority = 0;
        }
        _grid.Items.Refresh();

        _settings.SortOrderHorizontalFirst = dialog.HorizontalFirst;
        AppSettingsStore.Save(_settings);
        SortAndRefreshOutputPaths();


        var updated = CadTextUpdater.UpdateDrawingNumbers(finalSorted, _currentDocument,
            failure => AppendLog("WARN", failure));

        AppendLog("INFO", $"Перенумерация завершена: {finalSorted.Count} рамок отсортированы по листам " + (_settings.SortOrderHorizontalFirst ? "слева направо и сверху вниз" : "сверху вниз и слева направо") + $"; обновлено значений в CAD: {updated}.");
    }

    private static void ApplyRenumbering(IReadOnlyList<PlotJob> sorted, string prefix, string suffix, int start, int digits = 0)
    {
        if (digits <= 0)
        {
            var maxNumber = sorted.Count + start - 1;
            digits = Math.Max(2, maxNumber.ToString().Length);
        }

        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].DrawingNumber = prefix + (start + i).ToString($"D{digits}") + suffix;
            sorted[i].CadDrawingNumber = sorted[i].DrawingNumber;
        }
    }


    private static string DetectCommonDrawingNumberPrefix(IReadOnlyList<PlotJob> jobs)
    {
        if (jobs.Count == 0) return "";
        var numbers = jobs.Select(j => j.DrawingNumber).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (numbers.Count == 0) return "";


        var common = numbers[0];
        for (var i = 1; i < numbers.Count && common.Length > 0; i++)
        {
            var len = Math.Min(common.Length, numbers[i].Length);
            var j = 0;
            while (j < len && common[j] == numbers[i][j]) j++;
            common = common.Substring(0, j);
        }


        while (common.Length > 0 && char.IsDigit(common[common.Length - 1]))
            common = common.Substring(0, common.Length - 1);

        return common;
    }

    /// <summary>


    /// </summary>
    private List<PlotJob> SortRenumberJobsByLayout(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        var tabOrders = ReadCurrentLayoutTabOrders();
        var result = new List<PlotJob>(jobs.Count);
        var layoutGroups = jobs
            .GroupBy(job => job.SpaceName ?? "", StringComparer.Ordinal)
            .OrderBy(group => tabOrders.TryGetValue(group.Key, out var tabOrder) ? tabOrder : int.MaxValue)
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var layoutGroup in layoutGroups)
        {
            result.AddRange(SpatialSorter.Sort(layoutGroup.ToList(), horizontalFirst));
        }

        return result;
    }

    private Dictionary<string, int> ReadCurrentLayoutTabOrders()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var tr = _currentDocument.Database.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)tr.GetObject(_currentDocument.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                result[layout.LayoutName] = layout.TabOrder;
            }

            tr.Commit();
        }
        catch (Exception ex)
        {

            AppendLog("WARN", "Не удалось прочитать порядок листов; перенумерация будет выполнена по именам листов: " + ex.Message);
        }

        return result;
    }


    private List<PlotJob> SortTitleBlockJobs(IReadOnlyList<PlotJob> jobs)
    {
        if (jobs.Count > 0 && jobs.All(job => job.FrameSource == FrameSourceKind.Spds))
            return SortSpdsJobs(jobs);
        if (_settings.TitleBlockBatchSortMode == TitleBlockSortMode.Spatial)
        {

            return SortSpatialGroups(jobs);
        }

        var sorted = jobs
            .OrderByDescending(x => x.SortPriority)
            .ThenBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();


        return SpatiallyBreakTies(sorted);
    }

    private List<PlotJob> SortSpdsJobs(IReadOnlyList<PlotJob> jobs)
    {
        var pinned = jobs.Where(job => job.SortPriority > 0)
            .OrderByDescending(job => job.SortPriority)
            .ToList();
        var numbered = jobs.Where(job => job.SortPriority <= 0 && !job.PreferSpatialSort)
            .OrderBy(job => job.DrawingNumber, NaturalStringComparer.Instance)
            .ToList();
        var spatial = SpatialSorter.SortByLayout(
            jobs.Where(job => job.SortPriority <= 0 && job.PreferSpatialSort).ToList(),
            _settings.SortOrderHorizontalFirst);
        pinned.AddRange(numbered);
        pinned.AddRange(spatial);
        return pinned;
    }

    private void FrameSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_frameSourceCombo.SelectedItem is FrameSourceChoice choice)
        {
            ApplyFrameSourceMode(choice.Mode);
            if (_uiReady)
            {
                _settings.BatchFrameSourceMode = choice.Mode;
                AppSettingsStore.Save(_settings);
            }
        }
    }

    private sealed class FrameSourceChoice
    {
        public FrameSourceChoice(FrameSourceMode mode, string text) { Mode = mode; Text = text; }
        public FrameSourceMode Mode { get; }
        public string Text { get; }
        public override string ToString() => Text;
    }

    /// <summary>

    /// </summary>
    private List<PlotJob> SpatiallyBreakTies(List<PlotJob> sortedJobs)
    {
        if (sortedJobs.Count <= 1) return sortedJobs;
        var result = new List<PlotJob>(sortedJobs.Count);
        var i = 0;
        while (i < sortedJobs.Count)
        {
            var anchor = sortedJobs[i];
            var j = i + 1;

            while (j < sortedJobs.Count
                && sortedJobs[j].SortPriority == anchor.SortPriority
                && NaturalStringComparer.Instance.Compare(sortedJobs[j].DrawingNumber, anchor.DrawingNumber) == 0
                && string.Equals(sortedJobs[j].Title, anchor.Title, StringComparison.CurrentCultureIgnoreCase))
            {
                j++;
            }
            var group = sortedJobs.GetRange(i, j - i);
            if (group.Count > 1)
            {

                group = SortSpatialGroups(group);
            }
            result.AddRange(group);
            i = j;
        }
        return result;
    }

    /// <summary>



    /// </summary>
    private List<PlotJob> SortSpatialGroups(IReadOnlyList<PlotJob> jobs)
    {
        var result = new List<PlotJob>(jobs.Count);
        var sourceGroups = jobs
            .GroupBy(job => GetSourceGroupKey(job.SourceFile), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => GetSourceGroupOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var sourceGroup in sourceGroups)
        {
            result.AddRange(SpatialSorter.SortByLayout(
                sourceGroup.ToList(),
                _settings.SortOrderHorizontalFirst));
        }

        return result;
    }

    private int GetSourceGroupOrder(string sourceFile)
    {
        for (var index = 0; index < _selectedDwgFiles.Count; index++)
        {
            if (AreSamePath(_selectedDwgFiles[index], sourceFile))
            {
                return index;
            }
        }


        if (AreSamePath(_currentDocument.Database.Filename, sourceFile)
            || string.Equals(_currentDocument.Name, sourceFile, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.MaxValue;
    }

    private static string GetSourceGroupKey(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(sourceFile);
        }
        catch
        {
            return sourceFile?.Trim() ?? "";
        }
    }

    private static bool AreSamePath(string? left, string? right)
    {
        return string.Equals(
            GetSourceGroupKey(left),
            GetSourceGroupKey(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private void ClearJobs()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_jobs.Count == 0)
        {
            return;
        }

        if (System.Windows.MessageBox.Show("Очистить текущий список листов?", "Пакетная печать", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _jobs.Clear();
        _selectedDwgFiles.Clear();
        RebuildDuplicateIdentitySets();
        ClearSequenceOverlay();
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }
        RefreshStatus();
    }

    private string BuildOutputPath(
        PlotJob job,
        int sequenceNumber,
        int sequenceDigits,
        ISet<string> reservedPaths)
    {
        var baseName = FileNameSanitizer.FormatFileNamePattern(
            _settings.PdfFileNamePattern,
            job,
            sequenceNumber,
            sequenceDigits,
            _settings.LongPaperNameFormat,
            _settings.LongPaperSnapToleranceMm);
        return FileNameSanitizer.MakeUnique(
            GetOutputDirectory(job),
            baseName,
            reservedPaths,
            _settings.AddSequenceWhenPdfExists,
            SelectedOutputExtension,
            createDirectory: false);
    }

    private string GetDefaultOutputDirectory() => GetSelectedCadDirectory();

    private void ChooseOutputDirectory()
    {

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
        Description = "Выберите папку вывода",
            SelectedPath = _outputDirectory.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputDirectory.Text = dialog.SelectedPath;
            _outputDirectoryIsCustom = true;
            SaveCurrentSettings();
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "Папка вывода изменена: " + dialog.SelectedPath);
        }
    }

    private string GetSelectedCadDirectory()
    {
        var selectedFile = _selectedDwgFiles.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(selectedFile))
        {
            return Path.GetDirectoryName(selectedFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var firstJobFile = _jobs
            .Select(x => x.SourceFile)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
        if (!string.IsNullOrWhiteSpace(firstJobFile))
        {
            return Path.GetDirectoryName(firstJobFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var file = _currentDocument.Database.Filename;
        return string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void ApplyManuallyEnteredOutputDirectory()
    {
        if (!_outputDirectoryModified)
        {
            return;
        }

        _outputDirectoryModified = false;
        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            _outputDirectoryIsCustom = false;
            UpdateAutomaticOutputDirectory();
        }
        else
        {
            _outputDirectoryIsCustom = true;
            _outputDirectory.Text = directory;
        }

        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        AppendLog("INFO", "Папка вывода изменена: " + directory);
    }

    private void GenerateDrawingDirectory()
    {
        if (_jobs.Count == 0)
        {
        System.Windows.MessageBox.Show("Нет списка листов для создания ведомости.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ok = false;
        var message = "";
        CadWindowFocus.HideForCadInput(this);
        try
        {
            ok = DirectoryTableGenerator.PromptAndGenerate(_currentDocument, _jobs.ToList(), _settings, out message);
            AppendLog(ok ? "INFO" : "WARN", message);
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }


        System.Windows.MessageBox.Show(message, "Пакетная печать", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void SplitSelectedDwgs()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selectedJobs = _jobs.Where(x => x.Selected).ToList();
        if (selectedJobs.Count == 0)
        {
        System.Windows.MessageBox.Show("Сначала отметьте листы для разделения.", "Разделение DWG", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Будет создано отдельных файлов DWG: {selectedJobs.Count}.\n\nМодель: создаётся облегчённый DWG только с объектами внутри рамки или пересекающими её.\nЛист: модель не изменяется, сохраняется только текущий лист и его содержимое.\n\nПапка вывода: {GetOutputLocationDescription()}.\n\nПродолжить?",
        "Разделение DWG",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        Cursor = Cursors.Wait;
        IsEnabled = false;
        try
        {
            SaveCurrentSettings();
            AppendLog("INFO", $"Начато разделение на DWG; листов: {selectedJobs.Count}.");
            var results = DwgSplitService.SplitMany(
                selectedJobs,
                _currentDocument,
                _settings,
                job => AppendLog("INFO", $"Создание DWG: {job.DrawingNumber}_{job.Title}"),
                customOutputDirectory: CustomOutputDirectory,
                sourceSubfolder: AutomaticOutputSubfolder,
                explicitOutputPaths: selectedJobs.ToDictionary(job => job, job => job.OutputPath));

            var success = results.Count(x => x.Error == null);
            var failed = results.Count - success;
            foreach (var result in results)
            {
                if (result.Error == null)
                {
                    result.Job.OutputPath = result.OutputPath;
                    var actionText = result.Job.IsPaperSpace ? "удалено" : "пропущено";
                    AppendLog("INFO", $"DWG создан: {result.OutputPath}; сохранено объектов: {result.KeptEntities}; {actionText}: {result.RemovedEntities}; сохранено объектов с неизвестными границами: {result.UnknownExtentsKept}.");
                }
                else
                {
                    AppendLog("ERROR", $"Не удалось создать DWG {result.Job.DrawingNumber}_{result.Job.Title}: {result.Error.Message}");
                }
            }

            _grid.Items.Refresh();

            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            RefreshStatus();

            var logText = PrintLogMessageText(_lastLogPath);
            var failedText = failed == 0
                ? ""
                : "\n\nОшибки:\n" + string.Join("\n", results.Where(x => x.Error != null).Take(20).Select(x => $"{x.Job.DrawingNumber}_{x.Job.Title}: {x.Error!.Message}"));
            System.Windows.MessageBox.Show(
                $"Разделение завершено: успешно — {success}, с ошибкой — {failed}.{logText}{failedText}",
                "Разделение DWG",
                MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (success > 0 && _settings.OpenOutputDirectoryAfterBatchPrint)
            {
                var firstOk = results.FirstOrDefault(x => x.Error == null)?.OutputPath;
                OpenOutputDirectoryAfterPrint(firstOk);
            }
        }
        finally
        {
            IsEnabled = true;
            Cursor = Cursors.Arrow;
        }
    }

    private void ManageLibrary()
    {
        var form = new TitleBlockLibraryManagerForm();
        CadDialog.ShowModal(form);
        if (form.LibraryChanged)
        {
            ScanCurrentDrawing();
        }
    }

    private void ShowSettings()
    {
        ShowSettingsAtTab(SettingsForm.InitialTabIndex);
    }

    private void ShowSettingsAtTab(int tabIndex)
    {
        while (true)
        {
            SettingsForm.InitialTabIndex = tabIndex;

            var form = new SettingsForm();
            if (CadDialog.ShowModal(form) != true)
            {
                return;
            }

            ReloadSettings();
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "Настройки обновлены.");

            if (!form.RequestPickDirectoryRowHeight
                && !form.RequestPickDirectoryTextAppearance
                && !form.RequestPickScaleFromCad
                && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                tabIndex = form.SelectedTabIndex;
                return;
            }

            tabIndex = form.SelectedTabIndex;
            if (form.RequestPickScaleFromCad)
            {
                PickScaleSettingFromCad();
            }
            else
            {
                PickDirectorySettingFromCad(
                    form.RequestPickDirectoryRowHeight,
                    form.RequestPickDirectoryTextAppearance,
                    form.RequestedDirectoryColumnKey);
            }
        }
    }

    private void ShowSortSettings()
    {
        var dialog = new SortOrderDialog(
            _settings.SortOrderHorizontalFirst,
            showSortBasis: true,
            sortMode: _settings.TitleBlockBatchSortMode);
        if (CadDialog.ShowModal(dialog) != true) return;

        _settings.TitleBlockBatchSortMode = dialog.SortMode;
        _settings.SortOrderHorizontalFirst = dialog.HorizontalFirst;
        AppSettingsStore.Save(_settings);


        SortAndRefreshOutputPaths();
        var modeName = _settings.TitleBlockBatchSortMode == TitleBlockSortMode.Spatial
            ? "по расположению листов"
            : "по номеру листа (совпадения — по расположению)";
        var orderName = _settings.SortOrderHorizontalFirst ? "слева направо, сверху вниз" : "сверху вниз, слева направо";
        AppendLog("INFO", $"Рамки отсортированы: {modeName}; {orderName}.");
    }

    private void PickDirectorySettingFromCad(
        bool pickRowHeight,
        bool pickTextAppearance,
        string? columnKey)
    {
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var document = GetActiveCadDocument();
            if (document == null)
            {
                const string noDocumentMessage = "Нет доступного чертежа CAD. Откройте чертёж и повторите попытку.";
                AppendLog("WARN", noDocumentMessage);
                System.Windows.MessageBox.Show(noDocumentMessage, "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var settings = AppSettingsStore.Load();
            bool ok;
            string message;
            if (pickTextAppearance)
            {
                ok = DirectoryTableGenerator.PromptTextAppearance(document, settings, out _, out message);
            }
            else if (pickRowHeight)
            {
                ok = DirectoryTableGenerator.PromptRowHeight(document, settings, out _, out message);
            }
            else
            {
                ok = DirectoryTableGenerator.PromptColumnSize(
                    document,
                    settings,
                    columnKey ?? "",
                    out _,
                    out message);
            }
            ReloadSettings();
            AppendLog(ok ? "INFO" : "WARN", message);

            if (!ok)
            {
                System.Windows.MessageBox.Show(message, "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }


    private void PickScaleSettingFromCad()
    {
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var document = GetActiveCadDocument();
            if (document == null)
            {
                const string noDocumentMessage = "Нет доступного чертежа CAD. Откройте чертёж и повторите попытку.";
                AppendLog("WARN", noDocumentMessage);
                System.Windows.MessageBox.Show(noDocumentMessage, "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var settings = AppSettingsStore.Load();
            var ok = ScaleSettingsPicker.PromptScaleFromFrame(document, settings, out _, out var message);
            ReloadSettings();
            AppendLog(ok ? "INFO" : "WARN", message);
            if (!ok)
            {
                System.Windows.MessageBox.Show(message, "Настройки пакетной печати", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private static Document? GetActiveCadDocument()
    {
        try
        {
            return CadApp.DocumentManager.MdiActiveDocument;
        }
        catch
        {
            return null;
        }
    }

    private void ReloadSettings()
    {
        var updated = AppSettingsStore.Load();
        _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
        _settings.HideFrameBoundaryWhenPlotting = updated.HideFrameBoundaryWhenPlotting;
        _settings.PlotTransparency = updated.PlotTransparency;
        _settings.PlotObjectLineweights = updated.PlotObjectLineweights;
        _settings.AddSequenceWhenPdfExists = updated.AddSequenceWhenPdfExists;
        _settings.MergePdf = updated.MergePdf;
        _settings.UseFileNameAsPdfBookmark = updated.UseFileNameAsPdfBookmark;
        _settings.UseSheetTitleAsPdfBookmark = updated.UseSheetTitleAsPdfBookmark;
        _settings.MergePdfByPaperSize = updated.MergePdfByPaperSize;
        _settings.OpenOutputDirectoryAfterBatchPrint = updated.OpenOutputDirectoryAfterBatchPrint;
        _settings.OpenMergedPdfAfterMerge = updated.OpenMergedPdfAfterMerge;
        _settings.GeneratePrintLog = updated.GeneratePrintLog;
        _settings.ConvertTextToGeometryWhenPlotting = updated.ConvertTextToGeometryWhenPlotting;
        _settings.PdfFileNamePattern = updated.PdfFileNamePattern;
        _settings.PdfFileNameSeparator = updated.PdfFileNameSeparator;
        _settings.PdfFileNameFields = updated.PdfFileNameFields.ToList();
        _settings.FileNameSequenceDigits = updated.FileNameSequenceDigits;
        _settings.AutoFileNameSequenceDigits = updated.AutoFileNameSequenceDigits;
        _settings.FileNameSequenceStartNumber = updated.FileNameSequenceStartNumber;
        _settings.OpenExternalDwgForPlot = updated.OpenExternalDwgForPlot;
        _settings.DirectoryIndexWidth = updated.DirectoryIndexWidth;
        _settings.DirectoryNumberWidth = updated.DirectoryNumberWidth;
        _settings.DirectoryTitleWidth = updated.DirectoryTitleWidth;
        _settings.DirectoryPaperWidth = updated.DirectoryPaperWidth;
        _settings.DirectoryRemarkWidth = updated.DirectoryRemarkWidth;
        _settings.DirectoryRowHeight = updated.DirectoryRowHeight;
        _settings.DirectoryTextHeightRatio = updated.DirectoryTextHeightRatio;
        _settings.DirectoryTextStyleName = updated.DirectoryTextStyleName;
        _settings.DirectoryColorIndex = updated.DirectoryColorIndex;
        _settings.DirectoryTextHeight = updated.DirectoryTextHeight;
        _settings.DirectoryTextWidthFactor = updated.DirectoryTextWidthFactor;
        _settings.DirectoryLayerName = updated.DirectoryLayerName;
        _settings.DirectoryDrawHeader = updated.DirectoryDrawHeader;
        _settings.DirectoryDrawGridLines = updated.DirectoryDrawGridLines;
        _settings.DirectoryColumns = updated.DirectoryColumns.Select(x => x.Clone()).ToList();
        _settings.LongPaperNameFormat = updated.LongPaperNameFormat;
        _settings.LongPaperSnapToleranceMm = updated.LongPaperSnapToleranceMm;
        _settings.TitleBlockBatchSortMode = updated.TitleBlockBatchSortMode;
        _settings.SortOrderHorizontalFirst = updated.SortOrderHorizontalFirst;
        _settings.LastStyleSheet = updated.LastStyleSheet;
        _mergePdfCheckBox.IsChecked = updated.MergePdf;
    }

    private void ImportLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Библиотека рамок (*.json)|*.json",
            Title = "Импорт библиотеки рамок"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var library = TitleBlockLibraryStore.Load(dialog.FileName);
        TitleBlockLibraryStore.Save(library);
        System.Windows.MessageBox.Show("Библиотека рамок импортирована.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportLibrary()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Библиотека рамок (*.json)|*.json",
            FileName = "TitleBlockLibrary.json",
            Title = "Экспорт библиотеки рамок"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        TitleBlockLibraryStore.Save(TitleBlockLibraryStore.Load(), dialog.FileName);
        System.Windows.MessageBox.Show("Библиотека рамок экспортирована.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PrintSelectedJobs()
    {
        var selected = _jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
        System.Windows.MessageBox.Show("Не отмечено ни одного листа.", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var device = SelectedPlotDevice;
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
        System.Windows.MessageBox.Show($"Не найдено устройство вывода {SelectedOutputFormat}.{(IsDwfOutput ? "" : AcadPlotterInstaller.PdfDeviceNotFoundHint)}", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveCurrentSettings();
        SortAndRefreshOutputPaths();


        _mergedOutputPath = IsPdfOutput && _mergePdfCheckBox.IsChecked == true
            ? GetAutomaticMergedOutputPath(selected)
            : "";
        foreach (var directory in selected
                     .Select(job => Path.GetDirectoryName(job.OutputPath))
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(directory!);
        }
        ApplyLeaveMarginSelection(selected);

        ExecutePendingPrint();
    }

    private void PrintOrStop()
    {
        if (_printCts != null)
        {
            PrintSessionLog.Info("Нажата кнопка «Остановить».");
            _printCts.Cancel();
            return;
        }

        // Issue #9: every "Начать печать" starts a new %TEMP% log, and nothing may escape the click handler.
        using var session = PrintSessionScope.Begin(Dispatcher, "пакетная печать (" + SelectedOutputFormat + ")");
        try
        {
            LogPrintRequest();
            if (IsDwgOutput)
            {
                SplitSelectedDwgs();
                return;
            }

            PrintSelectedJobs();
        }
        catch (Exception ex)
        {
            session.Result = "печать прервана исключением: " + ex.Message;
            PrintSessionLog.Error("Печать прервана", ex);
            System.Windows.MessageBox.Show(
                "Ошибка печати: " + ex.Message + PrintLogMessageText(""),
                "Пакетная печать",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>Что именно запросил пользователь: попадает в начало журнала печати.</summary>
    private void LogPrintRequest()
    {
        PrintSessionLog.Info($"Окно: пакетная печать; формат={SelectedOutputFormat}; устройство={SelectedPlotDevice}; стиль={_styleCombo.SelectedItem}; "
            + $"папка={_outputDirectory.Text} (своя={_outputDirectoryIsCustom}); объединять PDF={_mergePdfCheckBox.IsChecked}; "
            + $"поля={_leaveMarginCheckBox.IsChecked} {_marginInput.Text}");
        PrintSessionLog.Info($"Чертёж: {_currentDocument?.Name}; листов: {_jobs.Count}; выбрано: {_jobs.Count(x => x.Selected)}");
        PrintSessionLog.Info("Настройки: " + PrintSessionLog.DescribeSettings(_settings));
    }

    /// <summary>"\nЖурнал: путь" for the result message: the %TEMP% log and, if enabled, the saved run log.</summary>
    private static string PrintLogMessageText(string savedLogPath)
    {
        var text = "\nЖурнал печати: " + PrintSessionLog.LastPath;
        return string.IsNullOrWhiteSpace(savedLogPath) ? text : text + "\nЖурнал: " + savedLogPath;
    }

    private bool IsDwgOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "DWG",
        StringComparison.OrdinalIgnoreCase);

    private bool IsPdfOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "PDF",
        StringComparison.OrdinalIgnoreCase);


    private bool SupportsLeaveMargin => !IsDwgOutput;

    private bool IsDwfOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "DWF",
        StringComparison.OrdinalIgnoreCase);

    private string SelectedOutputFormat => _outputFormatCombo.SelectedItem?.ToString()?.Trim() ?? "";

    private string SelectedOutputExtension => "." + SelectedOutputFormat.ToLowerInvariant();

    private string SelectedPlotDevice => IsDwfOutput
        ? _dwfPlotDevice
        : _pdfPlotDevice;

    private string? AutomaticOutputSubfolder => _savePathModeCombo.SelectedIndex == 1
                                                   && !string.IsNullOrWhiteSpace(SelectedOutputFormat)
        ? FileNameSanitizer.Clean(SelectedOutputFormat)
        : null;

    private string? CustomOutputDirectory => _outputDirectoryIsCustom
        ? _outputDirectory.Text.Trim()
        : null;

    private void RefreshSavePathModeOptions(bool preserveSelection)
    {
        var selectedIndex = preserveSelection && _savePathModeCombo.SelectedIndex >= 0
            ? Math.Min(_savePathModeCombo.SelectedIndex, 1)
            : 0;
        var format = SelectedOutputFormat;
        var formatPathText = string.IsNullOrWhiteSpace(format)
            ? "Папка исходного файла/формат вывода"
            : "Папка исходного файла/" + format;

        _savePathModeCombo.Items.Clear();
        _savePathModeCombo.Items.Add("Папка исходного файла");
        _savePathModeCombo.Items.Add(formatPathText);
        _savePathModeCombo.SelectedIndex = selectedIndex;
    }

    private void ApplySelectedSavePathMode()
    {
        _outputDirectoryIsCustom = false;
        UpdateAutomaticOutputDirectory();
        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        AppendLog("INFO", "Режим сохранения изменён: " + _savePathModeCombo.SelectedItem);
    }

    private void UpdateAutomaticOutputDirectory()
    {
        _outputDirectory.Text = AppendAutomaticSubfolder(GetSelectedCadDirectory());
    }

    private string AppendAutomaticSubfolder(string sourceDirectory)
    {
        var subfolder = AutomaticOutputSubfolder;
        return string.IsNullOrWhiteSpace(subfolder)
            ? sourceDirectory
            : Path.Combine(sourceDirectory, subfolder);
    }

    private string GetOutputDirectory(PlotJob job)
    {
        if (_outputDirectoryIsCustom)
        {
            return _outputDirectory.Text.Trim();
        }

        var sourceFile = !string.IsNullOrWhiteSpace(job.SourceFile) && File.Exists(job.SourceFile)
            ? job.SourceFile
            : _currentDocument.Database.Filename;
        var sourceDirectory = string.IsNullOrWhiteSpace(sourceFile)
            ? GetSelectedCadDirectory()
            : Path.GetDirectoryName(sourceFile) ?? GetSelectedCadDirectory();
        return AppendAutomaticSubfolder(sourceDirectory);
    }

    private string GetOutputLocationDescription()
    {
        if (_outputDirectoryIsCustom)
        {
            return _outputDirectory.Text.Trim();
        }

        var subfolder = AutomaticOutputSubfolder;
        return string.IsNullOrWhiteSpace(subfolder)
            ? "папка каждого исходного DWG"
            : $"подпапка {subfolder} рядом с каждым исходным DWG";
    }

    private void UpdateOutputFormatUi()
    {
        RefreshSavePathModeOptions(preserveSelection: true);
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var plotOutput = !IsDwgOutput;
        foreach (var control in _plotOnlyControls)
        {
            control.IsEnabled = plotOutput;
        }
        _styleSettingsButton.IsEnabled = plotOutput && _styleCombo.SelectedIndex >= 0;
        // DWF is printed with the DWF plotter of nanoCAD; PNG and JPG are made from the PDF of the chosen printer.
        _printerCombo.IsEnabled = plotOutput && !IsDwfOutput;
        _mergePdfCheckBox.IsEnabled = IsPdfOutput;

        _leaveMarginCheckBox.IsEnabled = SupportsLeaveMargin;
        _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;

        _outputNameColumn.Header = "Имя " + SelectedOutputFormat;

        SortAndRefreshOutputPaths();
    }

    private void ExecutePendingPrint()
    {
        var selected = _jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var device = SelectedPlotDevice;
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        var mergePdf = !string.IsNullOrWhiteSpace(_mergedOutputPath);
        var originalOutputPaths = selected.ToDictionary(job => job, job => job.OutputPath);
        string? temporaryDirectory = null;
        var mergedSuccessfully = false;
        var mergedOutputPaths = new List<string>();
        var completed = 0;



        _printCts = new CancellationTokenSource();
        _printButton.Content = "Остановить";
        _printButton.Background = new SolidColorBrush(Color.FromRgb(200, 40, 40));
        _printButton.BorderBrush = new SolidColorBrush(Color.FromRgb(160, 30, 30));
        _printButton.IsEnabled = true;
        var wasTopmost = Topmost;
        BatchPlotHostProgress.Begin();
        BatchPrintProgressSession? progress = null;
        PrintSessionLog.Info($"Печать: листов {selected.Count}; устройство={device}; стиль={style}; объединение PDF={mergePdf} ({_mergedOutputPath})");
        foreach (var job in selected)
        {
            PrintSessionLog.Info("Лист: " + PrintSessionLog.DescribeJob(job));
        }

        try
        {

            Visibility = System.Windows.Visibility.Visible;
            Topmost = true;
            Activate();
            progress = BatchPrintProgressSession.Start(
                this,
                selected.Count,
                () => _printCts?.Cancel());

            void PumpMessages()
            {

                Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            }

            var failed = new List<string>();
            PrepareCustomPaperRegistrations(selected, device);
            if (mergePdf && !_settings.KeepIndividualPdfsWhenMerging)
            {
                temporaryDirectory = CreateTemporaryPdfDirectory("Merge");
                for (var i = 0; i < selected.Count; i++)
                {
                    selected[i].OutputPath = Path.Combine(
                        temporaryDirectory,
                        (i + 1).ToString("D5") + ".pdf");
                }
            }

        _statusLabel.Text = $"Печать... 0 / {selected.Count}";
        progress.Report(0, selected.Count, "Подготовка пользовательских форматов и путей вывода…");
            PumpMessages();

            var results = PdfRasterExport.PlotMany(
                selected,
                device,
                style,
                _currentDocument,
                _settings,
                job =>
                {
                    completed++;
        _statusLabel.Text = $"Печать... {completed} / {selected.Count}";
                    var detail =
                        $"Лист {completed} из {selected.Count}\n" +
                        $"{job.DrawingNumber}_{job.Title}\n" +
                        $"Пространство: {job.SpaceName}\n" +
                        $"Выходной файл: {System.IO.Path.GetFileName(job.OutputPath)}";
                    progress.Report(completed, selected.Count, detail);
                    AppendLog(
                        "INFO",
                        $"Начало печати {job.DrawingNumber}_{job.Title}; источник={job.SourceFile}; пространство={job.SpaceName}; файл={job.OutputPath}");
                    Visibility = System.Windows.Visibility.Visible;
                    Activate();
                    PumpMessages();
                },
                _printCts.Token);

            foreach (var result in results)
            {
                var job = result.Job;
                if (result.Succeeded)
                {
                    AppendLog("INFO", $"Печать завершена: {job.OutputPath}");
                    continue;
                }

                var ex = result.Error!;
                var message = $"{job.DrawingNumber}_{job.Title}: {ex.Message}";
                failed.Add(message);
                PrintSessionLog.Error($"Лист {job.DrawingNumber}_{job.Title}", ex);
                AppendLog("ERROR", ex.ToString());
                AppendLog("ERROR", "Ошибка печати: " + message);
            }

            foreach (var skipped in selected.Except(results.Select(x => x.Job)))
            {
                var message = $"{skipped.DrawingNumber}_{skipped.Title}: файл не открыт, печать не начата.";
                failed.Add(message);
                AppendLog("ERROR", message);
            }

            var printed = results.Count(x => x.Succeeded);
            if (mergePdf && failed.Count == 0 && printed == selected.Count)
            {
                try
                {
        _statusLabel.Text = "Объединение PDF...";
        progress?.SetPhase("Объединение PDF…", "Подождите: окно хода работы закроется автоматически.");
                    PumpMessages();
                    var mergeInputs = selected.Select(job => new PdfMergeInput(
                        job.OutputPath,
                        _settings.UseSheetTitleAsPdfBookmark && !string.IsNullOrWhiteSpace(job.Title)
                            ? job.Title.Trim()
                            : Path.GetFileNameWithoutExtension(originalOutputPaths[job]),
                        OutputPaperNameResolver.Resolve(
                            job,
                            _settings.LongPaperSnapToleranceMm),
                        job.PaperWidthMm,
                        job.PaperHeightMm)).ToList();
                    var mergePlans = PdfDocumentService.PlanMerges(
                        mergeInputs,
                        _mergedOutputPath,
                        _settings.MergePdfByPaperSize,
                        _settings.AddSequenceWhenPdfExists);
                    foreach (var mergePlan in mergePlans)
                    {
                        PdfDocumentService.Merge(
                            mergePlan.Inputs,
                            mergePlan.OutputPath,
                            _settings.UseFileNameAsPdfBookmark || _settings.UseSheetTitleAsPdfBookmark);
                        mergedOutputPaths.Add(mergePlan.OutputPath);
                        AppendLog("INFO", $"PDF объединён: {mergePlan.OutputPath}");
                    }
                    mergedSuccessfully = true;
                }
                catch (Exception ex)
                {
                    var message = "Не удалось объединить PDF: " + ex.Message;
                    failed.Add(message);
                    AppendLog("ERROR", ex.ToString());
                }
            }

            var printLogPath = SavePrintLogIfEnabled();
            var printLogText = PrintLogMessageText(printLogPath);
        _statusLabel.Text = $"Готово: {printed} лист(ов)";
            var mergedFilesText = string.Join("\n", mergedOutputPaths);
            var summary = mergePdf
                ? mergedSuccessfully
                    ? $"Печать и объединение завершены: листов — {printed}, файлов PDF — {mergedOutputPaths.Count}.\nОбъединённые файлы:\n{mergedFilesText}{printLogText}"
                    : $"Печать завершена, но объединены не все PDF.\nУспешно: {printed}; ошибок: {failed.Count}; объединённых PDF: {mergedOutputPaths.Count}.{printLogText}"
                : $"Печать завершена: успешно — {printed}, с ошибкой — {failed.Count}.{printLogText}";
            if (failed.Count > 0)
            {
                summary += "\n\nОшибки:\n" + string.Join("\n", failed);
            }

        System.Windows.MessageBox.Show(summary, "Пакетная печать", MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            if (!mergePdf && printed > 0 && _settings.OpenOutputDirectoryAfterBatchPrint)
            {
                OpenOutputDirectoryAfterPrint();
            }
            else if (mergePdf && mergedSuccessfully && _settings.OpenMergedPdfAfterMerge)
            {
                OpenMergedPdfFiles(mergedOutputPaths);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = $"Остановлено (готово {completed} из {selected.Count})";
            AppendLog("INFO", $"Печать отменена пользователем; готово {completed} из {selected.Count}");
            var printLogPath = SavePrintLogIfEnabled();
            var printLogText = PrintLogMessageText(printLogPath);
            System.Windows.MessageBox.Show($"Печать остановлена.\nГотово {completed} из {selected.Count}.{printLogText}", "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
        _statusLabel.Text = "Ошибка печати";
            PrintSessionLog.Error("Печать прервана", ex);
            AppendLog("ERROR", ex.ToString());
            var printLogPath = SavePrintLogIfEnabled();
            var printLogText = PrintLogMessageText(printLogPath);
            System.Windows.MessageBox.Show("Ошибка печати: " + ex.Message + printLogText, "Пакетная печать", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Every cleanup step runs even if a previous one fails: an exception here would close the CAD (issue #9).
            RunPrintCleanup("Восстановление путей вывода", () =>
            {
                foreach (var pair in originalOutputPaths)
                {
                    pair.Key.OutputPath = pair.Value;
                }
            });

            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                RunPrintCleanup("Удаление временной папки", () => TryDeleteDirectory(temporaryDirectory));
            }


            _printCts?.Dispose();
            _printCts = null;
            RunPrintCleanup("Сброс кнопки печати", () =>
            {
        _printButton.Content = "Начать печать";
                _printButton.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                _printButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 95, 170));
                Topmost = wasTopmost;
            });
            RunPrintCleanup("Закрытие окна хода печати", () => progress?.Dispose());
            progress = null;
            BatchPlotHostProgress.End();

            RunPrintCleanup("Обновление состояния", RefreshStatus);

            RunPrintCleanup("Удаление временных номеров", () => ClearSequenceOverlay());
        }
    }

    private static void RunPrintCleanup(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            PrintSessionLog.Error(step, ex);
        }
    }

    private void OpenOutputDirectoryAfterPrint(string? outputFile = null)
    {
        var directory = !string.IsNullOrWhiteSpace(outputFile)
            ? Path.GetDirectoryName(outputFile) ?? ""
            : _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(outputFile) && File.Exists(outputFile))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + Path.GetFullPath(outputFile) + "\"",
                    UseShellExecute = true
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + Path.GetFullPath(directory) + "\"",
                WorkingDirectory = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog("WARN", "Не удалось открыть папку вывода: " + ex.Message);
        }
    }

    private void OpenMergedPdfFiles(IEnumerable<string> outputFiles)
    {
        foreach (var outputFile in outputFiles.Where(File.Exists))
        {
            try
            {

                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(outputFile),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "Не удалось открыть объединённый PDF: " + ex.Message);
            }
        }
    }

    private void ApplyLeaveMarginSelection(IEnumerable<PlotJob> jobs)
    {

        var leaveMargin = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;
        var marginMm = ReadMarginValue(_marginInput);
        foreach (var job in jobs)
        {

            job.LeavePaperMargin = leaveMargin;
            job.PaperMarginMm = marginMm;

            job.RequiresCustomPaperRegistration =
                job.DetectedRequiresCustomPaperRegistration || (leaveMargin && marginMm > 0);
            if (!leaveMargin || marginMm <= 0)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
            }
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PlotJob job)
        {
            PreviewJob(job);
        }
    }

    private void PreviewJob(PlotJob job)
    {

        var device = SelectedPlotDevice;
        var style = PlotStyleManager.ResolveJobStyle(job, _styleCombo.SelectedItem?.ToString() ?? "");
        if (string.IsNullOrWhiteSpace(device))
        {
        System.Windows.MessageBox.Show("Выберите устройство печати.", "Предпросмотр печати", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        job.LeavePaperMargin = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;
        job.PaperMarginMm = ReadMarginValue(_marginInput);
        var wasVisible = IsVisible;


        CadWindowFocus.HideForCadInput(this);
        try
        {

            var previewJobs = _jobs
                .Where(candidate => candidate.Selected || ReferenceEquals(candidate, job))
                .ToList();

            ApplyLeaveMarginSelection(previewJobs);
            PrepareCustomPaperRegistrations(previewJobs, device);
            AppendLog("INFO", $"Предпросмотр в CAD: {job.DrawingNumber}_{job.Title}");

            PendingPlotPreview.Start(new PendingPlotPreview.Request
            {
                Job = job,
                DeviceName = device,
                StyleSheet = style,
                Document = _currentDocument,
                OnFinally = () =>
                {
                    if (wasVisible)
                    {
                        CadWindowFocus.RestoreDialog(this);
                    }
                },
                OnError = ex =>
                {
                    AppendLog("ERROR", "Ошибка предварительного просмотра: " + ex);
                    System.Windows.MessageBox.Show(
                        "Ошибка предварительного просмотра: " + ex.Message,
                        "Предварительный просмотр",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }, Dispatcher);
        }
        catch (Exception ex)
        {
            PendingPlotPreview.Take();
            AppendLog("ERROR", "Ошибка предварительного просмотра: " + ex);
            System.Windows.MessageBox.Show("Ошибка предварительного просмотра: " + ex.Message, "Предварительный просмотр", MessageBoxButton.OK, MessageBoxImage.Error);
            if (wasVisible)
            {
                CadWindowFocus.RestoreDialog(this);
            }
        }
    }

    /// <summary>

    /// </summary>
    private void PrepareCustomPaperRegistrations(IReadOnlyList<PlotJob> jobs, string deviceName)
    {
        var result = CustomPaperBatchPreparer.Prepare(jobs, deviceName);
#if ZWCAD



        foreach (var job in jobs.Where(job =>
                     string.Equals(job.PaperName, PaperSizeDetector.CustomPaperName, StringComparison.OrdinalIgnoreCase)))
        {
            job.UseExactWindowScale = false;
        }
#endif
        if (result.Registrations.Count == 0)
        {
            return;
        }

        var sizes = string.Join(", ", result.Registrations.Select(registration =>
            $"{registration.WidthMm:0.######}x{registration.HeightMm:0.######} мм ({(registration.WasAdded ? "добавлено" : "использовано повторно")})"));
        AppendLog("INFO", $"Пользовательские форматы PDF/DWF подготовлены; вариантов: {result.Registrations.Count}: {sizes}");
#if AUTOCAD
        if (!string.IsNullOrWhiteSpace(result.AttachmentMessage))
        {
            AppendLog("INFO", "Обновление пользовательских форматов AutoCAD: " + result.AttachmentMessage);
        }
#endif
    }

    private string GetAutomaticMergedOutputPath(IReadOnlyList<PlotJob> selected)
    {
        var firstJob = selected.FirstOrDefault();
        var directory = firstJob == null ? "" : Path.GetDirectoryName(firstJob.OutputPath) ?? "";
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = firstJob == null ? _outputDirectory.Text.Trim() : GetOutputDirectory(firstJob);
        }

        var source = firstJob?.SourceFile;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = _currentDocument.Database.Filename;
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            source = _currentDocument.Name;
        }

        var baseName = FileNameSanitizer.Clean(Path.GetFileNameWithoutExtension(source));
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Объединённые чертежи";
        }

        return Path.Combine(directory, baseName + ".pdf");
    }

    private static string CreateTemporaryPdfDirectory(string purpose)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ZwcadBatchPlot",
            purpose,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string? directory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
        }
    }

    private void AppendLog(string level, string message)
    {
        PrintSessionLog.Write(level, message);
        if (!_settings.GeneratePrintLog)
        {
            return;
        }

        _logLines.Add(BatchPlotLogger.Format(level, message));
    }

    /// <summary>


    /// </summary>
    private string SavePrintLogIfEnabled()
    {
        _lastLogPath = "";
        if (!_settings.GeneratePrintLog)
        {
            return "";
        }

        _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
        return _lastLogPath;
    }

    private string PdfDeviceHint => string.IsNullOrWhiteSpace(_pdfPlotDevice)
        ? "PDF-плоттер не найден"
        : "PDF через " + Path.GetFileNameWithoutExtension(_pdfPlotDevice);

    private void RefreshStatus()
    {
        var selected = _jobs.Count(x => x.Selected);
        var formatHint = IsDwgOutput
            ? "Разделение DWG"
            : IsPdfOutput ? PdfDeviceHint : $"Отдельные файлы {SelectedOutputFormat}";
        var outputHint = $"{formatHint}; сохранение: {GetOutputLocationDescription()}.";
        _statusLabel.Text = $"Всего листов: {_jobs.Count}; выбрано: {selected}. {outputHint} Библиотека форматов: {TitleBlockLibraryStore.DefaultPath}";
    }

    private void SaveCurrentSettings()
    {
        _settings.LastPlotDevice = _pdfPlotDevice;
        var style = PlotStyleManager.NormalizeStyleName(_styleCombo.SelectedItem?.ToString());
        if (!string.IsNullOrEmpty(style))
        {
            _settings.LastStyleSheet = style;
        }
        _settings.MergePdf = _mergePdfCheckBox.IsChecked == true;
        _settings.LeavePaperMargin = _leaveMarginCheckBox.IsChecked == true;
        _settings.PaperMarginMm = ReadMarginValue(_marginInput);
        AppSettingsStore.Save(_settings);
    }



    private void ScanCurrentDrawing_Click(object sender, RoutedEventArgs e) => ScanCurrentDrawing();

    private void ScanSelectedObjects_Click(object sender, RoutedEventArgs e) => ScanSelectedObjects();

    private void ConfigureTextRegions_Click(object sender, RoutedEventArgs e)
    {
        var job = _grid.SelectedItems.OfType<PlotJob>().FirstOrDefault()
                  ?? _grid.SelectedItem as PlotJob;
        if (job == null)
        {
            System.Windows.MessageBox.Show(
                "Сначала выберите строку с рамкой, для которой нужно задать области текста.",
                "Поля текста",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (job.FrameSource == FrameSourceKind.Manual)
        {
            System.Windows.MessageBox.Show(
                "Для ручной области печати резервные поля текста не настраиваются. Выберите распознанную рамку.",
                "Поля текста",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!IsJobFromCurrentDocument(job))
        {
            System.Windows.MessageBox.Show(
                "Выбранная рамка находится не в текущем открытом DWG. Откройте этот файл и настройте области по рамке в нём.",
                "Поля текста",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var existing = FrameTextRegionService.FindProfile(_settings, job.FrameSource, job.BlockName);
        if (existing != null)
        {
            var action = System.Windows.MessageBox.Show(
                "Для этого типа рамки области текста уже заданы.\n\n" +
                "Да — выбрать области заново.\n" +
                "Нет — удалить резервные области.\n" +
                "Отмена — ничего не менять.",
                "Поля текста",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (action == MessageBoxResult.Cancel)
                return;

            if (action == MessageBoxResult.No)
            {
                if (FrameTextRegionService.RemoveProfile(_settings, job.FrameSource, job.BlockName))
                {
                    AppSettingsStore.Save(_settings);
                    AppendLog("INFO", $"Резервные области текста удалены: {GetTextRegionProfileLabel(job)}.");
                    System.Windows.MessageBox.Show(
                        "Резервные области текста удалены.",
                        "Поля текста",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return;
            }
        }

        if (job.CornerPoints is not { Length: >= 8 })
        {
            System.Windows.MessageBox.Show(
                "У выбранной рамки нет исходных углов в координатах чертежа. Пересканируйте чертёж и повторите.",
                "Поля текста",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var frameWorld = FrameTextRegionService.FromJobWorldBounds(job);
        LocalRectangle titleRegion;
        LocalRectangle numberRegion;

        // Толстая рамка overlay всегда находится под объектами чертежа,
        // поэтому при выборе областей её можно оставить без изменения.
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var editor = _currentDocument.Editor;
            // Editor.GetPoint возвращает точки в текущей ПСК, а рамка задана в МСК:
            // переводим области в МСК, иначе при повёрнутой/смещённой ПСК смещения
            // от правого нижнего угла рамки сохраняются неверно (issue #17).
            var ucsToWorld = GetCurrentUcsToWorld(editor);
            if (!BatchPlotCommands.TryGetRegion(
                    editor,
                    "\nУкажите первый угол области НАИМЕНОВАНИЯ листа: ",
                    "\nУкажите противоположный угол области НАИМЕНОВАНИЯ листа: ",
                    Matrix3d.Identity,
                    out titleRegion))
            {
                return;
            }

            if (!BatchPlotCommands.TryGetRegion(
                    editor,
                    "\nУкажите первый угол области НОМЕРА листа: ",
                    "\nУкажите противоположный угол области НОМЕРА листа: ",
                    Matrix3d.Identity,
                    out numberRegion))
            {
                return;
            }

            titleRegion = UcsRegionToWorld(titleRegion, ucsToWorld);
            numberRegion = UcsRegionToWorld(numberRegion, ucsToWorld);
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }

        if (!FrameTextRegionService.TryCreateProfile(
                job,
                frameWorld,
                titleRegion,
                numberRegion,
                out var profile,
                out var error))
        {
            System.Windows.MessageBox.Show(error, "Поля текста", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FrameTextRegionService.UpsertProfile(_settings, profile);
        AppSettingsStore.Save(_settings);
        AppendLog("INFO", $"Резервные области текста сохранены: {GetTextRegionProfileLabel(job)}.");

        System.Windows.MessageBox.Show(
            "Области сохранены.\n\n" +
            "Положение областей отсчитывается от ПРАВОГО НИЖНЕГО угла рамки (там находится штамп) " +
            "в мм бумаги, поэтому одна настройка подходит для листов любого формата и ориентации.\n\n" +
            "При следующем сканировании они будут использоваться только как резерв:\n" +
            "• если штатный номер листа пуст — берётся TEXT/MTEXT из области номера;\n" +
            "• если штатное наименование пусто — берётся TEXT/MTEXT из области наименования.",
            "Поля текста",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static Matrix3d GetCurrentUcsToWorld(Editor editor)
    {
        try
        {
            return editor.CurrentUserCoordinateSystem;
        }
        catch
        {
            return Matrix3d.Identity;
        }
    }

    /// <summary>Прямоугольник, указанный в ПСК, → охватывающий прямоугольник в МСК (по всем четырём углам).</summary>
    private static LocalRectangle UcsRegionToWorld(LocalRectangle ucsRegion, Matrix3d ucsToWorld)
    {
        if (ucsToWorld.IsEqualTo(Matrix3d.Identity))
            return ucsRegion;

        var corners = new[]
        {
            new Point3d(ucsRegion.MinX, ucsRegion.MinY, 0).TransformBy(ucsToWorld),
            new Point3d(ucsRegion.MaxX, ucsRegion.MinY, 0).TransformBy(ucsToWorld),
            new Point3d(ucsRegion.MaxX, ucsRegion.MaxY, 0).TransformBy(ucsToWorld),
            new Point3d(ucsRegion.MinX, ucsRegion.MaxY, 0).TransformBy(ucsToWorld)
        };
        return LocalRectangle.FromPoints(
            corners.Min(point => point.X),
            corners.Min(point => point.Y),
            corners.Max(point => point.X),
            corners.Max(point => point.Y));
    }

    private bool IsJobFromCurrentDocument(PlotJob job)
    {
        var current = string.IsNullOrWhiteSpace(_currentDocument.Database.Filename)
            ? _currentDocument.Name
            : _currentDocument.Database.Filename;

        if (string.IsNullOrWhiteSpace(job.SourceFile) || string.IsNullOrWhiteSpace(current))
            return true;

        try
        {
            if (Path.IsPathRooted(job.SourceFile) && Path.IsPathRooted(current))
            {
                return string.Equals(
                    Path.GetFullPath(job.SourceFile),
                    Path.GetFullPath(current),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
        }

        return string.Equals(
            Path.GetFileName(job.SourceFile),
            Path.GetFileName(current),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTextRegionProfileLabel(PlotJob job)
    {
        return job.FrameSource switch
        {
            FrameSourceKind.Spds => "CSoft СПДС",
            FrameSourceKind.Rectangle => "прямоугольные рамки",
            FrameSourceKind.Library => $"библиотечная рамка {job.BlockName}",
            _ => job.FrameSource.ToString()
        };
    }


    private void AddDwgFiles_Click(object sender, RoutedEventArgs e) => AddDwgFiles();

    private void ClearJobs_Click(object sender, RoutedEventArgs e) => ClearJobs();

    private void RenumberDrawingNumbers_Click(object sender, RoutedEventArgs e) => RenumberDrawingNumbers();

    private void GenerateDrawingDirectory_Click(object sender, RoutedEventArgs e) => GenerateDrawingDirectory();

    private void ChooseOutputDirectory_Click(object sender, RoutedEventArgs e) => ChooseOutputDirectory();

    private void PrintOrStop_Click(object sender, RoutedEventArgs e) => PrintOrStop();

    private void StyleSettings_Click(object sender, RoutedEventArgs e)
        => PlotStyleManager.EditSelectedStyle(this, _styleCombo.SelectedItem?.ToString());

    private void SortSettings_Click(object sender, RoutedEventArgs e) => ShowSortSettings();

    private void FileNameSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(1);

    private void DirectorySettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(2);

    private void GeneralSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(0);

    private void OutputDirectory_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_uiReady)
        {
            _outputDirectoryModified = true;
        }
    }

    private void OutputDirectory_LostFocus(object sender, RoutedEventArgs e) => ApplyManuallyEnteredOutputDirectory();

    private void OutputFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateOutputFormatUi();
    }

    private void SavePathMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplySelectedSavePathMode();
    }

    private void Style_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0 && !IsDwgOutput;
        if (_styleSelectionReady)
        {
            SaveCurrentSettings();
        }
    }

    private void Printer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _printerCombo.SelectedItem is not string printer)
        {
            return;
        }

        _pdfPlotDevice = printer;
        _settings.PdfPrinter = printer;
        SaveCurrentSettings();
        RefreshStatus();
        AppendLog("INFO", "Принтер для печати PDF, PNG и JPG: " + printer);
    }

    private void LeaveMargin_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;

        ApplyLeaveMarginSelection(_jobs);
    }

    private void MarginInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }


        ApplyLeaveMarginSelection(_jobs);
    }


    public sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }


    public static void InitMarginCombo(ComboBox combo, int width, double savedValue)
    {
        combo.IsEditable = false;
        combo.Width = width;
        combo.Items.Clear();

        for (var n = 1; n <= 10; n++)
        {
            combo.Items.Add(new MarginOption { Value = n });
            combo.Items.Add(new MarginOption { Value = -n });
        }

        var bestIdx = 0;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is MarginOption opt)
            {
                var diff = Math.Abs(opt.Value - savedValue);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }
        }
        combo.SelectedIndex = bestIdx;
    }


    public static double ReadMarginValue(ComboBox combo)
        => combo.SelectedItem is MarginOption opt ? opt.Value : 1.0;
}


public sealed class RowIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is int index && index >= 0 ? index + 1 : 0).ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>

/// </summary>
public sealed class DuplicateBrushConverter : IValueConverter
{
    public static HashSet<string>? DrawingNumbers;
    public static HashSet<string>? Titles;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrEmpty(text) || parameter as string != "DrawingNumber" && parameter as string != "Title")
        {
            return Brushes.Black;
        }

        var duplicates = parameter as string == "DrawingNumber" ? DrawingNumbers : Titles;
        if (text is null || duplicates is null)
        {
            return Brushes.Black;
        }

        return duplicates.Contains(text) ? Brushes.Red : Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
