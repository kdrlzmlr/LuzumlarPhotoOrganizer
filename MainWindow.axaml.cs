using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Directory = System.IO.Directory;

namespace LuzumlarPhotoOrganizer;

public partial class MainWindow : Window
{
    private string _source = "";
    private string _target = "";
    private readonly StringBuilder _logBuilder = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BrowseSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _source = await BrowseFolder();
        SourceTextBox.Text = _source;
    }

    private async void BrowseTarget_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _target = await BrowseFolder();
        TargetTextBox.Text = _target;
    }

    private async Task<string> BrowseFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select Folder",
                AllowMultiple = false
            });

        return folders.Count > 0 ? folders[0].Path.LocalPath : "";
    }


    private async void Start_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var box = MessageBoxManager
            .GetMessageBoxStandard("Confirmation", "Are you sure you want to start organizing files?", ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Question);
        if (string.IsNullOrEmpty(_source) || !Directory.Exists(_source))
        {
            box = MessageBoxManager
                .GetMessageBoxStandard("Error", "Invalid source folder.", ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Error);

            await box.ShowAsync();

            return;
        }
        if (string.IsNullOrEmpty(_target))
        {
            box = MessageBoxManager
                .GetMessageBoxStandard("Error", "Invalid target folder.", ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Error);
            await box.ShowAsync();

            return;
        }

        Directory.CreateDirectory(_target);
        _logBuilder.Clear();

        StartButton.IsEnabled = false;
        copyormove.IsEnabled = false;
        StatusText.Text = "Scanning files...";

        var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".heic", ".heif", ".mp4", ".avi", ".mov", ".wmv", ".mkv", ".m4v" };
        var files = Directory.EnumerateFiles(_source, "*.*", SearchOption.AllDirectories)
                             .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .ToList();

        if (files.Count == 0)
        {
            box = MessageBoxManager
                .GetMessageBoxStandard("Error", "No supported media files found.", ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Error);
            await box.ShowAsync();
            StartButton.IsEnabled = true;
            copyormove.IsEnabled = true;
            StatusText.Text = "Ready";
            return;
        }

        // === TOTAL PROGRESS SETUP ===
        // Total files to process: all files (each file is hashed AND moved)
        long totalOperations = files.Count * 2L;
        ProgressBar.Minimum = 0;
        ProgressBar.Maximum = totalOperations;
        ProgressBar.Value = 0;


        int uiCounter = 0;

        void UpdateProgressThrottled(string operation)
        {
            if (Interlocked.Increment(ref uiCounter) % 50 == 0)
                UpdateProgress(operation);
        }

        long completedOperations = 0;

        // Helper to update progress
        void UpdateProgress(string operation)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = completedOperations;
                double percent = totalOperations > 0 ? (completedOperations * 100.0 / totalOperations) : 0;
                StatusText.Text = $"{operation} progress: {percent:F1}% ({completedOperations}/{totalOperations} operations)";
            });
        }

        // Optimization: Use incremental hash + larger buffer + parallel processing for small files
        var fileGroups = new Dictionary<string, List<string>>();
        var duplicateFiles = new List<string>();
        long duplicateSizeBytes = 0;

        int processed = 0;

        // Parallel hashing with controlled concurrency (adjust based on CPU/disk)
        int maxDegree = Environment.ProcessorCount / 2;
        if (maxDegree < 1) maxDegree = 1;

        ProgressBar.Foreground = Brushes.Orange;

        await Task.Run(() =>
        {
            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegree },
                filePath =>
                {
                    string hash;
                    long size;

                    try
                    {
                        (hash, size) = ComputeFileHashFastAsync(filePath).Result;
                    }
                    catch (Exception ex)
                    {
                        lock (_logBuilder)
                            _logBuilder.AppendLine($"[ERROR] Hashing failed for {filePath}: {ex.Message}");

                        hash = Guid.NewGuid().ToString();
                        size = 0;
                    }

                    lock (fileGroups)
                    {
                        string key = $"{hash}|{size}";
                        if (!fileGroups.ContainsKey(key))
                            fileGroups[key] = new List<string>();

                        fileGroups[key].Add(filePath);

                        if (fileGroups[key].Count > 1)
                        {
                            lock (duplicateFiles)
                            {
                                duplicateFiles.Add(filePath);
                                duplicateSizeBytes += size;
                            }
                        }
                    }

                    Interlocked.Increment(ref completedOperations);
                    UpdateProgressThrottled("Hashing");
                });
        });

        // Phase 2: Moving or copying files
        StatusText.Text = "Organizing files...";
        ProgressBar.Foreground = Brushes.LimeGreen;

        string dupeDir = Path.Combine(_target, "Duplicates");
        Directory.CreateDirectory(dupeDir);

        int organized = 0;
        int duplicates = duplicateFiles.Count;

        await Task.Run(async () =>
        {
            Parallel.ForEach(fileGroups.Values, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async group =>
                {
                    var toKeepPath = group.First();
                    var date = GetDateTaken(toKeepPath);
                    string year = date.ToString("yyyy");
                    string month = date.ToString("MMMM");

                    string yearDir = Path.Combine(_target, year);
                    string destDir = Path.Combine(yearDir, month);
                    Directory.CreateDirectory(destDir);

                    var fileName = Path.GetFileName(toKeepPath);
                    var destPath = GetUniquePath(Path.Combine(destDir, fileName));

                    try
                    {
                        if (copyormove.IsChecked == true)
                            File.Move(toKeepPath, destPath);
                        else
                            File.Copy(toKeepPath, destPath);

                        organized++;
                    }
                    catch (Exception ex)
                    {
                        _logBuilder.AppendLine($"[ERROR] Failed to process {toKeepPath} -> {destPath}: {ex.Message}");
                    }

                    // Count unique file
                    Interlocked.Increment(ref completedOperations);
                    UpdateProgressThrottled(copyormove.IsChecked == true ? "Moving" : "Copying");

                    // Process duplicates
                    foreach (var dupePath in group.Skip(1))
                    {
                        var dupeDest = GetUniquePath(Path.Combine(dupeDir, Path.GetFileName(dupePath)));

                        try
                        {
                            if (copyormove.IsChecked == true)
                                File.Move(dupePath, dupeDest);
                            else
                                File.Copy(dupePath, dupeDest);
                        }
                        catch (Exception ex)
                        {
                            _logBuilder.AppendLine($"[ERROR] Failed to process duplicate {dupePath} -> {dupeDest}: {ex.Message}");
                        }

                        // Count duplicate file
                        Interlocked.Increment(ref completedOperations);
                        UpdateProgressThrottled(copyormove.IsChecked == true ? "Moving" : "Copying");
                    }

                    await Task.Delay(1); // allow UI refresh
                });

            double duplicateGB = duplicateSizeBytes / (1024.0 * 1024.0 * 1024.0);

            // Final report
            string summary = $"Organization Complete!\n\n" +
                            $"Unique files moved: {organized}\n" +
                            $"Duplicates found: {duplicates}\n" +
                            $"Space saved: {duplicateGB:F2} GB\n\n" +
                            $"Duplicates are in: {_target}\\Duplicates\n";

            if (_logBuilder.Length > 0)
            {
                summary += "\nSome errors occurred during processing. See log below.\n\n";
            }

            StatusText.Text = "Done!";
            StartButton.IsEnabled = true;
            copyormove.IsEnabled = true;


            box = MessageBoxManager
                .GetMessageBoxStandard("Success", summary, ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Success);
            await box.ShowAsync();

            // Show duplicates list and log if any
            if (duplicates > 0 || _logBuilder.Length > 0)
            {
                var report = new StringBuilder();
                report.AppendLine("=== DUPLICATE FILES ===");
                foreach (var d in duplicateFiles)
                    report.AppendLine(d);

                report.AppendLine($"\nTotal duplicates: {duplicates} ({duplicateGB:F2} GB)");

                if (_logBuilder.Length > 0)
                {
                    report.AppendLine("\n=== ERROR LOG ===");
                    report.Append(_logBuilder.ToString());
                }

                var logWindow = new Window
                {
                    Title = "Duplicates & Log Report",
                    Width = 800,
                    Height = 600,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };
                var textBox = new TextBox
                {
                    Text = report.ToString(),
                    FontFamily = FontFamily.Parse("Consolas"),
                    FontSize = 12,
                    IsReadOnly = true,
                    Margin = new Thickness(10)
                };

                var ownerWindow = this;
                logWindow.Content = textBox;
                await logWindow.ShowDialog(this);
            }
        });
    }
    // Optimized fast hashing: larger buffer + incremental + no extra allocations
    private static async Task<(string hash, long size)> ComputeFileHashFastAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);

            byte[] buffer = new byte[1024 * 1024];
            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha256.AppendData(buffer, 0, bytesRead);

            string hash = BitConverter.ToString(sha256.GetHashAndReset()).Replace("-", "").ToLowerInvariant();
            return (hash, stream.Length);
        });
    }

    private DateTime GetDateTaken(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);

            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd?.TryGetDateTime(ExifSubIfdDirectory.TagDateTimeOriginal, out var exifDt) == true)
                return exifDt;

            var movieHeader = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            if (movieHeader?.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var movieDt) == true)
                return movieDt;

            var trackHeader = directories.OfType<QuickTimeTrackHeaderDirectory>().FirstOrDefault();
            if (trackHeader?.TryGetDateTime(QuickTimeTrackHeaderDirectory.TagCreated, out var trackDt) == true)
                return trackDt;
        }
        catch (Exception ex)
        {
            _logBuilder.AppendLine($"[WARNING] Metadata read failed for {filePath}: {ex.Message}");
        }

        return new FileInfo(filePath).CreationTime;
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path);

        if (String.IsNullOrEmpty(dir))
            throw new InvalidOperationException("Directory cannot be determined for the given path.");
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int i = 1;

        while (true)
        {
            var newPath = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(newPath)) return newPath;
            i++;
        }
    }
}