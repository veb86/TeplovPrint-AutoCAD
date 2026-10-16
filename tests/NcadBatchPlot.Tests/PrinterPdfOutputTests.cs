using System.Reflection;
using System.Text;
using Xunit;

namespace NcadBatchPlot.Tests;

/// <summary>
/// Issue #15: printing in nanoCAD with the PDFCreator printer failed with "The file is not a valid PDF document"
/// (PdfSharp in PlotterService.ValidatePlotOutput). Plot.PlotToFile with a Windows printer writes what the printer
/// driver produces, and virtual PDF printers (PDFCreator, Adobe PDF, ...) have PostScript drivers: the ".pdf" file
/// was PostScript with a PJL header. It is converted to PDF with Ghostscript (bundled with PDFCreator).
/// </summary>
public class PrinterPdfOutputTests
{
    private const string OutputType = "ZwcadBatchPlot.PrinterPdfOutput";

    /// <summary>A landscape A4 page as the PostScript printer driver (pscript5) spools it: PJL header and trailer.</summary>
    internal static readonly byte[] PrinterPostScript = Encoding.ASCII.GetBytes(
        "\u001B%-12345X@PJL JOB\r\n"
        + "@PJL SET RESOLUTION = 600\r\n"
        + "@PJL ENTER LANGUAGE = POSTSCRIPT\r\n"
        + "%!PS-Adobe-3.0\r\n"
        + "%%Title: 00001.dwg\r\n"
        + "%%Creator: PScript5.dll Version 5.2.2\r\n"
        + "%%Pages: 1\r\n"
        + "%%EndComments\r\n"
        + "%%Page: 1 1\r\n"
        + "<< /PageSize [842 595] >> setpagedevice\r\n"
        + "0.5 setlinewidth newpath 36 36 moveto 806 36 lineto 806 559 lineto 36 559 lineto closepath stroke\r\n"
        + "showpage\r\n"
        + "%%Trailer\r\n"
        + "%%EOF\r\n"
        + "\u001B%-12345X@PJL EOJ\r\n"
        + "\u001B%-12345X");

    [Fact]
    public void PdfIsDetected()
    {
        Assert.Equal("Pdf", DetectFormat(Encoding.ASCII.GetBytes("%PDF-1.7\n%âãÏÓ\n1 0 obj")));
    }

    [Fact]
    public void PostScriptOfThePrinterDriverIsDetected()
    {
        Assert.Equal("PostScript", DetectFormat(PrinterPostScript));
        Assert.Equal("PostScript", DetectFormat(Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\n%%Pages: 1\n")));
    }

    [Fact]
    public void PdfFileThatEmbedsPostScriptTextIsStillPdf()
    {
        Assert.Equal("Pdf", DetectFormat(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj (%!PS-Adobe-3.0) endobj")));
    }

    [Theory]
    // PCL of a laser printer, XPS (a ZIP) of Microsoft XPS Document Writer, an empty file.
    [InlineData("\u001BE\u001B&l1O\u001B*t600R")]
    [InlineData("PK\u0003\u0004")]
    [InlineData("")]
    public void OtherPrinterDataIsNotPdf(string head)
    {
        Assert.Equal("Unknown", DetectFormat(Encoding.ASCII.GetBytes(head)));
    }

    [Fact]
    public void PjlHeaderAndTrailerAreRemovedFromThePostScript()
    {
        var postScript = Encoding.ASCII.GetString(ExtractPostScript(PrinterPostScript));

        Assert.StartsWith("%!PS-Adobe-3.0", postScript);
        Assert.EndsWith("%%EOF\r\n", postScript);
        Assert.DoesNotContain("@PJL", postScript);
    }

    [Theory]
    [InlineData("gswin64c.exe")]
    [InlineData("gswin32c.exe")]
    public void GhostscriptBundledWithPdfCreatorIsFound(string executable)
    {
        var pdfCreator = Directory.CreateTempSubdirectory("teplov-pdfcreator-").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(pdfCreator, "Ghostscript", "Bin")).FullName;
            Directory.CreateDirectory(Path.Combine(pdfCreator, "Ghostscript", "Lib"));
            File.WriteAllText(Path.Combine(bin, executable), "");

            Assert.Equal(Path.Combine(bin, executable), FindPdfCreatorGhostscript(pdfCreator));
        }
        finally
        {
            Directory.Delete(pdfCreator, true);
        }
    }

    [Fact]
    public void PdfCreatorWithoutGhostscriptIsSkipped()
    {
        var pdfCreator = Directory.CreateTempSubdirectory("teplov-pdfcreator-").FullName;
        try
        {
            Assert.Null(FindPdfCreatorGhostscript(pdfCreator));
            Assert.Null(FindPdfCreatorGhostscript(Path.Combine(pdfCreator, "missing")));
        }
        finally
        {
            Directory.Delete(pdfCreator, true);
        }
    }

    [Fact]
    public void ConfiguredGhostscriptMayBeAFileOrAFolder()
    {
        var root = Directory.CreateTempSubdirectory("teplov-gs-").FullName;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var executable = Path.Combine(bin, "gswin64c.exe");
            File.WriteAllText(executable, "");

            Assert.Equal(executable, ResolveConfiguredGhostscript("\"" + executable + "\""));
            Assert.Equal(executable, ResolveConfiguredGhostscript(bin));
            Assert.Equal(executable, ResolveConfiguredGhostscript(root));
            Assert.Null(ResolveConfiguredGhostscript(Path.Combine(root, "missing.exe")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(@"C:\PROGRA~1\gs\bin\gswin64c.exe", @"C:\PROGRA~1\gs\bin\gswin64c.exe")]
    [InlineData(@"-sOutputFile=C:\Печать PDF\a.pdf", @"""-sOutputFile=C:\Печать PDF\a.pdf""")]
    [InlineData(@"C:\a b\", @"""C:\a b\\""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData("", @"""""")]
    public void ArgumentsAreQuotedForTheWindowsCommandLine(string argument, string expected)
    {
        Assert.Equal(expected, Invoke<string>("QuoteArgument", argument));
    }

    [Fact]
    public void PdfOutputIsLeftAsIs()
    {
        WithOutput(Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"), path =>
        {
            EnsurePdf(path, "Internal PDF Plotter", () => throw new InvalidOperationException("Ghostscript is not needed for a PDF."));
            Assert.Equal("%PDF-1.4\n%%EOF\n", File.ReadAllText(path));
        });
    }

    [Fact]
    public void PostScriptWithoutGhostscriptIsReportedWithGuidance()
    {
        WithOutput(PrinterPostScript, path =>
        {
            var error = Assert.IsType<InvalidDataException>(Unwrap(() => EnsurePdf(path, "PDFCreator", () => null)));

            Assert.Contains("PDFCreator", error.Message);
            Assert.Contains("PostScript", error.Message);
            Assert.Contains("Ghostscript", error.Message);
            Assert.Contains("TEPLOV_PRINT_GHOSTSCRIPT", error.Message);
        });
    }

    [Fact]
    public void UnknownPrinterDataIsReportedWithGuidance()
    {
        WithOutput(Encoding.ASCII.GetBytes("\u001BE\u001B&l1O\u001B*t600R"), path =>
        {
            var error = Assert.IsType<InvalidDataException>(Unwrap(() => EnsurePdf(path, "HP LaserJet", () => null)));

            Assert.Contains("HP LaserJet", error.Message);
            Assert.Contains("не PDF", error.Message);
        });
    }

    /// <summary>
    /// The real conversion: needs Ghostscript (PDFCreator, GPL Ghostscript, TEPLOV_PRINT_GHOSTSCRIPT, or gs on PATH).
    /// CI installs Ghostscript and sets TEPLOV_TEST_REQUIRE_GHOSTSCRIPT=1, so there the test cannot be skipped silently.
    /// </summary>
    [Fact]
    public void PrinterPostScriptIsConvertedToPdfInPlace()
    {
        var ghostscript = Invoke<string?>("FindGhostscript") ?? FindOnPath("gs");
        if (ghostscript == null)
        {
            Assert.True(Environment.GetEnvironmentVariable("TEPLOV_TEST_REQUIRE_GHOSTSCRIPT") != "1", "Ghostscript was not found.");
            return;
        }

        // A Cyrillic folder with a space, as the CAD output folders usually are.
        var directory = Path.Combine(Directory.CreateTempSubdirectory("teplov-gs-").FullName, "Печать PDF");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "00001 План.pdf");
            File.WriteAllBytes(path, PrinterPostScript);

            EnsurePdf(path, "PDFCreator", () => ghostscript);

            var pdf = File.ReadAllBytes(path);
            Assert.Equal("Pdf", DetectFormat(pdf));
            var text = Encoding.Latin1.GetString(pdf);
            Assert.Matches(@"/Type\s*/Page\b", text);
            // The sheet keeps the orientation the CAD plotted (-dAutoRotatePages=/None).
            Assert.Matches(@"/MediaBox\s*\[\s*0 0 842 595\s*\]", text);
            // Only the requested file is left: the temporary ASCII copies are removed.
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, true);
        }
    }

    [Fact]
    public void PlotDatabaseConvertsPrinterOutputBeforeTheValidation()
    {
        var graph = new PluginCallGraph(TestPaths.PluginPath);

        Assert.True(graph.FindPath("ZwcadBatchPlot.PlotterService::PlotDatabase", OutputType + "::EnsurePdf") != null,
            "PlotDatabase does not convert the printer output: PostScript of PDFCreator fails the PDF validation.");
    }

    private static void WithOutput(byte[] content, Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("teplov-printer-output-").FullName;
        try
        {
            var path = Path.Combine(directory, "00001.pdf");
            File.WriteAllBytes(path, content);
            test(path);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static Exception? Unwrap(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException;
        }
    }

    private static string? FindOnPath(string name)
    {
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static string DetectFormat(byte[] head)
        => Output().GetMethod("DetectFormat", new[] { typeof(byte[]) })!.Invoke(null, new object[] { head })!.ToString()!;

    private static byte[] ExtractPostScript(byte[] data) => Invoke<byte[]>("ExtractPostScript", data);

    private static string? FindPdfCreatorGhostscript(string directory) => Invoke<string?>("FindPdfCreatorGhostscript", directory);

    private static string? ResolveConfiguredGhostscript(string configured) => Invoke<string?>("ResolveConfiguredGhostscript", configured);

    private static void EnsurePdf(string path, string device, Func<string?> findGhostscript)
        => Output().GetMethod("EnsurePdf", new[] { typeof(string), typeof(string), typeof(Func<string?>) })!
            .Invoke(null, new object?[] { path, device, findGhostscript });

    private static T Invoke<T>(string method, params object[] arguments)
    {
        var types = arguments.Select(argument => argument.GetType()).ToArray();
        return (T)Output().GetMethod(method, BindingFlags.Public | BindingFlags.Static, types)!.Invoke(null, arguments)!;
    }

    /// <summary>PrinterPdfOutput has no host dependencies, so it runs without nanoCAD.</summary>
    private static Type Output() => NanoCadPrintRouteTests.PluginType(OutputType);
}
