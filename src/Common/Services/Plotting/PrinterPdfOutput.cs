using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

/// <summary>What a plotter wrote into the plot file.</summary>
public enum PlotFileFormat
{
    Unknown = 0,
    Pdf = 1,
    PostScript = 2
}

/// <summary>
/// Issue #15: a Windows printer chosen for PDF (PDFCreator, Adobe PDF, ...) ignores the PDF meaning of Plot.PlotToFile:
/// the CAD prints "to file" through the printer driver, and the file gets the printer data of the driver instead of
/// a PDF. Virtual PDF printers have PostScript drivers (PDFCreator itself converts the PostScript with Ghostscript),
/// so the plot file is PostScript and PdfSharp failed with "The file is not a valid PDF document". The PostScript is
/// converted to PDF with Ghostscript (the copy bundled with PDFCreator, an installed GPL Ghostscript or
/// TEPLOV_PRINT_GHOSTSCRIPT); other printer data is reported with a clear message.
/// No host dependencies, so it is unit-tested without the CAD.
/// </summary>
public static class PrinterPdfOutput
{
    /// <summary>Path to gswin64c.exe (or its folder) used to convert PostScript, e.g. TEPLOV_PRINT_GHOSTSCRIPT=C:\gs\bin\gswin64c.exe.</summary>
    public const string GhostscriptEnvironmentVariable = "TEPLOV_PRINT_GHOSTSCRIPT";

    /// <summary>The PDF header may follow up to 1024 bytes of garbage (PDF 1.7, Annex H); PJL headers before PostScript are short.</summary>
    private const int HeadLength = 64 * 1024;

    private const int PdfHeaderWindow = 1024;
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);
    private static readonly byte[] PdfMarker = Encoding.ASCII.GetBytes("%PDF-");
    private static readonly byte[] PostScriptMarker = Encoding.ASCII.GetBytes("%!PS");

    /// <summary>PJL "universal exit language" that printer drivers put before and after the PostScript job.</summary>
    private static readonly byte[] UniversalExit = Encoding.ASCII.GetBytes("\u001B%-12345X");

    private static readonly string[] GhostscriptExecutables = { "gswin64c.exe", "gswin32c.exe" };
    private static readonly string[] PdfCreatorRegistryKeys = { @"SOFTWARE\pdfforge\PDFCreator\Program", @"SOFTWARE\pdfforge\PDFCreator Server\Program" };

    /// <summary>Format of the plot file at <paramref name="path"/> by its first bytes.</summary>
    public static PlotFileFormat DetectFormat(string path)
    {
        return DetectFormat(ReadHead(path));
    }

    public static PlotFileFormat DetectFormat(byte[] head)
    {
        var pdf = IndexOf(head, PdfMarker, 0, Math.Min(head.Length, PdfHeaderWindow + PdfMarker.Length));
        var postScript = IndexOf(head, PostScriptMarker, 0, head.Length);
        if (pdf >= 0 && (postScript < 0 || pdf < postScript))
        {
            return PlotFileFormat.Pdf;
        }

        return postScript >= 0 ? PlotFileFormat.PostScript : PlotFileFormat.Unknown;
    }

    /// <summary>
    /// Makes sure the PDF plot file <paramref name="outputPath"/> is a PDF: PostScript written by a Windows printer is
    /// converted with Ghostscript in place. Other files, missing and empty files are left to the output validation.
    /// </summary>
    public static void EnsurePdf(string outputPath, string deviceName)
    {
        EnsurePdf(outputPath, deviceName, FindGhostscript);
    }

    /// <summary><see cref="EnsurePdf(string, string)"/> with the Ghostscript search given by the caller (tests).</summary>
    public static void EnsurePdf(string outputPath, string deviceName, Func<string?> findGhostscript)
    {
        if (!string.Equals(Path.GetExtension(outputPath), ".pdf", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(outputPath)
            || new FileInfo(outputPath).Length == 0)
        {
            return;
        }

        var head = ReadHead(outputPath);
        var format = DetectFormat(head);
        PrintSessionLog.Info($"Файл печати: формат={format}; размер={new FileInfo(outputPath).Length}; начало=«{DescribeHead(head)}»; устройство={deviceName}");
        if (format == PlotFileFormat.Pdf)
        {
            return;
        }

        if (format == PlotFileFormat.Unknown)
        {
            throw new InvalidDataException(
                $"Принтер «{deviceName}» записал в файл не PDF, а данные своего драйвера (начало файла: «{DescribeHead(head)}»): {outputPath}. "
                + "Такой принтер не печатает в PDF-файл. Выберите в списке «Принтер» PDF-плоттер nanoCAD "
                + "или PDF-принтер с драйвером PostScript (например, PDFCreator).");
        }

        var ghostscript = findGhostscript();
        if (ghostscript == null)
        {
            throw new InvalidDataException(
                $"Принтер «{deviceName}» записал в файл PostScript, а не PDF: {outputPath}. "
                + "Для преобразования в PDF нужен Ghostscript (он входит в PDFCreator), но он не найден. "
                + $"Укажите путь к gswin64c.exe в переменной окружения {GhostscriptEnvironmentVariable} до запуска CAD, "
                + "установите GPL Ghostscript или выберите в списке «Принтер» PDF-плоттер nanoCAD.");
        }

        ConvertPostScriptToPdf(ghostscript, outputPath);
    }

    /// <summary>
    /// Converts the PostScript file <paramref name="path"/> to PDF in place with <paramref name="ghostscript"/>.
    /// Ghostscript works with ASCII names next to the file: the CAD paths often have Cyrillic and spaces.
    /// </summary>
    public static void ConvertPostScriptToPdf(string ghostscript, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetTempPath();
        var token = "teplov-" + Guid.NewGuid().ToString("N");
        var postScriptPath = Path.Combine(directory, token + ".ps");
        var pdfPath = Path.Combine(directory, token + ".pdf");
        try
        {
            File.WriteAllBytes(postScriptPath, ExtractPostScript(File.ReadAllBytes(path)));
            var shortDirectory = ShortPath(directory);
            var arguments = new List<string>
            {
                "-q",
                "-dNOPAUSE",
                "-dBATCH",
                "-dSAFER",
                "-sDEVICE=pdfwrite",
                // The CAD already rotated the sheet on the paper: keep every page as plotted.
                "-dAutoRotatePages=/None"
            };

            arguments.AddRange(LibraryArguments(ghostscript));
            arguments.Add("-sOutputFile=" + Path.Combine(shortDirectory, token + ".pdf"));
            arguments.Add("-f");
            arguments.Add(Path.Combine(shortDirectory, token + ".ps"));

            PrintSessionLog.Step($"Ghostscript: PostScript -> PDF ({ghostscript} {string.Join(" ", arguments)})");
            var (exitCode, output) = Run(ghostscript, arguments);
            if (exitCode != 0 || !File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0 || DetectFormat(pdfPath) != PlotFileFormat.Pdf)
            {
                throw new InvalidDataException(
                    $"Ghostscript не преобразовал PostScript в PDF (код {exitCode}): {path}. {Tail(output)}".TrimEnd());
            }

            File.Delete(path);
            File.Move(pdfPath, path);
            PrintSessionLog.Info($"PostScript преобразован в PDF (Ghostscript): {path}");
        }
        finally
        {
            TryDelete(postScriptPath);
            TryDelete(pdfPath);
        }
    }

    /// <summary>
    /// The PostScript job without the PJL header and trailer of the printer driver: from the first "%!PS" to the
    /// next PJL "universal exit language" (or the end of the file).
    /// </summary>
    public static byte[] ExtractPostScript(byte[] data)
    {
        var start = IndexOf(data, PostScriptMarker, 0, data.Length);
        if (start < 0)
        {
            return data;
        }

        var end = IndexOf(data, UniversalExit, start, data.Length);
        if (end < 0)
        {
            end = data.Length;
        }

        var result = new byte[end - start];
        Array.Copy(data, start, result, 0, result.Length);
        return result;
    }

    /// <summary>
    /// Ghostscript for the PostScript conversion: <see cref="GhostscriptEnvironmentVariable"/>, the copy bundled with
    /// PDFCreator, then the newest installed GPL Ghostscript. Null if none is found.
    /// </summary>
    public static string? FindGhostscript()
    {
        var configured = Environment.GetEnvironmentVariable(GhostscriptEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = ResolveConfiguredGhostscript(configured!);
            if (resolved != null)
            {
                return resolved;
            }

            PrintSessionLog.Write("WARN", $"{GhostscriptEnvironmentVariable}={configured}: Ghostscript не найден по этому пути.");
        }

        if (!IsWindows)
        {
            return null;
        }

        return PdfCreatorDirectories()
                   .Select(FindPdfCreatorGhostscript)
                   .FirstOrDefault(found => found != null)
               ?? InstalledGhostscriptBinDirectories()
                   .Select(FindExecutableIn)
                   .FirstOrDefault(found => found != null);
    }

    /// <summary>A file, or a Ghostscript folder (its bin subfolder is also searched) from <see cref="GhostscriptEnvironmentVariable"/>.</summary>
    public static string? ResolveConfiguredGhostscript(string configured)
    {
        var path = configured.Trim().Trim('"');
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        return Directory.Exists(path)
            ? FindExecutableIn(path) ?? FindExecutableIn(Path.Combine(path, "bin"))
            : null;
    }

    /// <summary>PDFCreator keeps its Ghostscript in &lt;PDFCreator&gt;\Ghostscript\Bin (PDFCreator GhostscriptDiscovery).</summary>
    public static string? FindPdfCreatorGhostscript(string pdfCreatorDirectory)
    {
        return string.IsNullOrWhiteSpace(pdfCreatorDirectory)
            ? null
            : FindExecutableIn(Path.Combine(pdfCreatorDirectory, "Ghostscript", "Bin"));
    }

    private static string? FindExecutableIn(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        return GhostscriptExecutables
            .Select(name => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> PdfCreatorDirectories()
    {
        foreach (var keyName in PdfCreatorRegistryKeys)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var value = ReadRegistryString(RegistryHive.LocalMachine, view, keyName, "ApplicationPath");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value!;
                }
            }
        }

        foreach (var programFiles in ProgramFilesDirectories())
        {
            yield return Path.Combine(programFiles, "PDFCreator");
        }
    }

    /// <summary>bin folders of GPL Ghostscript, the newest version first: the registry (GS_DLL), then Program Files\gs\gs*.</summary>
    private static IEnumerable<string> InstalledGhostscriptBinDirectories()
    {
        var found = new List<(Version Version, string Bin)>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var (version, dll) in ReadGhostscriptRegistry(view))
            {
                var bin = Path.GetDirectoryName(dll);
                if (!string.IsNullOrWhiteSpace(bin))
                {
                    found.Add((version, bin!));
                }
            }
        }

        foreach (var programFiles in ProgramFilesDirectories())
        {
            var root = Path.Combine(programFiles, "gs");
            string[] versions;
            try
            {
                versions = Directory.Exists(root) ? Directory.GetDirectories(root, "gs*") : Array.Empty<string>();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var directory in versions)
            {
                var name = Path.GetFileName(directory).Substring(2);
                found.Add((ParseVersion(name), Path.Combine(directory, "bin")));
            }
        }

        return found.OrderByDescending(item => item.Version).Select(item => item.Bin);
    }

    private static IEnumerable<(Version Version, string Dll)> ReadGhostscriptRegistry(RegistryView view)
    {
        var result = new List<(Version, string)>();
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(@"SOFTWARE\GPL Ghostscript");
            if (root == null)
            {
                return result;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("GS_DLL") is string dll && !string.IsNullOrWhiteSpace(dll))
                {
                    result.Add((ParseVersion(name), dll));
                }
            }
        }
        catch (Exception)
        {
            // No registry access: Program Files is still searched.
        }

        return result;
    }

    private static string? ReadRegistryString(RegistryHive hive, RegistryView view, string keyName, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyName);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> ProgramFilesDirectories()
    {
        return new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Version ParseVersion(string text)
    {
        return Version.TryParse(text, out var version) ? version : new Version(0, 0);
    }

    /// <summary>-I with the Bin and Lib folders of a bundled Ghostscript, as PDFCreator passes them.</summary>
    private static IEnumerable<string> LibraryArguments(string ghostscript)
    {
        var bin = Path.GetDirectoryName(Path.GetFullPath(ghostscript));
        var root = bin == null ? null : Path.GetDirectoryName(bin);
        if (bin == null || root == null)
        {
            yield break;
        }

        var lib = Path.Combine(root, "Lib");
        if (Directory.Exists(lib))
        {
            yield return "-I" + ShortPath(bin) + Path.PathSeparator + ShortPath(lib);
        }
    }

    private static (int ExitCode, string Output) Run(string executable, IReadOnlyList<string> arguments)
    {
        var output = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)ConversionTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
            }

            throw new TimeoutException($"Ghostscript не завершил преобразование PostScript в PDF за {ConversionTimeout.TotalMinutes:0} мин.");
        }

        // Flushes the asynchronous output readers.
        process.WaitForExit();
        var text = output.ToString().Trim();
        if (text.Length > 0)
        {
            PrintSessionLog.Info("Ghostscript: " + text);
        }

        return (process.ExitCode, text);
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules) of one argument.</summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        return builder.Append('"').ToString();
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>The 8.3 name of an existing path (ASCII, no spaces) when Windows has one, otherwise the path itself.</summary>
    private static string ShortPath(string path)
    {
        if (!IsWindows)
        {
            return path;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetShortPathName(path, buffer, buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
        }
        catch (Exception)
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

    private static byte[] ReadHead(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(HeadLength, stream.Length)];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read == buffer.Length)
        {
            return buffer;
        }

        var head = new byte[read];
        Array.Copy(buffer, head, read);
        return head;
    }

    /// <summary>The first printable bytes of the file for the log and the error message.</summary>
    private static string DescribeHead(byte[] head)
    {
        var builder = new StringBuilder();
        foreach (var b in head.Take(48))
        {
            builder.Append(b >= 0x20 && b < 0x7F ? (char)b : b == 0x1B ? '␛' : '·');
        }

        return builder.ToString();
    }

    private static string Tail(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.Skip(Math.Max(0, lines.Length - 5)));
    }

    private static int IndexOf(byte[] data, byte[] marker, int start, int end)
    {
        end = Math.Min(end, data.Length);
        for (var i = Math.Max(0, start); i <= end - marker.Length; i++)
        {
            var match = true;
            for (var j = 0; j < marker.Length; j++)
            {
                if (data[i + j] != marker[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
