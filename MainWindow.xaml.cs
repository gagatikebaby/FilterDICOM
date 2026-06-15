using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace FilterDICOM
{
    public partial class MainWindow : Window
    {
        private static readonly Dictionary<string, string[]> BodyPartAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Head"] = ["HEAD", "BRAIN"],
            ["Neck"] = ["NECK", "CSPINE"],
            ["Chest"] = ["CHEST", "THORAX"],
            ["Abdomen"] = ["ABDOMEN"],
            ["Pelvis"] = ["PELVIS"],
            ["Spine"] = ["SPINE", "CSPINE", "TSPINE", "LSPINE"],
            ["Upper Limb"] = ["ARM", "UPPER EXTREMITY"],
            ["Lower Limb"] = ["LEG", "LOWER EXTREMITY"],
            ["Heart"] = ["HEART", "CARDIAC"],
        };

        public MainWindow()
        {
            InitializeComponent();
        }

        private void BrowseInputButton_Click(object sender, RoutedEventArgs e)
        {
            BrowseFolder(InputPathTextBox);
        }

        private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
        {
            BrowseFolder(OutputPathTextBox);
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
            StatusTextBlock.Text = "Processing...";
            StartButton.IsEnabled = false;

            try
            {
                var criteria = ReadCriteria();
                var inputPath = InputPathTextBox.Text.Trim();
                var outputPath = OutputPathTextBox.Text.Trim();

                if (!Directory.Exists(inputPath))
                {
                    MessageBox.Show("The input folder does not exist.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    MessageBox.Show("Please select or enter an output folder.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (IsSameOrChildPath(inputPath, outputPath))
                {
                    MessageBox.Show("The output folder cannot be the input folder or a folder inside it.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Directory.CreateDirectory(outputPath);

                var progress = new Progress<string>(AppendLog);
                var result = await Task.Run(() => FilterStudies(inputPath, outputPath, criteria, progress));
                StatusTextBlock.Text = $"Done: matched {result.MatchedCount} / {result.TotalCount}";
                AppendLog($"Done. Checked {result.TotalCount} study folders and exported {result.MatchedCount}.");
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusTextBlock.Text = "Invalid input";
            }
            catch (Exception ex)
            {
                AppendLog($"Error: {ex.Message}");
                StatusTextBlock.Text = "Failed";
            }
            finally
            {
                StartButton.IsEnabled = true;
            }
        }

        private static void BrowseFolder(TextBox target)
        {
            var dialog = new OpenFolderDialog
            {
                Multiselect = false,
                Title = "Select Folder"
            };

            if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
            {
                dialog.InitialDirectory = target.Text;
            }

            if (dialog.ShowDialog() == true)
            {
                target.Text = dialog.FolderName;
            }
        }

        private FilterCriteria ReadCriteria()
        {
            var sex = PatientSexComboBox.SelectedItem is ComboBoxItem sexItem
                ? Convert.ToString(sexItem.Tag, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;

            return new FilterCriteria(
                PatientNameTextBox.Text.Trim(),
                sex.Trim(),
                ReadSelectedBodyParts(),
                ReadSelectedAgeRanges());
        }

        private string[] ReadSelectedBodyParts()
        {
            return BodyPartListBox.SelectedItems
                .OfType<ListBoxItem>()
                .Select(item => Convert.ToString(item.Content, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private AgeRange[] ReadSelectedAgeRanges()
        {
            var ranges = AgeRangeListBox.SelectedItems
                .OfType<ListBoxItem>()
                .Select(item => Convert.ToString(item.Tag, CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(value => value.Length > 0)
                .Select(AgeRange.ParseRequired)
                .ToList();

            ranges.AddRange(AgeRange.ParseMany(CustomAgeTextBox.Text.Trim()));
            return ranges
                .Distinct()
                .ToArray();
        }

        private FilterResult FilterStudies(string inputPath, string outputPath, FilterCriteria criteria, IProgress<string> progress)
        {
            var total = 0;
            var matched = 0;
            var studyFolders = EnumerateCandidateStudyFolders(inputPath);

            foreach (var studyFolder in studyFolders)
            {
                total++;
                var studyName = Path.GetFileName(studyFolder);
                progress.Report($"[{total}] Study: {studyName}");

                try
                {
                    var seriesFolders = Directory.GetDirectories(studyFolder)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Take(2)
                        .ToArray();

                    if (seriesFolders.Length < 2)
                    {
                        progress.Report("  Skipped: fewer than two series folders.");
                        continue;
                    }

                    var firstImage = TryReadFirstDicom(seriesFolders[0], progress);
                    var secondImage = TryReadFirstDicom(seriesFolders[1], progress);

                    if (firstImage is null || secondImage is null)
                    {
                        progress.Report("  Skipped: a readable first DICOM image was not found in both first series folders.");
                        continue;
                    }

                    if (Matches(firstImage.Value, criteria) && Matches(secondImage.Value, criteria))
                    {
                        var destination = BuildOutputPath(inputPath, outputPath, studyFolder);
                        CopyDirectory(studyFolder, destination);
                        matched++;
                        progress.Report($"  Matched: exported to {destination}");
                    }
                    else
                    {
                        progress.Report("  Not matched: at least one first image does not satisfy the filters.");
                    }
                }
                catch (Exception ex)
                {
                    progress.Report($"  Skipped: {ex.Message}");
                }
            }

            return new FilterResult(total, matched);
        }

        private static DicomMetadata? TryReadFirstDicom(string seriesFolder, IProgress<string> progress)
        {
            foreach (var filePath in Directory.EnumerateFiles(seriesFolder).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var metadata = DicomMetadataReader.Read(filePath);
                    progress.Report($"  First image: {Path.GetFileName(seriesFolder)}\\{Path.GetFileName(filePath)}");
                    return metadata;
                }
                catch
                {
                    // Continue until a readable DICOM file is found in this sequence folder.
                }
            }

            progress.Report($"  No readable DICOM found: {seriesFolder}");
            return null;
        }

        private static IEnumerable<string> EnumerateCandidateStudyFolders(string inputPath)
        {
            return Directory.EnumerateDirectories(inputPath, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildOutputPath(string inputPath, string outputPath, string studyFolder)
        {
            var relativePath = Path.GetRelativePath(inputPath, studyFolder);
            if (relativePath == "." || relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                relativePath = Path.GetFileName(studyFolder);
            }

            return Path.Combine(outputPath, relativePath);
        }

        private static bool Matches(DicomMetadata metadata, FilterCriteria criteria)
        {
            if (!string.IsNullOrWhiteSpace(criteria.PatientName) &&
                !DicomTextEquals(metadata.PatientName, criteria.PatientName))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(criteria.PatientSex) &&
                !DicomTextEquals(metadata.PatientSex, criteria.PatientSex))
            {
                return false;
            }

            if (criteria.BodyParts.Length > 0 &&
                !criteria.BodyParts.Any(bodyPart => BodyPartMatches(metadata.BodyPartExamined, bodyPart)))
            {
                return false;
            }

            if (criteria.AgeRanges.Length > 0)
            {
                var age = ParseDicomAge(metadata.PatientAge);
                if (!age.HasValue || !criteria.AgeRanges.Any(range => range.Contains(age.Value)))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BodyPartMatches(string actual, string expected)
        {
            if (DicomTextEquals(actual, expected))
            {
                return true;
            }

            if (BodyPartAliases.TryGetValue(expected, out var aliases))
            {
                return aliases.Any(alias => DicomTextEquals(actual, alias));
            }

            return false;
        }

        private static bool DicomTextEquals(string actual, string expected)
        {
            return string.Equals(NormalizeDicomText(actual), NormalizeDicomText(expected), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDicomText(string value)
        {
            return value.Trim().Replace('^', ' ');
        }

        private static int? ParseDicomAge(string patientAge)
        {
            var value = patientAge.Trim();
            if (value.Length == 0)
            {
                return null;
            }

            var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            var unit = value.Length > digits.Length ? char.ToUpperInvariant(value[digits.Length]) : 'Y';
            return unit switch
            {
                'Y' => number,
                'M' => number / 12,
                'W' => number / 52,
                'D' => number / 365,
                _ => number
            };
        }

        private static void CopyDirectory(string source, string destination)
        {
            var sourceFullPath = Path.GetFullPath(source);
            var destinationFullPath = Path.GetFullPath(destination);
            if (string.Equals(sourceFullPath.TrimEnd(Path.DirectorySeparatorChar), destinationFullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The output folder is the same as the source study folder.");
            }

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relativePath));
            }

            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(source, file);
                var targetFile = Path.Combine(destination, relativePath);
                var targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(file, targetFile, overwrite: true);
            }
        }

        private static bool IsSameOrChildPath(string parentPath, string candidatePath)
        {
            var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private void AppendLog(string message)
        {
            LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        }
    }

    internal readonly record struct FilterCriteria(string PatientName, string PatientSex, string[] BodyParts, AgeRange[] AgeRanges);

    internal readonly record struct FilterResult(int TotalCount, int MatchedCount);

    internal readonly record struct DicomMetadata(string PatientName, string PatientSex, string BodyPartExamined, string PatientAge);

    internal readonly record struct AgeRange(int Min, int Max)
    {
        public bool Contains(int value) => value >= Min && value <= Max;

        public static AgeRange? Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var normalized = input.Replace('－', '-').Replace('—', '-').Trim();
            var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var age))
            {
                if (age < 0)
                {
                    throw new ArgumentException("Age cannot be less than 0.");
                }

                return new AgeRange(age, age);
            }

            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
            {
                if (min < 0 || max < 0 || min > max)
                {
                    throw new ArgumentException("The age range is invalid. Use a format like 30-50.");
                }

                return new AgeRange(min, max);
            }

            throw new ArgumentException("The age format is invalid. Enter 30 or 30-50.");
        }

        public static AgeRange ParseRequired(string input)
        {
            return Parse(input) ?? throw new ArgumentException("The age format is invalid. Enter 30 or 30-50.");
        }

        public static IEnumerable<AgeRange> ParseMany(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return [];
            }

            return input
                .Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseRequired);
        }
    }

    internal static class DicomMetadataReader
    {
        private static readonly Dictionary<uint, Action<DicomMetadataBuilder, string>> TagSetters = new()
        {
            [0x00100010] = (builder, value) => builder.PatientName = value,
            [0x00100040] = (builder, value) => builder.PatientSex = value,
            [0x00101010] = (builder, value) => builder.PatientAge = value,
            [0x00180015] = (builder, value) => builder.BodyPartExamined = value,
        };

        private static readonly HashSet<string> LongValueRepresentations = ["OB", "OD", "OF", "OL", "OW", "SQ", "UC", "UR", "UT", "UN"];

        public static DicomMetadata Read(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

            if (stream.Length < 8)
            {
                throw new InvalidDataException("The file is too small to be read as DICOM.");
            }

            if (stream.Length > 132)
            {
                stream.Seek(128, SeekOrigin.Begin);
                var marker = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (marker != "DICM")
                {
                    stream.Seek(0, SeekOrigin.Begin);
                }
            }

            var builder = new DicomMetadataBuilder();
            while (stream.Position + 8 <= stream.Length && !builder.IsComplete)
            {
                var group = reader.ReadUInt16();
                var element = reader.ReadUInt16();
                var tag = ((uint)group << 16) | element;

                if (group > 0x0018 && builder.HasAnyValue)
                {
                    break;
                }

                var vrOrLengthBytes = reader.ReadBytes(2);
                var vr = Encoding.ASCII.GetString(vrOrLengthBytes);
                uint length;
                if (IsValueRepresentation(vr))
                {
                    if (LongValueRepresentations.Contains(vr))
                    {
                        _ = reader.ReadUInt16();
                        length = reader.ReadUInt32();
                    }
                    else
                    {
                        length = reader.ReadUInt16();
                    }
                }
                else
                {
                    var nextLengthBytes = reader.ReadBytes(2);
                    length = BitConverter.ToUInt32([vrOrLengthBytes[0], vrOrLengthBytes[1], nextLengthBytes[0], nextLengthBytes[1]]);
                }

                if (length == 0xFFFFFFFF)
                {
                    throw new InvalidDataException("DICOM elements with undefined length are not supported.");
                }

                if (length > stream.Length - stream.Position)
                {
                    throw new InvalidDataException("The DICOM element length exceeds the file range.");
                }

                if (TagSetters.TryGetValue(tag, out var setValue))
                {
                    var valueBytes = reader.ReadBytes(checked((int)length));
                    var value = DecodeDicomText(valueBytes);
                    setValue(builder, value);
                }
                else
                {
                    stream.Seek(length, SeekOrigin.Current);
                }
            }

            if (!builder.HasAnyValue)
            {
                throw new InvalidDataException("No DICOM tags required for filtering were found.");
            }

            return builder.Build();
        }

        private static string DecodeDicomText(byte[] bytes)
        {
            return Encoding.Default.GetString(bytes).Trim('\0', ' ');
        }

        private static bool IsValueRepresentation(string value)
        {
            return value.Length == 2 && value.All(ch => ch is >= 'A' and <= 'Z');
        }

        private sealed class DicomMetadataBuilder
        {
            public string PatientName { get; set; } = string.Empty;
            public string PatientSex { get; set; } = string.Empty;
            public string BodyPartExamined { get; set; } = string.Empty;
            public string PatientAge { get; set; } = string.Empty;

            public bool HasAnyValue =>
                PatientName.Length > 0 ||
                PatientSex.Length > 0 ||
                BodyPartExamined.Length > 0 ||
                PatientAge.Length > 0;

            public bool IsComplete =>
                PatientName.Length > 0 &&
                PatientSex.Length > 0 &&
                BodyPartExamined.Length > 0 &&
                PatientAge.Length > 0;

            public DicomMetadata Build() => new(PatientName, PatientSex, BodyPartExamined, PatientAge);
        }
    }
}
